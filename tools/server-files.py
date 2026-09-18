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


def build_sftp_args(cmd: list[str], key: str, port: int, batch_file: str, ssh: str) -> list[str]:
    """拼出 sftp 的 argv（单独一个函数便于测试：不真的连服务器也能验参数）。

    cmd 默认是 ["sftp"]，可以用 $PI_SERVER_SFTP_BIN / --sftp-bin 换成别的（比如 Windows 上
    C:\\Windows\\System32\\OpenSSH\\sftp.exe，或者 `python 某脚本`——测试就是这么干的）。
    """
    args = list(cmd) + ["-b", batch_file]
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


def run_batch(ssh: str, key: str, port: int, cmd: list[str], script: str) -> int:
    if not cmd or not shutil.which(cmd[0]):
        print(f"[X] 找不到 {cmd[0] if cmd else 'sftp'}（Windows 上要装 OpenSSH 客户端："
              "设置 → 应用 → 可选功能；也可以用 $PI_SERVER_SFTP_BIN 指到 sftp.exe）", file=sys.stderr)
        return 3

    fd, path = tempfile.mkstemp(prefix="server-files-", suffix=".txt")
    try:
        with os.fdopen(fd, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(script if script.endswith("\n") else script + "\n")
        proc = subprocess.run(build_sftp_args(cmd, key, port, path, ssh), text=True,
                              stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        out = proc.stdout or ""
        if proc.returncode != 0:
            print(out.strip() or f"[X] sftp 退出码 {proc.returncode}", file=sys.stderr)
            return proc.returncode
        if out.strip():
            print(out.rstrip())
        return 0
    finally:
        try:
            os.unlink(path)
        except OSError:
            pass


def quote(remote: str) -> str:
    """sftp 批处理里的路径：空格要转义（sftp 用反斜杠转义，不用引号）。"""
    return remote.replace("\\", "\\\\").replace(" ", "\\ ")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="服务器文件操作（sftp 包装；坐标默认取环境变量）")
    ap.add_argument("--ssh", default="", help="ssh 目标（默认 $PI_SERVER_SSH）")
    ap.add_argument("--key", default="", help="私钥路径（默认 $PI_SERVER_KEY）")
    ap.add_argument("--port", default="", help="本地转发端口（默认 $PI_SERVER_SFTP_PORT）")
    ap.add_argument("--sftp-bin", default="", help="sftp 可执行文件（默认 $PI_SERVER_SFTP_BIN 或 sftp）")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("ls", help="列目录")
    p.add_argument("remote", nargs="?", default=".")
    p = sub.add_parser("cat", help="打印文件")
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

    if args.cmd == "ls":
        # ls -l 会带权限/大小/时间；-1 更干净，两者都给（先 -1 再 -l 太吵，取 -l）
        return run_batch(ssh, key, port, sftp_cmd, f"ls -l {quote(args.remote)}")
    if args.cmd == "cat":
        return run_batch(ssh, key, port, sftp_cmd, f"cat {quote(args.remote)}")
    if args.cmd == "get":
        return run_batch(ssh, key, port, sftp_cmd,
                         f"get {quote(args.remote)}" + (f" {quote(args.local)}" if args.local else ""))
    if args.cmd == "put":
        return run_batch(ssh, key, port, sftp_cmd,
                         f"put {quote(args.local)}" + (f" {quote(args.remote)}" if args.remote else ""))
    if args.cmd == "rm":
        return run_batch(ssh, key, port, sftp_cmd, f"rm {quote(args.remote)}")
    if args.cmd == "mkdir":
        return run_batch(ssh, key, port, sftp_cmd, f"mkdir {quote(args.remote)}")
    if args.cmd == "raw":
        return run_batch(ssh, key, port, sftp_cmd, args.script)

    print(f"[X] 不认识的动作 {args.cmd}", file=sys.stderr)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
