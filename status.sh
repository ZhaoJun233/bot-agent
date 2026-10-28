#!/bin/sh
# Universal trampoline for standard Linux (Ubuntu, Debian, CentOS, etc.) and Termux
if [ -z "$BASH_VERSION" ]; then
    if command -v bash >/dev/null 2>&1; then
        exec bash "$0" "$@"
    else
        echo "Error: bash is required." >&2
        exit 1
    fi
fi

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="$DIR/runtime/bot-agent.pid"
LOG_FILE="$DIR/runtime/logs/bot-agent.log"

echo "=== Bot Agent 运行状态 (Headless) ==="
if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
        echo "[+] 状态: 运行中 (PID: $PID)"
        echo "    模式: 纯无头服务（常驻守护）"
    else
        echo "[-] 状态: 未运行 (PID 文件残留)"
    fi
else
    echo "[-] 状态: 未运行"
fi

if [ -f "$LOG_FILE" ]; then
    echo ""
    echo "=== 最近 15 行日志 ==="
    tail -n 15 "$LOG_FILE"
fi
