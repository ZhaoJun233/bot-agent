#!/usr/bin/env python3
"""云端 TTS 旁路服务（替代原来的 Piper 容器）：文本 → 音频，一个 URL 搞定。

为什么要这层：机器人要的是「给一段文本、拿一段音频」的 HTTP 调用，而各家云 TTS 的
proto 各不相同（MiniMax 走 /v1/t2a_v2 且返回 **hex**、OpenAI 系走 /v1/audio/speech
且直接吐二进制）。包一层之后：
  • 机器人/协议端只依赖 /speak?text=…&voice=…&speed=… 这一个稳定契约（老 Piper 那套原样保留）；
  • 换厂商 = 改环境变量，不动机器人一行代码；
  • 同一句话第二次要，直接吃磁盘缓存，不重复花钱、也不重复等。

为什么不用 Piper（本地 ONNX）了：1 核 1G 的机器上，Piper 那 490MB 镜像 + 常驻 200MB 内存
＋ 每句话几百毫秒到几秒的 CPU 推理，性价比远不如云端（音色也更自然）。
本服务常驻内存 ~20MB，且**不加载任何模型**。

契约（与 tools/tts-server.py 的 Piper 版一致，机器人/面板按这个写死）：
  GET /speak?text=…&voice=…&speed=1.0&format=wav   → 音频字节
  GET /health                                       → {"voices":[…],"default":…}
  GET /healthz                                      → 200 OK（给 docker healthcheck）
参数：
  text    要合成的文本（上限 MAX_CHARS，超了回 400 —— 长文本该由机器人拒绝发语音）
  voice   音色；不认得的名字（比如老配置里的 zh_CN-huayan-medium）回落到 DEFAULT_VOICE
  speed   语速倍数，1.0 = 原速（云厂商的取值范围不同，这里会 clamp）
  format  wav（默认，兼容老面板的试听）/ mp3（小）/ pcm（裸流，喂 silk 编码器）/ silk
"""

import binascii
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# ─────────────────────────── 配置（全部环境变量） ───────────────────────────

PORT = int(os.environ.get("PORT", "5000"))
PROVIDER = os.environ.get("TTS_PROVIDER", "minimax").strip().lower()
DEFAULT_VOICE = os.environ.get("DEFAULT_VOICE", "male-qn-qingse").strip()
MAX_CHARS = int(os.environ.get("MAX_CHARS", "300"))
TIMEOUT = float(os.environ.get("TTS_TIMEOUT", "30"))
MAX_CONCURRENCY = int(os.environ.get("MAX_CONCURRENCY", "2"))
DEFAULT_FORMAT = os.environ.get("DEFAULT_FORMAT", "wav").strip().lower()

CACHE_DIR = os.environ.get("CACHE_DIR", "/cache")
CACHE_MAX_MB = int(os.environ.get("CACHE_MAX_MB", "80"))
CACHE_ENABLED = os.environ.get("CACHE", "1") not in ("0", "false", "no")

# MiniMax（国内站 / 国际站二选一；key 与站点要配对，配错会报 1004/2049）
MINIMAX_KEY = os.environ.get("MINIMAX_API_KEY", "").strip()
MINIMAX_BASE = os.environ.get("MINIMAX_API_BASE", "https://api.minimaxi.com").rstrip("/")
MINIMAX_MODEL = os.environ.get("MINIMAX_MODEL", "speech-2.8-hd").strip()
MINIMAX_GROUP_ID = os.environ.get("MINIMAX_GROUP_ID", "").strip()

# OpenAI 兼容后端（任何 /v1/audio/speech 都能接：OpenAI、SiliconFlow、本地 TTS 网关…）
OPENAI_KEY = os.environ.get("OPENAI_TTS_API_KEY", os.environ.get("OPENAI_API_KEY", "")).strip()
OPENAI_BASE = os.environ.get("OPENAI_TTS_BASE_URL", "https://api.openai.com/v1").rstrip("/")
OPENAI_MODEL = os.environ.get("OPENAI_TTS_MODEL", "gpt-4o-mini-tts").strip()
OPENAI_VOICE = os.environ.get("OPENAI_TTS_VOICE", "alloy").strip()

# silk 编码器（官方通道发语音要 silk；NapCat 那条路不需要，它自己转）
SILK_CMD = os.environ.get("SILK_ENCODER", "silk_v3_encoder")

