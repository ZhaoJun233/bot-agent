#!/usr/bin/env python3
"""服务器文件操作（sftp/scp 的小包装）—— 给 **本机 pi agent** 用的。

为什么有它：机器人跑在服务器上，agent 在自己那台电脑上。「读写服务器文件」本来只能
手敲交互式 `sftp`（脚本里一用就容易卡在提示符上），而这个脚本把常用几步做成子命令，
非交互、失败有退出码。

坐标从哪来：桥（pi-bridge.py）给每个 pi 任务注入了三个环境变量 ——
    PI_SERVER_SSH        ssh 目标，如 ec2-user@example.com（或 ~/.ssh/config 里的别名）
    PI_SERVER_KEY        私钥路径（本机上的路径，**不是密钥内容**）
    PI_SERVER_SFTP_PORT  桥转发到服务器 sshd 的本地端口（默认 2222）
所以 agent 里直接 `python tools/server-files.py ls /opt/qqchat` 就行，不用记参数。

也可以显式传：--ssh / --key / --port（命令行优先于环境变量）。

用法：
    server-files.py ls [远程目录]                列目录（默认 /）
    server-files.py cat <远程文件>               打印文件内容
    server-files.py get <远程文件> [本地路径]     下载
    server-files.py put <本地文件> [远程路径]     上传
    server-files.py rm <远程文件>                删除（文件或空目录）
    server-files.py mkdir <远程目录>             建目录（含父目录）
    server-files.py raw "<sftp 批量命令>"         直接跑一段 sftp 批处理（进阶）

注意：这些都是**在服务器上真改文件**的操作，别在没把握时批量跑。
"""

from __future__ import annotations

import argparse
import os
import shlex
import shutil
import subprocess
import sys
import tempfile

# 控制台可能是 GBK（中文 Windows）：服务器上中文文件名会让解码/打印直接崩，
# 所以进出都固定走 UTF-8（错字符也不抛，换掉就行）。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:  # noqa: BLE001 —— 老 Python / 重定向场景没有 reconfigure
    pass


def build_sftp_args(cmd: list[str], key: str, port: int, batch_file: str, ssh: str) -> list[str]:
    """拼出 sftp 的 argv（单独一个函数便于测试：不真的连服务器也能验参数）。

    cmd 默认是 ["sftp"]，可以用 $PI_SERVER_SFTP_BIN / --sftp-bin 换成别的（比如 Windows 上
    C:\\Windows\\System32\\OpenSSH\\sftp.exe，或者 `python 某脚本`——测试就是这么干的）。
    """
    args = list(cmd) + ["-b", batch_file, "-q"]
    if port and port > 0:
        args += ["-P", str(port)]
    if key:
        args += ["-i", key]
    args += ["-o", "StrictHostKeyChecking=no", "-o", "BatchMode=yes"]
    if ssh:
        args.append(ssh)
    return args


def resolve(args: argparse.Namespace) -> tuple[str, str, int, list[str]]:
    ssh = (args.ssh or os.environ.get("PI_SERVER_SSH", "")).strip()
    key = (args.key or os.environ.get("PI_SERVER_KEY", "")).strip()
    raw_port = args.port or os.environ.get("PI_SERVER_SFTP_PORT", "") or "0"
    try:
        port = int(str(raw_port).strip() or 0)
    except ValueError:
        port = 0

    raw_bin = (getattr(args, "sftp_bin", "") or os.environ.get("PI_SERVER_SFTP_BIN", "")).strip()
    cmd = shlex.split(raw_bin) if raw_bin else ["sftp"]
    if not cmd:
        cmd = ["sftp"]
    return ssh, key, port, cmd


