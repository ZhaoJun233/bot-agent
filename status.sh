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

echo "=== Bot Agent 运行状态 ==="
if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
        echo "[+] 状态: 运行中 (PID: $PID)"
    else
        echo "[-] 状态: 未运行 (PID 文件残留)"
    fi
else
    echo "[-] 状态: 未运行"
fi

# 动态自动发现面板监听端口：
# 1. 优先提取当前服务实际监听绑定的端口（日志证据）
DETECTED_PORT=""
if [ -f "$LOG_FILE" ]; then
    DETECTED_PORT=$(grep -oE '面板已监听 http://[^:]+:[0-9]+' "$LOG_FILE" 2>/dev/null | tail -n 1 | grep -oE '[0-9]+$')
    if [ -z "$DETECTED_PORT" ]; then
        DETECTED_PORT=$(grep -oE 'Web 面板[[:space:]]+http://[^:]+:[0-9]+' "$LOG_FILE" 2>/dev/null | tail -n 1 | grep -oE '[0-9]+$')
    fi
fi

# 2. 其次提取运行中进程的实际环境变量
if [ -z "$DETECTED_PORT" ] && [ -n "$PID" ] && [ -r "/proc/$PID/environ" ]; then
    DETECTED_PORT=$(tr '\0' '\n' < "/proc/$PID/environ" 2>/dev/null | grep -E '^(QQCHAT_HEALTH_PORT|HEALTH_PORT)=' | tail -n 1 | cut -d= -f2)
fi

# 3. 再次从 .env 中读取配置
if [ -z "$DETECTED_PORT" ] && [ -f "$DIR/.env" ]; then
    DETECTED_PORT=$(grep -E '^(QQCHAT_HEALTH_PORT|HEALTH_PORT)=' "$DIR/.env" 2>/dev/null | tail -n 1 | cut -d= -f2 | tr -d ' "\r\t')
fi

PORT="${DETECTED_PORT:-${QQCHAT_HEALTH_PORT:-${HEALTH_PORT:-18245}}}"
echo ""

if [ "$PORT" = "0" ]; then
    echo "=== HTTP 探针 ==="
    echo "[*] Web 面板与健康检查已关闭 (端口设为 0)"
else
    echo "=== HTTP 探针 (端口: $PORT) ==="
    HEALTH=$(curl -s -m 2 "http://127.0.0.1:${PORT}/healthz" 2>/dev/null || true)
    if [ -n "$HEALTH" ]; then
        echo "[+] 存活探针 /healthz: $HEALTH"
    else
        echo "[-] 存活探针 /healthz: 无法连接"
    fi

    READY=$(curl -s -m 2 "http://127.0.0.1:${PORT}/readyz" 2>/dev/null || true)
    if [ -n "$READY" ]; then
        echo "[+] 就绪探针 /readyz: $READY"
    else
        echo "[-] 就绪探针 /readyz: 未就绪 (尚未连接到 OneBot 协议端或服务未起)"
    fi
fi

if [ -f "$LOG_FILE" ]; then
    echo ""
    echo "=== 最近 10 行日志 ==="
    tail -n 10 "$LOG_FILE"
fi