_SEM = threading.Semaphore(MAX_CONCURRENCY)
_lock = threading.Lock()

# 面板里给用户选的中文音色（MiniMax 系统音色；要更多就调 /v1/get_voice）
MINIMAX_VOICES = [
    "male-qn-qingse", "male-qn-jingying", "male-qn-badao", "male-qn-daxuesheng",
    "female-shaonv", "female-yujie", "female-chengshu", "female-tianmei",
    "Chinese (Mandarin)_Reliable_Executive", "Chinese (Mandarin)_News_Anchor",
    "Chinese (Mandarin)_Mature_Woman", "Arrogant_Miss",
]

MIME = {"wav": "audio/wav", "mp3": "audio/mpeg", "pcm": "audio/L16", "silk": "audio/silk"}
EXT = {"wav": ".wav", "mp3": ".mp3", "pcm": ".pcm", "silk": ".silk"}


def log(msg: str) -> None:
    """日志：只打形状与配置状态，绝不打 key。"""
    print(f"[tts] {msg}", flush=True)


def mask(secret: str) -> str:
    return "(未配置)" if not secret else secret[:3] + "***" + secret[-2:] if len(secret) > 8 else "***"


# ─────────────────────────── 缓存 ───────────────────────────


def cache_path(key: str, fmt: str):
    os.makedirs(CACHE_DIR, exist_ok=True)
    return os.path.join(CACHE_DIR, key + EXT.get(fmt, ".bin"))


def cache_get(key: str, fmt: str):
    if not CACHE_ENABLED:
        return None
    path = cache_path(key, fmt)
    if os.path.exists(path) and os.path.getsize(path) > 0:
        try:
            os.utime(path, None)  # 摸一下，LRU 用访问时间
        except OSError:
            pass
        with open(path, "rb") as fh:
            return fh.read()
    return None


def cache_put(key: str, fmt: str, data: bytes) -> None:
    if not CACHE_ENABLED or not data:
        return
    path = cache_path(key, fmt)
    tmp = path + ".tmp"
    with open(tmp, "wb") as fh:
        fh.write(data)
    os.replace(tmp, path)
    prune()


def prune() -> None:
    """容量超过 CACHE_MAX_MB 就按访问时间删最老的（说话的句子重复率很高，这个缓存很划算）。"""
    try:
        files = []
        total = 0
        for name in os.listdir(CACHE_DIR):
            p = os.path.join(CACHE_DIR, name)
            if not os.path.isfile(p):
                continue
            st = os.stat(p)
            if name.endswith(".tmp"):
                os.remove(p)
                continue
            files.append((st.st_atime, st.st_size, p))
            total += st.st_size
        limit = CACHE_MAX_MB * 1024 * 1024
        if total <= limit:
            return
        files.sort()
        for _, size, p in files:
            if total <= limit * 0.8:
                break
            try:
                os.remove(p)
                total -= size
            except OSError:
                pass
    except Exception as exc:  # 缓存清理永远不该让请求失败
        log(f"缓存清理失败（忽略）：{exc}")


# ─────────────────────────── 云端后端 ───────────────────────────


def http_json(url: str, payload: dict, headers: dict, timeout: float) -> dict:
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8", "replace"))


