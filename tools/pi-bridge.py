#!/usr/bin/env python3
"""把「QQ 群里的 // 命令」接到本机 pi 上的桥（跑在号主自己的电脑上）。

──────────────────────────────────────────────────────────────────────
为什么需要这个脚本（拓扑设计，别删这段）：
  机器人跑在服务器容器里，而 agent 要的是**本机的能力**：你的代码目录、工具链、
  pi 的登录态。所以方向反过来 —— 本机主动连到机器人（本机在 NAT 后面，只能出站）：

      群里的 //消息 → 机器人（服务器）──ws──> 本脚本 ──subprocess──> pi -p …

  连接怎么到机器人：机器人面板只监听宿主机 127.0.0.1:8080，所以本脚本自己起一条
  ssh 本地转发（-L 18080:127.0.0.1:8080），再连 ws://127.0.0.1:18080/agent-bridge。
  ssh 断了会自动重连，不需要公网开放任何端口。

用法（把本文件和 pi-bridge.py 放同一个目录）：
    python pi-bridge.py                  # 用默认配置（见下方 DEFAULTS / 环境变量）
    python pi-bridge.py --workdir E:/bot # 指定 pi 的工作目录
    python pi-bridge.py --name ZHAOSPC --url ws://机器人/agent-bridge --token <令牌>   # 自己指定设备名
环境变量：PI_BRIDGE_URL / PI_BRIDGE_SSH / PI_BRIDGE_KEY / PI_BRIDGE_TOKEN / PI_BRIDGE_WORKDIR / PI_BRIDGE_NAME
──────────────────────────────────────────────────────────────────────
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import socket
import struct
import subprocess
import sys
import threading
import time

# ─────────── 默认配置（都可以用命令行/环境变量覆盖）───────────
DEFAULT_SSH = ""
DEFAULT_KEY = ""
DEFAULT_LOCAL_PORT = 18080          # 本地转发端口（连到服务器 127.0.0.1:8080）
DEFAULT_REMOTE = "127.0.0.1:8080"   # 服务器上的面板/桥端口
DEFAULT_WORKDIR = os.getcwd()         # pi 在哪个目录里干活
DEFAULT_PI = "pi"
DEFAULT_TOKEN = os.environ.get("PI_BRIDGE_TOKEN", "")
LOG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pi-bridge.log")

log_lock = threading.Lock()


def log(msg: str) -> None:
    line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}"
    with log_lock:
        print(line, flush=True)
        try:
            with open(LOG_PATH, "a", encoding="utf-8") as fh:
                fh.write(line + "\n")
        except OSError:
            pass


# ══════════════════════════════════════════════════════════════
#  最小 WebSocket 客户端（不依赖第三方库：pip install 在这台机器上不是必须的）
# ══════════════════════════════════════════════════════════════
class WsClient:
    def __init__(self, host: str, port: int, path: str, timeout: float = 30.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.sock.settimeout(None)
        self._buf = b""
        # 发送必须上锁：任务线程发 chunk/done 的同时，主线程每 30 秒发一次 pong ——
        # 两边同时 sendall 会把帧交缠在一起，机器人的 WS 解析器看到的就是垃圾（实测丢结果就是这个）
        self._send_lock = threading.Lock()
        key = base64.b64encode(os.urandom(16)).decode()
        req = (
            f"GET {path} HTTP/1.1\r\n"
            f"Host: {host}:{port}\r\n"
            "Upgrade: websocket\r\n"
            "Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            "Sec-WebSocket-Version: 13\r\n\r\n"
        )
        self.sock.sendall(req.encode())

        # 读握手响应（到空行为止）
        head = b""
        while b"\r\n\r\n" not in head:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("握手时连接被关闭")
            head += chunk

        header, _, rest = head.partition(b"\r\n\r\n")
        status = header.split(b"\r\n", 1)[0].decode(errors="replace")
        if "101" not in status:
            raise ConnectionError(f"握手失败：{status} {header[-200:]!r}")
        self._buf = rest

    # ---- 收 ----
    def _read_exact(self, n: int) -> bytes:
        while len(self._buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("连接已关闭")
            self._buf += chunk
        data, self._buf = self._buf[:n], self._buf[n:]
        return data

    def recv(self, timeout: float | None = None):
        """收一条完整消息。返回 (opcode, payload)；timeout 到点抛 TimeoutError。"""
        if timeout is not None:
            self.sock.settimeout(timeout)
        try:
            payload = b""
            while True:
                b1, b2 = self._read_exact(2)
                fin = b1 & 0x80
                opcode = b1 & 0x0F
                masked = b2 & 0x80
                length = b2 & 0x7F
                if length == 126:
                    length = struct.unpack("!H", self._read_exact(2))[0]
                elif length == 127:
                    length = struct.unpack("!Q", self._read_exact(8))[0]
                mask = self._read_exact(4) if masked else None
                chunk = self._read_exact(length) if length else b""
                if mask:
                    chunk = bytes(c ^ mask[i % 4] for i, c in enumerate(chunk))

                if opcode == 0x9:      # ping → 立刻回 pong
                    self._send_frame(0xA, chunk)
                    continue
                if opcode == 0xA:      # pong
                    continue
                if opcode == 0x8:      # close
                    raise ConnectionError("对端关闭")

                payload += chunk
                if fin:
                    return opcode, payload
        finally:
            if timeout is not None:
                self.sock.settimeout(None)

    # ---- 发 ----
    def _send_frame(self, opcode: int, data: bytes) -> None:
        mask = os.urandom(4)
        header = bytes([0x80 | opcode])
        n = len(data)
        if n < 126:
            header += bytes([0x80 | n])
        elif n < 65536:
            header += bytes([0x80 | 126]) + struct.pack("!H", n)
        else:
            header += bytes([0x80 | 127]) + struct.pack("!Q", n)
        masked = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
        frame = header + mask + masked
        with self._send_lock:
            self.sock.sendall(frame)

    def send_text(self, text: str) -> None:
        self._send_frame(0x1, text.encode("utf-8"))

    def send_json(self, obj: dict) -> None:
        self.send_text(json.dumps(obj, ensure_ascii=False))

    def close(self) -> None:
        try:
            self._send_frame(0x8, b"")
        except OSError:
            pass
        try:
            self.sock.close()
        except OSError:
            pass


# ══════════════════════════════════════════════════════════════
#  ssh 本地转发（自己管，断了重连）
# ══════════════════════════════════════════════════════════════
class SshTunnel:
    def __init__(self, ssh_target: str, key: str, local_port: int, remote: str, enabled: bool = True):
        self.ssh_target = ssh_target
        self.key = key
        self.local_port = local_port
        self.remote = remote
        self.enabled = enabled
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        if not self.enabled:
            return
        args = [
            "ssh", "-N",
            "-o", "StrictHostKeyChecking=no",
            "-o", "ServerAliveInterval=20",
            "-o", "ServerAliveCountMax=3",
            "-o", "ExitOnForwardFailure=yes",
        ]
        if self.key:
            args += ["-i", self.key]
        args += ["-L", f"{self.local_port}:{self.remote}", self.ssh_target]
        creation = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        self.proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                    creationflags=creation)
        log(f"ssh 转发已启动：127.0.0.1:{self.local_port} → {self.ssh_target}:{self.remote}")

    @property
    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def ensure(self) -> None:
        if not self.enabled:
            return
        if not self.alive:
            if self.proc is not None:
                log("ssh 转发断了 → 重连")
            self.start()
            time.sleep(1.5)

    def stop(self) -> None:
        if self.proc is not None and self.proc.poll() is None:
            self.proc.terminate()


# ══════════════════════════════════════════════════════════════
#  pi 的执行与流式回传
# ══════════════════════════════════════════════════════════════
TOOL_HINTS = {
    "bash": "🔧 跑命令",
    "read": "📖 读文件",
    "write": "📝 写文件",
    "edit": "✏️ 改文件",
    "grep": "🔍 搜代码",
    "find": "🔍 找文件",
    "ls": "📁 看目录",
}


class TaskRunner:
    def __init__(self, send_json, pi_cmd: str, workdir: str):
        self.send_json = send_json
        self.pi_cmd = pi_cmd
        self.workdir = workdir
        self.proc: subprocess.Popen | None = None
        self.cancelled = False
        self.lock = threading.Lock()

    def cancel(self) -> None:
        with self.lock:
            self.cancelled = True
            proc = self.proc
        if proc and proc.poll() is None:
            _kill_tree(proc)
            log("任务被取消 → 已杀掉 pi")

    def run(self, task: dict) -> None:
        task_id = task["id"]
        prompt = task["prompt"]
        cwd = task.get("cwd") or self.workdir
        model = (task.get("model") or "").strip()
        tools = (task.get("tools") or "").strip()
        session = (task.get("session") or "").strip()
        timeout = int(task.get("timeoutSec") or 900)

        args = [self.pi_cmd, "-p", "--mode", "json"]
        if session:
            args += ["--session-id", session]
        if model:
            args += ["--model", model]
        if tools:
            args += ["--tools", tools]
        args += ["--", prompt]

        log(f"任务 #{task_id} 开始：{prompt[:60]!r}（目录 {cwd}）")
        started = time.time()
        text_parts: list[str] = []
        tool_calls = 0
        tail = ""
        exit_code = 0
        error: str | None = None

        try:
            creation = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
            self.proc = subprocess.Popen(
                args, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8", errors="replace", bufsize=1,
                # stdin 必须断开：pi 的非交互模式偶尔会问一句（项目本地文件的信任/确认），
                # 接到控制台就会**永远等下去** —— 机器人那边看到的就是“卡住”（实测踩过）
                stdin=subprocess.DEVNULL,
                creationflags=creation,
            )
            self.send_json({"type": "started", "id": task_id, "pid": self.proc.pid})

            # stderr 单独一个线程读到尾巴：卡住时至少能看到 pi 说了什么
            err_tail: list[str] = []

            def _drain_stderr() -> None:
                try:
                    for line in self.proc.stderr:            # type: ignore[union-attr]
                        err_tail.append(line.rstrip())
                        if len(err_tail) > 40:
                            del err_tail[0]
                except Exception:                            # noqa: BLE001
                    pass

            threading.Thread(target=_drain_stderr, daemon=True).start()

            # 看门狗：pi 卡住（不吐任何东西）时也必须能超时杀掉 ——
            # 光靠下面那个循环里判 deadline 是不够的（没输出就永远出不来）
            deadline = started + timeout
            killed = threading.Event()

            def _watchdog() -> None:
                while not killed.is_set() and time.time() < deadline:
                    time.sleep(0.5)
                if not killed.is_set() and self.proc and self.proc.poll() is None:
                    _kill_tree(self.proc)
                    err_tail.append(f"（看门狗：超过 {timeout}s 无结果，已杀掉 pi）")

            threading.Thread(target=_watchdog, daemon=True).start()

            for line in self.proc.stdout:                      # NDJSON 一行一个事件
                if time.time() > deadline:
                    _kill_tree(self.proc)
                    error = f"超时（{timeout} 秒）"
                    break

                line = line.strip()
                if not line:
                    continue
                try:
                    event = json.loads(line)
                except json.JSONDecodeError:
                    continue

                etype = event.get("type")
                if etype == "message_update":
                    inner = event.get("assistantMessageEvent") or {}
                    if inner.get("type") == "text_delta" and inner.get("delta"):
                        text_parts.append(inner["delta"])
                        tail += inner["delta"]
                        if len(tail) > 4000:
                            tail = tail[-4000:]
                        self.send_json({"type": "chunk", "id": task_id, "text": inner["delta"]})
                    elif inner.get("type") == "tool_start":
                        tool_calls += 1
                        name = (inner.get("toolName") or inner.get("name") or "tool")
                        self.send_json({"type": "progress", "id": task_id,
                                        "note": TOOL_HINTS.get(name, f"🔧 {name}")})
                elif etype == "tool_execution_start":
                    tool_calls += 1
                    name = event.get("toolName") or "tool"
                    self.send_json({"type": "progress", "id": task_id,
                                    "note": TOOL_HINTS.get(name, f"🔧 {name}")})

            if error is None:
                exit_code = self.proc.wait(timeout=30)
                stderr = (self.proc.stderr.read() or "").strip() if self.proc.stderr else ""
                if exit_code != 0:
                    error = stderr[-600:] if stderr else f"pi 退出码 {exit_code}"
        except FileNotFoundError:
            error = f"找不到 pi：{self.pi_cmd}（改 --pi 或 PI_BRIDGE_PI）"
        except Exception as exc:                                # noqa: BLE001
            error = f"{type(exc).__name__}: {exc}"
        finally:
            killed.set()
            with self.lock:
                self.proc = None
                was_cancelled = self.cancelled

        if error and err_tail:
            error = error + "｜pi stderr: " + " / ".join(err_tail[-6:])

        duration_ms = int((time.time() - started) * 1000)
        text = "".join(text_parts).strip()

        if was_cancelled:
            self.send_json({"type": "error", "id": task_id, "message": "任务已取消"})
        elif error:
            self.send_json({"type": "error", "id": task_id, "message": error, "exitCode": exit_code or -1})
        else:
            self.send_json({"type": "done", "id": task_id, "text": text or tail.strip(),
                            "exitCode": 0, "durationMs": duration_ms, "toolCalls": tool_calls})
        log(f"任务 #{task_id} 结束：{error or f'{len(text)} 字'}（{duration_ms / 1000:.1f}s，{tool_calls} 次工具）")


def _kill_tree(proc: subprocess.Popen) -> None:
    """连子进程一起杀（pi 自己也会拉起子进程；只杀父进程会留下孤儿占着目录）。"""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/T", "/F", "/PID", str(proc.pid)],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
        else:
            os.killpg(os.getpgid(proc.pid), 15)
    except Exception:                                          # noqa: BLE001
        try:
            proc.kill()
        except Exception:                                      # noqa: BLE001
            pass


# ══════════════════════════════════════════════════════════════
#  主循环：连桥 → 收任务 → 跑 pi → 回传；断了就重连
# ══════════════════════════════════════════════════════════════
def main() -> int:
    ap = argparse.ArgumentParser(description="本机 pi ↔ QQ 机器人 agent 桥")
    ap.add_argument("--ssh", default=os.environ.get("PI_BRIDGE_SSH", DEFAULT_SSH))
    ap.add_argument("--key", default=os.environ.get("PI_BRIDGE_KEY", DEFAULT_KEY))
    ap.add_argument("--local-port", type=int, default=int(os.environ.get("PI_BRIDGE_PORT", DEFAULT_LOCAL_PORT)))
    ap.add_argument("--remote", default=os.environ.get("PI_BRIDGE_REMOTE", DEFAULT_REMOTE))
    ap.add_argument("--url", default=os.environ.get("PI_BRIDGE_URL", ""),
                    help="直连 ws://（给了就不起 ssh 转发）")
    ap.add_argument("--token", default=DEFAULT_TOKEN, help="与机器人 QQCHAT_AGENT_TOKEN 一致")
    ap.add_argument("--name", default=os.environ.get("PI_BRIDGE_NAME", ""),
                    help="设备名（面板里按这个名字认设备；空 = 用本机主机名）")
    ap.add_argument("--workdir", default=os.environ.get("PI_BRIDGE_WORKDIR", DEFAULT_WORKDIR))
    ap.add_argument("--pi", default=os.environ.get("PI_BRIDGE_PI", DEFAULT_PI))
    args = ap.parse_args()

    if not args.token:
        log("!! 没有令牌：设环境变量 PI_BRIDGE_TOKEN 或加 --token（与机器人 QQCHAT_AGENT_TOKEN 一致）")
        return 2

    if not args.url and not args.ssh:
        log("!! 得知道去哪儿连：要么 --url ws://机器人:/agent-bridge（推荐，最省事），")
        log("   要么 --ssh user@host + --key 让本脚本自己开 ssh 转发。")
        return 2

    if not os.path.exists(args.pi):
        log(f"!! 找不到 pi：{args.pi}（用 --pi 指到 pi.cmd）")

    tunnel = SshTunnel(args.ssh, args.key, args.local_port, args.remote, enabled=not args.url)
    runner = TaskRunner(send_json=lambda obj: None, pi_cmd=args.pi, workdir=args.workdir)

    backoff = 3
    while True:
        tunnel.ensure()
        url = args.url or f"ws://127.0.0.1:{args.local_port}/agent-bridge"
        host, port, path = _split_ws_url(url)
        path = f"{path}?token={args.token}" if "?" not in path else f"{path}&token={args.token}"

        log(f"连接 {host}:{port}{path.split('?')[0]} …")
        try:
            ws = WsClient(host, port, path)
        except Exception as exc:                                # noqa: BLE001
            log(f"连接失败（{exc}）→ {backoff}s 后重试")
            time.sleep(backoff)
            backoff = min(backoff * 2, 30)
            continue

        backoff = 3
        runner.send_json = ws.send_json
        ws.send_json({
            "type": "hello",
            # 设备名：面板里“按名字认设备”就靠它。默认取本机主机名，可以用 --name/PI_BRIDGE_NAME 改。
            "host": args.name or os.environ.get("COMPUTERNAME") or socket.gethostname(),
            "cwd": args.workdir,
            "pi": _pi_version(args.pi),
            # 模型列表：面板里直接选（不用手敲模型名，也不用手改桥的启动参数）
            "models": _list_models(args.pi),
            "version": 1,
        })
        log("已连上机器人（等 // 命令）")

        try:
            while True:
                try:
                    opcode, payload = ws.recv(timeout=30)
                except (TimeoutError, socket.timeout):
                    ws.send_json({"type": "pong"})              # 心跳：顺便探测连接还活着
                    continue
                except ConnectionError:
                    break

                if opcode != 0x1:
                    continue
                try:
                    msg = json.loads(payload.decode("utf-8"))
                except json.JSONDecodeError:
                    continue

                mtype = msg.get("type")
                if mtype == "task":
                    if runner.proc is not None:
                        ws.send_json({"type": "error", "id": msg.get("id"),
                                      "message": "本机还有任务在跑（串行执行）"})
                        continue
                    threading.Thread(target=runner.run, args=(msg,), daemon=True).start()
                elif mtype == "cancel":
                    runner.cancel()
                elif mtype == "models":
                    # 面板里点“刷新模型”时用：现场问一遍 pi 有哪些模型
                    ws.send_json({"type": "models", "models": _list_models(args.pi)})
                elif mtype == "forget":
                    # deleted session: also remove pi's own session file on this machine
                    removed = _forget_session(msg.get("session") or "")
                    ws.send_json({"type": "forgot", "session": msg.get("session"), "removed": removed})
                elif mtype == "ping":
                    ws.send_json({"type": "pong"})
        except KeyboardInterrupt:
            log("收到 Ctrl+C，退出")
            tunnel.stop()
            return 0
        except Exception as exc:                                # noqa: BLE001
            log(f"连接出错：{type(exc).__name__} {exc}")
        finally:
            try:
                ws.close()
            except Exception:                                   # noqa: BLE001
                pass

        log(f"桥断了 → {backoff}s 后重连")
        time.sleep(backoff)


def _split_ws_url(url: str) -> tuple[str, int, str]:
    m = re.match(r"^ws://([^/:]+)(?::(\d+))?(/.*)?$", url)
    if not m:
        raise SystemExit(f"看不懂的 ws 地址：{url}")
    host = m.group(1)
    port = int(m.group(2) or 80)
    path = m.group(3) or "/"
    return host, port, path


def _pi_version(pi_cmd: str) -> str:
    try:
        out = subprocess.run([pi_cmd, "--version"], capture_output=True, text=True, timeout=30)
        return (out.stdout or out.stderr).strip().splitlines()[0][:40] if (out.stdout or out.stderr) else "?"
    except Exception:                                           # noqa: BLE001
        return "?"


def _forget_session(pi_session_id: str) -> int:
    """删掉 pi 那边的会话文件（`~/.pi/agent/sessions/<工作目录>/<会话>.jsonl`）。

    为什么由桥来做：会话文件在**本机**，机器人看不到。删不掉也不报错（只是历史还在），
    但会写进日志，号主自己能看出发生了什么。
    """
    if not pi_session_id:
        return 0

    import glob

    home = os.path.expanduser("~")
    pattern = os.path.join(home, ".pi", "agent", "sessions", "*", f"*{pi_session_id}*.jsonl")
    removed = 0
    for path in glob.glob(pattern):
        try:
            os.remove(path)
            removed += 1
            log(f"已删除 pi 会话文件：{path}")
        except OSError as exc:
            log(f"删不掉 {path}：{exc}")

    if removed == 0:
        log(f"没找到 pi 会话文件（{pi_session_id}）—— 可能还没落盘/存到别的目录")
    return removed


def _list_models(pi_cmd: str) -> list[str]:
    """问一遍 pi 有哪些模型（`pi --list-models` 是张表：provider / model / …）。

    返回形如 "provider/model" 的列表 —— pi 的 --model 就吃这种写法。
    """
    try:
        out = subprocess.run([pi_cmd, "--list-models"], capture_output=True, text=True, timeout=60)
        text = out.stdout or ""
    except Exception:                                           # noqa: BLE001
        return []

    models: list[str] = []
    for line in text.splitlines()[1:]:                          # 第一行是表头
        parts = line.split()
        if len(parts) >= 2 and not parts[0].startswith("-"):
            models.append(f"{parts[0]}/{parts[1]}")
    return models[:200]


if __name__ == "__main__":
    sys.exit(main())
