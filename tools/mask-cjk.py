#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把日志行里的中文（发言、昵称、群名…）换成占位符 —— 排查时**只看机器字段**。

为什么要有它：排查机器人问题时大家习惯 `tail 日志`，而日志里混着群聊正文。
这个过滤器把非 ASCII 的连续片段整体替换掉，留下的就是时间戳 / 标签 / 计数 / 耗时 /
HTTP 状态码 / 异常类型这类**排错真正需要的东西**，而发言内容一个字都读不到。

用法（配合 grep，只挑错误行）：

    grep -aE 'ERROR|Exception|Unhandled|Traceback' /opt/qqchat/data/logs/qqchat.log \
      | tail -30 | python3 tools/mask-cjk.py

    # 只看结构（连 CJK 占位符都省了，只保留长度）
    python3 tools/mask-cjk.py --drop   # 直接把非 ASCII 片段删掉

参数：
    --drop        删掉非 ASCII 片段（不打印 <CJK> 占位符）
    --max N       每行最多打印 N 个字符（默认 220）
    --keep-hanzi  反悔开关：什么都不替换（只在确认这行没有对话正文时才用）

注意：它是**过滤器**，不是备份 —— 原始日志内容不会被它写进任何文件。
"""
import argparse
import re
import sys

# 连续的非 ASCII 片段（中文/表情/全角标点）整体替换，避免把“一半的昵称”留在输出里
NON_ASCII = re.compile(r"[^\x00-\x7f]+")


def main() -> int:
    parser = argparse.ArgumentParser(description="遮住日志里的中文，只留机器字段")
    parser.add_argument("--drop", action="store_true", help="直接删掉非 ASCII 片段")
    parser.add_argument("--max", type=int, default=220, help="每行最多打印多少字符（默认 220）")
    parser.add_argument("--keep-hanzi", action="store_true", help="不替换（除非确认这行没有对话正文）")
    args = parser.parse_args()

    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    for line in sys.stdin:
        text = line.rstrip("\n")
        if not args.keep_hanzi:
            text = NON_ASCII.sub("" if args.drop else "<CJK>", text)
        if args.max > 0 and len(text) > args.max:
            text = text[: args.max] + "…"
        sys.stdout.write(text + "\n")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