def http_bytes(url: str, payload: dict, headers: dict, timeout: float) -> bytes:
    body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
    req = urllib.request.Request(url, data=body, headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def minimax_audio(text: str, voice: str, speed: float, fmt: str) -> bytes:
    """MiniMax T2A v2：返回的音频在 data.audio 里，是 **hex**（不是 base64）。"""
    if not MINIMAX_KEY:
        raise RuntimeError("MINIMAX_API_KEY 没配（云端 TTS 需要一个 key）")

    # silk 要的是 16k 单声道 PCM，直接让云端吐 pcm，省掉一次重采样
    want = {"silk": "pcm", "wav": "wav", "mp3": "mp3", "pcm": "pcm"}[fmt]
    payload = {
        "model": MINIMAX_MODEL,
        "text": text,
        "stream": False,
        "output_format": "hex",
        "voice_setting": {
            "voice_id": voice,
            "speed": round(max(0.5, min(2.0, speed)), 2),
            "vol": 1.0,
            "pitch": 0,
        },
        "audio_setting": {
            "format": want,
            "sample_rate": 16000 if want in ("wav", "pcm") else 32000,
            "bitrate": 128000,
            "channel": 1,
        },
    }
    url = f"{MINIMAX_BASE}/v1/t2a_v2"
    if MINIMAX_GROUP_ID:
        url += "?GroupId=" + urllib.parse.quote(MINIMAX_GROUP_ID)

    data = http_json(url, payload, {
        "Authorization": "Bearer " + MINIMAX_KEY,
        "Content-Type": "application/json",
    }, TIMEOUT)

    base = (data or {}).get("base_resp") or {}
    if base.get("status_code") not in (0, None):
        raise RuntimeError(f"MiniMax 报错 {base.get('status_code')}: {base.get('status_msg')}")

    node = (data or {}).get("data") or {}
    audio_hex = node.get("audio")
    if not audio_hex:
        raise RuntimeError("MiniMax 没返回音频（data.audio 为空）")
    return binascii.unhexlify(audio_hex)


def openai_audio(text: str, voice: str, speed: float, fmt: str) -> bytes:
    """OpenAI 兼容 /v1/audio/speech：直接返回二进制音频。"""
    if not OPENAI_KEY:
        raise RuntimeError("OPENAI_TTS_API_KEY 没配")
    want = {"silk": "pcm", "wav": "wav", "mp3": "mp3", "pcm": "pcm"}[fmt]
    payload = {
        "model": OPENAI_MODEL,
        "input": text,
        "voice": voice or OPENAI_VOICE,
        "response_format": want,
        "speed": round(max(0.25, min(4.0, speed)), 2),
    }
    return http_bytes(f"{OPENAI_BASE}/audio/speech", payload, {
        "Authorization": "Bearer " + OPENAI_KEY,
        "Content-Type": "application/json",
        "Accept": "application/octet-stream",
    }, TIMEOUT)


def synth_raw(text: str, voice: str, speed: float, fmt: str) -> bytes:
    if PROVIDER == "openai":
        return openai_audio(text, voice, speed, fmt)
    return minimax_audio(text, voice, speed, fmt)


def to_silk(pcm: bytes) -> bytes:
    """PCM(16k/mono/s16le) → 腾讯 SILK v3。没装编码器就明确报错（别静默发坏音频）。"""
    exe = shutil.which(SILK_CMD)
    if not exe:
        raise RuntimeError(
            "这台机器上没有 silk 编码器（官方通道发语音才需要它；NapCat 那条路用 mp3/wav 即可）。"
            f"想启用：装一个 silk_v3_encoder 放进 PATH，或用 SILK_ENCODER 指定路径（缺 {SILK_CMD}）")
    with open("/tmp/_tts.pcm", "wb") as fh:
        fh.write(pcm)
    out = "/tmp/_tts.silk"
    proc = subprocess.run([exe, "/tmp/_tts.pcm", out, "-tencent", "-rate", "16000"],
                          capture_output=True, timeout=60)
    if proc.returncode != 0 or not os.path.exists(out):
        raise RuntimeError("silk 编码失败: " + proc.stderr.decode("utf-8", "replace")[:200])
    with open(out, "rb") as fh:
        return fh.read()


# ─────────────────────────── HTTP 层 ───────────────────────────


class Handler(BaseHTTPRequestHandler):
    server_version = "qqchat-tts/2.0"

    def log_message(self, fmt, *args):  # 默认会打访问日志，噪音太大；只留我们的
        return

    def _send(self, code: int, body: bytes, ctype: str, cache: bool = False) -> None:
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "public, max-age=86400" if cache else "no-store")
        self.end_headers()
        if self.command != "HEAD":
            self.wfile.write(body)

    def _json(self, code: int, obj: dict) -> None:
        self._send(code, json.dumps(obj, ensure_ascii=False).encode("utf-8"), "application/json; charset=utf-8")

    def do_HEAD(self):
        self.do_GET()

    def do_GET(self):
        parsed = urllib.parse.urlparse(self.path)
        path = parsed.path.rstrip("/") or "/"
        qs = urllib.parse.parse_qs(parsed.query)

        if path == "/healthz":
            return self._json(200, {"ok": True})
        if path in ("/health", "/voices"):
            return self._json(200, {
                "voices": MINIMAX_VOICES if PROVIDER != "openai" else [OPENAI_VOICE],
                "default": DEFAULT_VOICE,
                "provider": PROVIDER,
                "model": MINIMAX_MODEL if PROVIDER != "openai" else OPENAI_MODEL,
                "format": DEFAULT_FORMAT,
                "cache": CACHE_ENABLED,
                "key": mask(MINIMAX_KEY if PROVIDER != "openai" else OPENAI_KEY),
                "silk": bool(shutil.which(SILK_CMD)),
            })
        if path != "/speak":
            return self._json(404, {"error": "用法：GET /speak?text=…&voice=…&speed=1.0&format=wav（或 /health）"})

        text = (qs.get("text", [""])[0] or "").strip()
        if not text:
            return self._json(400, {"error": "text 不能为空"})
        if len(text) > MAX_CHARS:
            return self._json(400, {"error": f"文本 {len(text)} 字，超过上限 {MAX_CHARS} 字（长文本不该发语音）"})

        fmt = (qs.get("format", [DEFAULT_FORMAT])[0] or DEFAULT_FORMAT).lower()
        if fmt not in MIME:
            return self._json(400, {"error": f"format 只支持 {'/'.join(MIME)}"})

        voice = (qs.get("voice", [""])[0] or "").strip()
        if not voice or normalize_voice(voice) is None:
            # 老配置里是 Piper 音色名（zh_CN-huayan-medium）——那是本服务的家常便饭，直接回落
            log(f"音色 {voice!r} 不是云端音色 → 用默认 {DEFAULT_VOICE}")
            voice = DEFAULT_VOICE

        try:
            speed = float(qs.get("speed", ["1.0"])[0] or 1.0)
        except ValueError:
            speed = 1.0
        speed = max(0.5, min(2.0, speed))

        key = hashlib.sha256(f"{PROVIDER}|{MINIMAX_MODEL if PROVIDER != 'openai' else OPENAI_MODEL}|{voice}|{speed}|{fmt}|{text}".encode("utf-8")).hexdigest()[:40]

        hit = cache_get(key, fmt)
        if hit is not None:
            log(f"缓存命中（{len(hit)} 字节，{fmt}，{len(text)} 字）")
            return self._send(200, hit, MIME[fmt], cache=True)

        started = time.time()
        try:
            with _SEM:
                raw = synth_raw(text, voice, speed, fmt)
                if fmt == "silk":
                    raw = to_silk(raw)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", "replace")[:300]
            log(f"上游 HTTP {exc.code}：{detail}")
            return self._json(502, {"error": f"云端 TTS 返回 {exc.code}", "detail": detail})
        except urllib.error.URLError as exc:
            log(f"上游连不上：{exc.reason}")
            return self._json(502, {"error": f"云端 TTS 连不上：{exc.reason}"})
        except Exception as exc:
            log(f"合成失败：{exc}")
            return self._json(502, {"error": str(exc)[:300]})

        if not raw:
            return self._json(502, {"error": "云端返回了空音频"})

        cache_put(key, fmt, raw)
        log(f"合成完成 {len(raw)} 字节 / {fmt} / {len(text)} 字 / {time.time() - started:.2f}s")
        return self._send(200, raw, MIME[fmt], cache=True)


