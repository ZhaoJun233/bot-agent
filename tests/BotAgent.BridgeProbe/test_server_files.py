#!/usr/bin/env python3
"""server-files.py 的测试：**不连服务器**，用一个假的 sftp 验参数与批处理内容。

为什么这么测：真连一台 SSH 服务器在 CI/开发机上不可行，而最容易错的就是
「端口/私钥/批量脚本」这三样怎么拼 —— 一个假 sftp 把它记下来就够了。
"""

from __future__ import annotations

import os
import shutil
import stat
import subprocess
import sys
import tempfile
from pathlib import Path

# 控制台可能是 GBK（中文 Windows）：输出统一走 UTF-8，否则一打印 ▶ 就 UnicodeEncodeError
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:  # noqa: BLE001  —— 老 Python / 重定向场景下没有 reconfigure
    pass

HERE = Path(__file__).resolve().parent
SCRIPT = HERE.parent.parent / "tools" / "server-files.py"

FAKE_SFTP = '''#!/usr/bin/env python3
"""假 sftp：把 argv 与 -b 批处理内容各写一份，然后成功退出。"""
import os
import pathlib
import sys

out = pathlib.Path(os.environ["FAKE_OUT"])
out.mkdir(parents=True, exist_ok=True)
argv = sys.argv[1:]
(out / "argv.txt").write_text(" ".join(argv), encoding="utf-8")
batch = ""
if "-b" in argv:
    batch = pathlib.Path(argv[argv.index("-b") + 1]).read_text(encoding="utf-8").strip()
(out / "batch.txt").write_text(batch, encoding="utf-8")
print("fake-sftp ok")
'''

PASS = 0
FAIL = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global PASS, FAIL
    if ok:
        PASS += 1
        print(f"  [OK] {name}")
    else:
        FAIL += 1
        print(f"  [XX] {name}" + (f"  → {detail}" if detail else ""))


def run_case(args: list[str], env_extra: dict[str, str] | None = None) -> tuple[int, str, Path]:
    tmp = Path(tempfile.mkdtemp(prefix="server-files-"))
    stub = tmp / "fake_sftp.py"
    stub.write_text(FAKE_SFTP, encoding="utf-8")

    env = dict(os.environ)
    env["FAKE_OUT"] = str(tmp)
    # 用 "python 脚本" 当 sftp —— Windows 上验子进程不现实，这样两边都能跑
    env["PI_SERVER_SFTP_BIN"] = f'"{sys.executable}" "{stub}"'
    env["PI_SERVER_SSH"] = "ec2-user@example.com"
    env["PI_SERVER_KEY"] = r"C:\keys\example.pem"
    env["PI_SERVER_SFTP_PORT"] = "2222"
    if env_extra:
        env.update(env_extra)

    proc = subprocess.run([sys.executable, str(SCRIPT)] + args, env=env,
                          text=True, encoding="utf-8", errors="replace",
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    return proc.returncode, proc.stdout or "", tmp


def main() -> int:
    print("▶ server-files.py（假 sftp 验参数）")

    # ① 环境变量里的坐标：端口 -P、私钥 -i、目标在最后
    code, out, tmp = run_case(["ls", "/opt/qqchat"])
    argv = (tmp / "argv.txt").read_text(encoding="utf-8").strip() if (tmp / "argv.txt").exists() else ""
    batch = (tmp / "batch.txt").read_text(encoding="utf-8").strip() if (tmp / "batch.txt").exists() else ""
    check("退出码 0（假 sftp 成功）", code == 0, out.strip()[:120])
    check("★ 端口来自 $PI_SERVER_SFTP_PORT（-P 2222）", "-P 2222" in argv, argv)
    check("★ 私钥来自 $PI_SERVER_KEY（-i …）", "-i" in argv and "example.pem" in argv, argv)
    check("★ 目标在最后且是 $PI_SERVER_SSH", argv.rstrip().endswith("ec2-user@example.com"), argv)
    check("★ 非交互：带 -b 批处理 + BatchMode=yes（不然脚本会卡在提示符上）",
          "-b" in argv and "BatchMode=yes" in argv, argv)
    check("★ 批处理里是 ls -l \"/opt/qqchat\"（双引号包裹）", batch == 'ls -l "/opt/qqchat"', batch)

    # ② 命令行覆盖环境变量
    code, out, tmp = run_case(["--port", "3333", "--ssh", "someone@host2", "ls", "."])
    argv = (tmp / "argv.txt").read_text(encoding="utf-8").strip()
    check("★ 命令行参数优先于环境变量", "-P 3333" in argv and argv.rstrip().endswith("someone@host2"), argv)

    # ③ 带空格的路径用**双引号**包裹（实测 sftp 认这个；反斜杠转义不稳）
    code, out, tmp = run_case(["cat", "/tmp/my file.txt"])
    batch = (tmp / "batch.txt").read_text(encoding="utf-8").strip()
    check("★ 远端路径用双引号包（空格不再靠反斜杠转义）",
          batch.startswith('get "/tmp/my file.txt"') and "\\ " not in batch, batch[:70])

    # ③b 本地路径转成正斜杠（Windows 的反斜杠会被 sftp 当转义吃掉）
    code, out, tmp = run_case(["put", r"C:\temp\a.txt"])
    batch = (tmp / "batch.txt").read_text(encoding="utf-8").strip()
    check("★ 本地 Windows 路径转正斜杠后才写进批处理", batch.startswith('put "C:/temp/a.txt"') and "\\\\" not in batch, batch)

    # ④ 子命令拼出来的批处理对不对
    for sub, args, expect in [
        ("get", ["/etc/hosts"], 'get "/etc/hosts"'),
        ("put", ["a.txt"], None),
        ("rm", ["/tmp/x"], 'rm "/tmp/x"'),
        ("mkdir", ["/tmp/a/b"], 'mkdir "/tmp/a/b"'),
    ]:
        code, out, tmp = run_case([sub] + args)
        batch = (tmp / "batch.txt").read_text(encoding="utf-8").strip()
        if expect is None:
            check(f"批处理：{sub} 带上了本地绝对路径", batch.startswith('put "') and batch.endswith('"'), batch)
        else:
            check(f"批处理：{sub} → {expect}", batch == expect, batch)

    # ⑤ 没有坐标时要说人话，而不是默默连本机
    code, out, tmp = run_case(["ls", "."], env_extra={"PI_SERVER_SSH": ""})
    check("★ 没配 PI_SERVER_SSH 时明确报错（退出码 2）", code == 2 and "PI_SERVER_SSH" in out, f"code={code} {out.strip()[:100]}")

    # ⑥ 找不到 sftp 时的提示（把“sftp 可执行”指到一个不存在的名字）
    code, out, tmp = run_case(["ls", "."], env_extra={"PI_SERVER_SFTP_BIN": "no-such-sftp-xyz"})
    check("★ 本机没有 sftp 时给出可操作的提示", code == 3 and "no-such-sftp-xyz" in out, out.strip()[:140])

    for tmp in Path(tempfile.gettempdir()).glob("server-files-*"):
        shutil.rmtree(tmp, ignore_errors=True)
    print(f"\n通过 {PASS}，失败 {FAIL}")
    return 0 if FAIL == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
