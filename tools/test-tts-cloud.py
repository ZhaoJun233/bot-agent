#!/usr/bin/env python3
"""云端 TTS 旁路服务的自测：起一个**假上游**（不联网、不花钱），逐条验证契约。

跑法（仓库根目录）：
    python tools/test-tts-cloud.py

覆盖：/health、/speak 拿 wav、缓存命中不重复上游、mp3 分支、Piper 老音色名回落、
      超长文本 400、上游报错透传 502、silk 没装编码器时明确报错。
"""

import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.abspath(__file__))
PROXY = os.path.join(HERE, "tts-cloud-server.py")

passed = 0
failed = 0


def check(desc: str, ok: bool, detail: str = "") -> None:
    global passed, failed
    if ok:
        passed += 1
        print(f"  ✓ {desc}")
    else:
        failed += 1
        print(f"  ✗ {desc}" + (f"  ← {detail}" if detail else ""))


def tiny_wav(seconds: float = 0.2, rate: int = 16000) -> bytes:
    """一段真 RIFF WAVE（16k 单声道静音），够用来验头。"""
    import struct
    frames = int(seconds * rate)
    data = b"\x00\x00" * frames
    header = (b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt "
              + struct.pack("<IHHIIHH", 16, 1, 1, rate, rate * 2, 2, 16)
              + b"data" + struct.pack("<I", len(data)))
    return header + data


WAV = tiny_wav()
MP3 = b"ID3\x03\x00\x00\x00\x00\x00\x00" + b"\xff\xfb\x90\x00" + b"\x00" * 200


class MockUpstream(BaseHTTPRequestHandler):
    """假 MiniMax：/v1/t2a_v2 返回 hex 音频；可切换成报错。"""

    calls: list = []
    mode = "ok"

    def log_message(self, *a):
        return

    def do_POST(self):
        length = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(length).decode("utf-8"))
        MockUpstream.calls.append({
            "path": self.path,
            "auth": self.headers.get("Authorization", ""),
            "body": body,
        })

        if MockUpstream.mode == "fail":
            payload = {"data": None, "base_resp": {"status_code": 1008, "status_msg": "insufficient balance"}}
        else:
            fmt = (body.get("audio_setting") or {}).get("format", "mp3")
            audio = WAV if fmt in ("wav", "pcm") else MP3
            payload = {"data": {"audio": audio.hex(), "status": 2},
                       "extra_info": {"audio_length": 200, "usage_characters": len(body.get("text", ""))},
                       "base_resp": {"status_code": 0, "status_msg": "success"}}
        raw = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)


def get(url: str):
    try:
        with urllib.request.urlopen(url, timeout=20) as resp:
            return resp.status, resp.read(), resp.headers
    except urllib.error.HTTPError as exc:
        return exc.code, exc.read(), exc.headers