def normalize_voice(voice: str):
    """云端认识的音色名就原样返回；本地的 Piper 名（zh_CN-…）返回 None（调用方回落默认音色）。"""
    v = voice.strip()
    if not v:
        return None
    if v.lower().startswith(("zh_cn", "zh-cn", "en_us", "en-us")) and "-" in v:
        return None
    if re.fullmatch(r"[A-Za-z0-9_\-\.\(\) ]{2,64}", v):
        return v
    return None


def main() -> None:
    os.makedirs(CACHE_DIR, exist_ok=True)
    log(f"启动：provider={PROVIDER} model={MINIMAX_MODEL if PROVIDER != 'openai' else OPENAI_MODEL} "
        f"voice={DEFAULT_VOICE} key={mask(MINIMAX_KEY if PROVIDER != 'openai' else OPENAI_KEY)} "
        f"format={DEFAULT_FORMAT} 端口={PORT} 缓存={CACHE_DIR}({CACHE_MAX_MB}MB) "
        f"silk编码器={'有' if shutil.which(SILK_CMD) else '无'}")
    if PROVIDER == "openai" and not OPENAI_KEY:
        log("警告：TTS_PROVIDER=openai 但 OPENAI_TTS_API_KEY 没配，/speak 会失败")
    if PROVIDER != "openai" and not MINIMAX_KEY:
        log("警告：MINIMAX_API_KEY 没配，/speak 会失败（面板「试听一句」会直接告诉你原因）")
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(0)