def run_batch(ssh: str, key: str, port: int, cmd: list[str], script: str, quiet: bool = False) -> int:
    if not cmd or not shutil.which(cmd[0]):
        print(f"[X] 找不到 {cmd[0] if cmd else 'sftp'}（Windows 上要装 OpenSSH 客户端："
              "设置 → 应用 → 可选功能；也可以用 $PI_SERVER_SFTP_BIN 指到 sftp.exe）", file=sys.stderr)
        return 3

    fd, path = tempfile.mkstemp(prefix="server-files-", suffix=".txt")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(script if script.endswith("\n") else script + "\n")
        proc = subprocess.run(build_sftp_args(cmd, key, port, path, ssh), text=True,
                              encoding="utf-8", errors="replace",
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        out = proc.stdout or ""
        if proc.returncode != 0:
            print(out.strip() or f"[X] sftp 退出码 {proc.returncode}", file=sys.stderr)
            return proc.returncode
        if out.strip() and not quiet:
            print(out.rstrip())
        return 0
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


def quote(remote: str) -> str:
    """sftp 批处理里的路径：**双引号**包裹（实测 `ls -l "/tmp/sp ace.txt"` 可行）。

    为什么不用反斜杠转义（`/tmp/sp\\ ace.txt`）：那套在 sftp 的批处理解析里不稳，
    而且会把 Windows 本地路径的 `\\` 也搞乱。路径里真的带双引号就直说 —— 这种名字不该从脚本里碰。
    """
    text = (remote or "").strip()
    if '"' in text:
        raise ValueError(f"路径里带双引号，脚本不敢碰：{text}")
    return f'"{text}"'


def quote_local(path: str) -> str:
    """本地路径：转成正斜杠再引号包 —— Windows 上 `C:\\a\\b` 里的反斜杠会被 sftp 当转义吃掉。"""
    return quote(os.path.abspath(os.path.expanduser(path)).replace("\\", "/"))


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="服务器文件操作（sftp 包装；坐标默认取环境变量）")
    ap.add_argument("--ssh", default="", help="ssh 目标（默认 $PI_SERVER_SSH）")
    ap.add_argument("--key", default="", help="私钥路径（默认 $PI_SERVER_KEY）")
    ap.add_argument("--port", default="", help="本地转发端口（默认 $PI_SERVER_SFTP_PORT）")
    ap.add_argument("--sftp-bin", default="", help="sftp 可执行文件（默认 $PI_SERVER_SFTP_BIN 或 sftp）")
    ap.add_argument("--max-cat", type=int, default=256 * 1024, help="cat 最多打印多少字节（默认 256KB）")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("ls", help="列目录")
    p.add_argument("remote", nargs="?", default=".")
    p = sub.add_parser("cat", help="打印文件（实际是先下载到临时文件；sftp 没有 cat 命令）")
    p.add_argument("remote")
    p = sub.add_parser("get", help="下载")
    p.add_argument("remote")
    p.add_argument("local", nargs="?")
    p = sub.add_parser("put", help="上传")
    p.add_argument("local")
    p.add_argument("remote", nargs="?")
    p = sub.add_parser("rm", help="删除文件/空目录")
    p.add_argument("remote")
    p = sub.add_parser("mkdir", help="建目录（含父目录）")
    p.add_argument("remote")
    p = sub.add_parser("raw", help="直接跑一段 sftp 批处理")
    p.add_argument("script")

    args = ap.parse_args(argv)
    ssh, key, port, sftp_cmd = resolve(args)

    if not ssh and args.cmd != "raw":
        print("[X] 没有服务器坐标：设环境变量 PI_SERVER_SSH（或传 --ssh）", file=sys.stderr)
        return 2

    try:
        if args.cmd == "ls":
            # ls -l 带权限/大小/时间，比 -1 有用
            return run_batch(ssh, key, port, sftp_cmd, f"ls -l {quote(args.remote)}")
        if args.cmd == "cat":
            return cat_remote(ssh, key, port, sftp_cmd, args.remote, args.max_cat)
        if args.cmd == "get":
            return run_batch(ssh, key, port, sftp_cmd,
                             f"get {quote(args.remote)}" + (f" {quote_local(args.local)}" if args.local else ""))
        if args.cmd == "put":
            return run_batch(ssh, key, port, sftp_cmd,
                             f"put {quote_local(args.local)}" + (f" {quote(args.remote)}" if args.remote else ""))
        if args.cmd == "rm":
            return run_batch(ssh, key, port, sftp_cmd, f"rm {quote(args.remote)}")
        if args.cmd == "mkdir":
            return run_batch(ssh, key, port, sftp_cmd, f"mkdir {quote(args.remote)}")
        if args.cmd == "raw":
            return run_batch(ssh, key, port, sftp_cmd, args.script)
    except ValueError as ex:
        print(f"[X] {ex}", file=sys.stderr)
        return 2

    print(f"[X] 不认识的动作 {args.cmd}", file=sys.stderr)
    return 2


def cat_remote(ssh: str, key: str, port: int, sftp_cmd: list[str], remote: str, max_bytes: int) -> int:
    """打印远端文件：sftp 没有 cat 命令（实测 `cat x` → Invalid command），
    所以先 get 到临时文件再读出来（顺带能卡大小 + 识别二进制）。"""
    fd, tmp = tempfile.mkstemp(prefix="server-files-cat-")
    os.close(fd)
    try:
        code = run_batch(ssh, key, port, sftp_cmd, f"get {quote(remote)} {quote_local(tmp)}", quiet=True)
        if code != 0:
            return code
        size = os.path.getsize(tmp)
        with open(tmp, "rb") as handle:
            data = handle.read(max_bytes)
        if b"\x00" in data[:4096]:
            print(f"[!] {remote} 看着是二进制（{size} 字节）：不打印内容，请用 get 下载", file=sys.stderr)
            return 4
        sys.stdout.write(data.decode("utf-8", "replace"))
        if size > len(data):
            print(f"\n[!] 只打印了前 {len(data)} 字节（共 {size} 字节）", file=sys.stderr)
        return 0
    finally:
        try:
            os.unlink(tmp)
        except OSError:
            pass


if __name__ == "__main__":
    raise SystemExit(main())
