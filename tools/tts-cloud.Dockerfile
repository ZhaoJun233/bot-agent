# 云端 TTS 旁路服务的镜像定义（约 60MB，常驻内存 ~20MB —— 因为**不装任何模型**）
#
# 构建（在仓库根目录）：
#     docker build -t qqchat-tts:latest -f tools/tts-cloud.Dockerfile tools/
#
# 跑起来：docker-compose.yml 里的 tts 服务就是它（改那里的 TTS_PROVIDER / KEY 即可）
#
# 验证（不经过机器人）：
#     curl -s http://127.0.0.1:5010/health
#     curl -s -o /tmp/t.wav "http://127.0.0.1:5010/speak?text=%E4%BD%A0%E5%A5%BD&voice=male-qn-qingse"
#     file /tmp/t.wav        # 应当是 RIFF WAVE（面板「试听一句」就靠这个格式）
#
# 和旧版（tools/tts.Dockerfile，Piper 本地模型）的区别：
#   旧版 490MB 镜像 + 200MB+ 常驻内存 + 每句几百毫秒~几秒 CPU 推理；
#   本版 ~60MB 镜像、~20MB 内存、零 CPU 推理（都交给云端），音色也更自然。
FROM python:3.11-alpine

# 可选：官方商用通道发语音要**腾讯 SILK v3**，而 NapCat（私域）那边自己会转，不需要。
# 需要时用构建参数把 silk_v3_encoder 塞进来（例如 kn007/silk-v3-decoder 的静态产物）：
#     docker build --build-arg SILK_ENCODER_URL=<直链> -t qqchat-tts:latest -f tools/tts-cloud.Dockerfile tools/
# 不塞也能跑：/speak?...&format=silk 会明确回 501 并说明原因（不会静默发坏音频）。
ARG SILK_ENCODER_URL=""
RUN if [ -n "$SILK_ENCODER_URL" ]; then \
        wget -qO /usr/local/bin/silk_v3_encoder "$SILK_ENCODER_URL" \
        && chmod +x /usr/local/bin/silk_v3_encoder \
        && /usr/local/bin/silk_v3_encoder 2>&1 | head -3 || true; \
    fi

WORKDIR /app
COPY tts-cloud-server.py /app/tts-cloud-server.py

ENV PORT=5000 \
    CACHE_DIR=/cache \
    DEFAULT_FORMAT=wav \
    PYTHONUNBUFFERED=1

EXPOSE 5000

HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -qO- http://127.0.0.1:5000/healthz || exit 1

ENTRYPOINT ["python", "/app/tts-cloud-server.py"]
