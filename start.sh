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
set -e

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PID_FILE="$DIR/runtime/bot-agent.pid"
LOG_DIR="$DIR/runtime/logs"
LOG_FILE="$LOG_DIR/bot-agent.log"
ENV_FILE="$DIR/.env"

mkdir -p "$DIR/runtime" "$LOG_DIR"

# 检查是否已在运行
if [ -f "$PID_FILE" ]; then
    PID=$(cat "$PID_FILE" 2>/dev/null || true)
    if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
        echo "Bot Agent 已在运行中 (PID: $PID)"
        echo "查看日志: tail -f $LOG_FILE"
        exit 0
    else
        rm -f "$PID_FILE"
    fi
fi

# 检查并载入 .env 文件
if [ -f "$ENV_FILE" ]; then
    echo "载入配置文件: $ENV_FILE"
    while IFS= read -r line || [ -n "$line" ]; do
        [[ "$line" =~ ^[[:space:]]*# ]] && continue
        [[ "$line" =~ ^[[:space:]]*$ ]] && continue
        if [[ "$line" =~ ^([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]]; then
            key="${BASH_REMATCH[1]}"
            val="${BASH_REMATCH[2]}"
            # 去除两端引号
            if [[ "$val" =~ ^\"(.*)\"$ ]]; then
                val="${BASH_REMATCH[1]}"
            elif [[ "$val" =~ ^\'(.*)\'$ ]]; then
                val="${BASH_REMATCH[1]}"
            fi
            export "$key"="$val"
        fi
    done < "$ENV_FILE"
else
    echo "[*] 未找到 .env 文件，正在从 .env.example 复制..."
    cp "$DIR/.env.example" "$ENV_FILE"
    echo "[!] 请先编辑 $ENV_FILE 填入必要参数（如 MODEL_API_KEY）后重新启动。"
    exit 1
fi

# 兼容映射标准 .env 变量到 QQCHAT_* 环境变量
export QQCHAT_BASE_URL="${QQCHAT_BASE_URL:-${MODEL_BASE_URL:-https://api.openai.com/v1}}"
export QQCHAT_API_KEY="${QQCHAT_API_KEY:-${MODEL_API_KEY:-}}"
export QQCHAT_MODEL="${QQCHAT_MODEL:-${MODEL_NAME:-gpt-4o-mini}}"
export QQCHAT_MAX_TOKENS="${QQCHAT_MAX_TOKENS:-${MAX_TOKENS:-2048}}"

export QQCHAT_ONEBOT_PROTOCOL="${QQCHAT_ONEBOT_PROTOCOL:-${ONEBOT_PROTOCOL:-ForwardWebSocket}}"
export QQCHAT_ONEBOT_URL="${QQCHAT_ONEBOT_URL:-${ONEBOT_URL:-ws://127.0.0.1:3001}}"
export QQCHAT_ONEBOT_TOKEN="${QQCHAT_ONEBOT_TOKEN:-${ONEBOT_TOKEN:-}}"

export QQCHAT_UIN="${QQCHAT_UIN:-${BOT_UIN:-}}"
export QQCHAT_WHITELIST="${QQCHAT_WHITELIST:-${WHITELIST:-*}}"
export QQCHAT_PERSONA="${QQCHAT_PERSONA:-${BOT_PERSONA:-}}"
export QQCHAT_AI_DESIRE="${QQCHAT_AI_DESIRE:-${AI_DESIRE:-50}}"
export QQCHAT_AI_MODE="${QQCHAT_AI_MODE:-${AI_MODE:-1}}"

export QQCHAT_GROUP_COOLDOWN="${QQCHAT_GROUP_COOLDOWN:-${GROUP_COOLDOWN:-8}}"
export QQCHAT_PRIVATE_COOLDOWN="${QQCHAT_PRIVATE_COOLDOWN:-${PRIVATE_COOLDOWN:-3}}"
export QQCHAT_IDLE_FALLBACK="${QQCHAT_IDLE_FALLBACK:-${IDLE_FALLBACK:-60}}"
export QQCHAT_SPLIT_REPLIES="${QQCHAT_SPLIT_REPLIES:-${SPLIT_REPLIES:-1}}"
export QQCHAT_SEGMENT_DELAY_MS="${QQCHAT_SEGMENT_DELAY_MS:-${SEGMENT_DELAY_MS:-700}}"

export QQCHAT_PANEL_PASSWORD="${QQCHAT_PANEL_PASSWORD:-${PANEL_PASSWORD:-}}"
export QQCHAT_PANEL_TOKEN="${QQCHAT_PANEL_TOKEN:-${PANEL_TOKEN:-}}"
export QQCHAT_DISABLE_PANEL_AUTH="${QQCHAT_DISABLE_PANEL_AUTH:-${DISABLE_PANEL_AUTH:-}}"
export QQCHAT_TLS_CERT="${QQCHAT_TLS_CERT:-${TLS_CERT:-}}"
export QQCHAT_TLS_KEY="${QQCHAT_TLS_KEY:-${TLS_KEY:-}}"
export QQCHAT_HEALTH_PORT="${QQCHAT_HEALTH_PORT:-${HEALTH_PORT:-18245}}"
export QQCHAT_DATA_DIR="${QQCHAT_DATA_DIR:-$DIR/runtime}"

export QQCHAT_NAPCAT_WEBUI_URL="${QQCHAT_NAPCAT_WEBUI_URL:-${NAPCAT_WEBUI_URL:-http://127.0.0.1:6099}}"
export QQCHAT_NAPCAT_WEBUI_TOKEN="${QQCHAT_NAPCAT_WEBUI_TOKEN:-${NAPCAT_WEBUI_TOKEN:-}}"

# 检查必填项
if [ -z "$QQCHAT_API_KEY" ] || [ "$QQCHAT_API_KEY" = "sk-xxxxxxxxxxxxxxxxxxxxxxxx" ]; then
    echo "[!] 错误: 未配置有效的 MODEL_API_KEY / QQCHAT_API_KEY。"
    echo "请在 $ENV_FILE 中设置 MODEL_API_KEY 后重试。"
    exit 1
fi

# 如果未设置 PANEL_PASSWORD，则控制面板为公开免密模式（可在面板设置中随时配置）
if [ -z "$QQCHAT_PANEL_PASSWORD" ]; then
    echo "[*] 提示: 未配置 PANEL_PASSWORD，面板将以免密模式运行。"
fi

# 自动检测与适配运行环境（标准 Linux / Termux）
IS_TERMUX=false
if [ -n "$TERMUX_VERSION" ] || [ -d "/data/data/com.termux/files" ]; then
    IS_TERMUX=true
fi

if [ "$IS_TERMUX" = true ]; then
    export PREFIX="${PREFIX:-/data/data/com.termux/files/usr}"
    [ -d "$PREFIX/lib/dotnet" ] && export DOTNET_ROOT="$PREFIX/lib/dotnet"
    export LD_LIBRARY_PATH="$PREFIX/lib${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
    # 针对 Android Bionic 环境补充原生 SQLite 符号链接
    if [ ! -f "$PREFIX/lib/libe_sqlite3.so" ] && [ -f "$PREFIX/lib/libsqlite3.so" ]; then
        ln -sf "$PREFIX/lib/libsqlite3.so" "$PREFIX/lib/libe_sqlite3.so" 2>/dev/null || true
    fi
else
    # 标准 Linux 发行版（Ubuntu, Debian, Fedora, Arch 等）
    if [ -z "$DOTNET_ROOT" ]; then
        if [ -d "/usr/share/dotnet" ]; then
            export DOTNET_ROOT="/usr/share/dotnet"
        elif [ -d "/usr/lib/dotnet" ]; then
            export DOTNET_ROOT="/usr/lib/dotnet"
        fi
    fi
fi

if ! command -v dotnet >/dev/null 2>&1; then
    if [ -n "$DOTNET_ROOT" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
        export PATH="$DOTNET_ROOT:$PATH"
    else
        echo "[!] 错误: 未检测到 dotnet 命令，请确保已安装 .NET 8.0 SDK 并加入 PATH。"
        echo "例如在 Ubuntu/Debian 上执行: sudo apt update && sudo apt install -y dotnet-sdk-8.0"
        exit 1
    fi
fi

PROJECT_FILE="$DIR/src/BotAgent.Headless/BotAgent.Headless.csproj"
DLL_FILE="$DIR/src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll"

# 如果产物不存在，先编译
if [ ! -f "$DLL_FILE" ]; then
    echo "首次运行，正在编译源码 (Release)..."
    dotnet build "$PROJECT_FILE" -c Release
fi

# 运行模式选择（直接从源码项目运行）
if [ "$1" = "--foreground" ] || [ "$1" = "-f" ]; then
    echo "正在以前台模式从源码运行 Bot Agent..."
    echo $$ > "$PID_FILE"
    trap 'rm -f "$PID_FILE"' EXIT INT TERM
    exec dotnet exec "$DLL_FILE"
fi

echo "正在以后台模式从源码运行 Bot Agent..."
nohup dotnet exec "$DLL_FILE" < /dev/null >> "$LOG_FILE" 2>&1 &
BOT_PID=$!
disown "$BOT_PID" 2>/dev/null || true
echo $BOT_PID > "$PID_FILE"

echo "正在启动服务，请稍候..."
STARTED=false
for i in $(seq 1 20); do
    if ! kill -0 $BOT_PID 2>/dev/null; then
        break
    fi
    if curl -s -m 1 "http://127.0.0.1:${QQCHAT_HEALTH_PORT}/healthz" 2>/dev/null | grep -q '"status":"ok"'; then
        STARTED=true
        break
    fi
    sleep 1
done

if [ "$STARTED" = true ]; then
    echo "[+] Bot Agent 启动成功 (PID: $BOT_PID)"
    echo "    Web 面板: http://127.0.0.1:${QQCHAT_HEALTH_PORT}/"
    echo "    运行日志: $LOG_FILE"
    echo "使用 './status.sh' 查看状态，'./stop.sh' 停止服务。"
else
    if kill -0 $BOT_PID 2>/dev/null; then
        echo "[!] 进程已启动 (PID: $BOT_PID)，正在后台初始化中..."
        echo "请稍后运行 './status.sh' 检查状态，或查看日志: $LOG_FILE"
    else
        echo "[!] Bot Agent 启动失败，请查看日志:"
        tail -n 25 "$LOG_FILE"
        rm -f "$PID_FILE"
        exit 1
    fi
fi