def main() -> int:
    upstream = ThreadingHTTPServer(("127.0.0.1", 0), MockUpstream)
    up_port = upstream.server_address[1]
    threading.Thread(target=upstream.serve_forever, daemon=True).start()

    cache_dir = tempfile.mkdtemp(prefix="tts-cloud-")
    # 端口自己找一个空的：写死端口会被上一次没清干净的实例占着，
    # 结果是“测试在问上一个代理”，看上去全绿其实什么都没验到（踩过一次）。
    probe = socket.socket()
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
    probe.close()
    env = dict(os.environ)
    env.update({
        "PORT": str(port),
        "TTS_PROVIDER": "minimax",
        "MINIMAX_API_KEY": "sk-test-key-1234567890",
        "MINIMAX_API_BASE": f"http://127.0.0.1:{up_port}",
        "MINIMAX_MODEL": "speech-2.8-hd",
        "DEFAULT_VOICE": "male-qn-qingse",
        "CACHE_DIR": cache_dir,
        "DEFAULT_FORMAT": "wav",
        "SILK_ENCODER": "definitely-not-installed-silk",
        "MAX_CHARS": "60",
        "PYTHONUNBUFFERED": "1",
        "PYTHONIOENCODING": "utf-8",
    })
    proc = subprocess.Popen([sys.executable, PROXY], env=env,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8")
    base = f"http://127.0.0.1:{port}"

    # 子进程的 stdout 是管道：直接 read() 会阻塞到它退出（而它不会自己退）——踩过两次，
    # 所以开一个后台线程一直排空，最后从列表里取。
    child_log: list = []
    threading.Thread(target=lambda: [child_log.append(line) for line in proc.stdout],
                     daemon=True).start()

    try:
        ready = False
        for _ in range(50):  # 等它起来
            if proc.poll() is not None:
                break  # 进程已经死了，别再等：下面会把它的话打出来
            try:
                if get(base + "/healthz")[0] == 200:
                    ready = True
                    break
            except Exception:
                time.sleep(0.2)

        if not ready:
            print("服务没起来：")
            print("".join(child_log))
            return 1

        # 再确认一遍“现在答话的确实是我们刚起的这个进程”（端口冲突时能当场看出来）
        if proc.poll() is not None:
            print("刚起的代理已经退出了（端口被占？）：")
            print("".join(child_log))
            return 1

        print("▶ /health 契约")
        status, body, _ = get(base + "/health")
        body_text = body.decode("utf-8", "replace")
        health = json.loads(body_text)
        check("HTTP 200", status == 200)
        check("有 voices 列表（面板下拉用）", isinstance(health.get("voices"), list) and len(health["voices"]) > 0)
        check("有 default 音色", bool(health.get("default")))
        check("报出 provider / 缓存状态", health.get("provider") == "minimax" and health.get("cache") is True)
        check("key 只回掩码（不回原文）", "sk-test-key" not in body_text, body_text[:120])
        check("silk 编码器缺失被如实报出", health.get("silk") is False)

        print("▶ /speak：来一段，拿到 wav")
        MockUpstream.calls.clear()
        status, audio, headers = get(base + "/speak?text=%E4%BD%A0%E5%A5%BD&voice=male-qn-qingse&speed=1.0")
        check("HTTP 200", status == 200, str(status))
        check("Content-Type=audio/wav", (headers.get("Content-Type") or "") == "audio/wav")
        check("字节是 RIFF/WAVE（面板试听按这个校验）", audio[:4] == b"RIFF" and audio[8:12] == b"WAVE")
        check("上游收到的是 hex 输出请求", MockUpstream.calls and MockUpstream.calls[0]["body"].get("output_format") == "hex")
        check("上游收到的 text 正确", MockUpstream.calls and MockUpstream.calls[0]["body"].get("text") == "你好")
        check("上游收到的音色/语速正确",
              MockUpstream.calls[0]["body"]["voice_setting"]["voice_id"] == "male-qn-qingse"
              and abs(MockUpstream.calls[0]["body"]["voice_setting"]["speed"] - 1.0) < 0.01)
        check("鉴权头是 Bearer", MockUpstream.calls[0]["auth"].startswith("Bearer sk-test-key"))
        check("wav 请求走 16k 单声道（silk 也吃这个）",
              MockUpstream.calls[0]["body"]["audio_setting"]["sample_rate"] == 16000
              and MockUpstream.calls[0]["body"]["audio_setting"]["channel"] == 1)

        print("▶ 缓存：同一句第二次不再打上游")
        before = len(MockUpstream.calls)
        status2, audio2, _ = get(base + "/speak?text=%E4%BD%A0%E5%A5%BD&voice=male-qn-qingse&speed=1.0")
        check("第二次仍是 200 且字节一致", status2 == 200 and audio2 == audio)
        check("没有新的上游调用（省了钱也省了等待）", len(MockUpstream.calls) == before)

        print("▶ mp3 分支")
        MockUpstream.calls.clear()
        status3, audio3, headers3 = get(base + "/speak?text=hi&format=mp3")
        check("HTTP 200 且 Content-Type=audio/mpeg", status3 == 200 and headers3.get("Content-Type") == "audio/mpeg")
        check("上游被要求 mp3", MockUpstream.calls[0]["body"]["audio_setting"]["format"] == "mp3")

        print("▶ 老 Piper 音色名回落（老配置不改也能用）")
        MockUpstream.calls.clear()
        status4, _, _ = get(base + "/speak?text=%E5%96%82&voice=zh_CN-huayan-medium")
        check("HTTP 200", status4 == 200)
        check("实际用的是默认云端音色",
              MockUpstream.calls[0]["body"]["voice_setting"]["voice_id"] == "male-qn-qingse",
              str(MockUpstream.calls[0]["body"]["voice_setting"]))

        print("▶ 边界：空文本/超长文本")
        check("空文本 400", get(base + "/speak?text=")[0] == 400)
        check("超过 MAX_CHARS 400（长文本不该发语音）", get(base + "/speak?text=" + "a" * 61)[0] == 400)

        print("▶ 边界：上游报错要透传，不能假装成功")
        MockUpstream.mode = "fail"
        status5, body5, _ = get(base + "/speak?text=%E6%B5%8B%E8%AF%95%E6%8A%A5%E9%94%99&voice=male-qn-qingse")
        detail = json.loads(body5.decode("utf-8", "replace")).get("error", "")
        check("HTTP 502（不是 200）", status5 == 502, str(status5))
        check("错误里带上游原话（1008 余额不足）", "1008" in detail, detail[:120])
        MockUpstream.mode = "ok"

        print("▶ 边界：silk 没装编码器时明确报错")
        status6, body6, _ = get(base + "/speak?text=%E4%BD%A0%E5%A5%BD&format=silk")
        silk_err = json.loads(body6.decode("utf-8", "replace")).get("error", "")
        check("HTTP 502 且说明原因（不静默发坏音频）", status6 == 502 and "silk" in silk_err, silk_err[:120])

        print("▶ 杂项")
        check("未知路径 404", get(base + "/nope")[0] == 404)
        time.sleep(0.3)  # 给排空线程一点时间把最后几行收完
        log = "".join(child_log)
        check("日志里没有 key 原文", "sk-test-key-1234567890" not in log, log[-200:] if "sk-test" in log else "")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        upstream.shutdown()
        shutil.rmtree(cache_dir, ignore_errors=True)

    print("─" * 60)
    print(f"通过 {passed}，失败 {failed}")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
