# BotAgent {{TAG}}：Linux x64 + Docker

下载本版本全部六个附件（两个 `.tar.gz`、`Dockerfile.app.txt`、`BUILD-INFO.json`、`INSTALL.md` 和 `SHA256SUMS.txt`）到同一目录，先校验完整性：

```sh
sha256sum -c SHA256SUMS.txt
cp Dockerfile.app.txt Dockerfile.app
cp botagent-{{TAG}}-linux-x64.tar.gz app.tar.gz
docker build -f Dockerfile.app -t botagent:{{VERSION}} .
```

后端包为依赖 .NET 10 运行时的 `linux-x64` 产物；`Dockerfile.app` 已固定运行时镜像，服务器不编译源码。Web 包需由独立的静态 Web 服务（例如 Caddy）在 `/web/` 提供，不会自动打入后端镜像。

## 裸宿主与官方插件市场

- 本版本宿主内置 `9` 个 `system.*` 核心与平台包及 `19` 个 `contracts/*` 工程。
- 全新初始化的裸宿主默认预装 `0` 个功能插件，且启动阶段**不会**自动联网下载外部插件包。
- 13 项对话功能插件（`preset.feature.*`，`1.1.0`）已迁出至官方插件市场仓库 `ZhaoJun233/bot-agent-plugins`，请在 `/web/` 管理控制台「插件中心」按需安装或升级。详见[宿主分发架构](https://github.com/{{REPO}}/blob/{{TAG}}/docs/architecture/host-plugin-distribution.md)。

## 首次安装

准备权限为 `600` 的本地 `botagent.env`，填写 `BOTAGENT_API_KEY`、`BOTAGENT_BASE_URL`、`BOTAGENT_MODEL`、首次初始化密码 `QQCHAT_PANEL_PASSWORD`，以及独立的 `BOTAGENT_ZBA_TOKEN` / `BOTAGENT_ZBA_READ_TOKEN`。密码和令牌需自行生成且仅在部署环境配置。未初始化密码或模型配置缺失时按宿主安全边界受限/终止启动，完整字段与首次修改密码步骤见下方指南。

```sh
mkdir -p runtime
docker run -d --name botagent --restart unless-stopped \
  --env-file botagent.env \
  -e BOTAGENT_DATA_DIR=/data \
  -e BOTAGENT_HEALTH_PORT=8080 -e BOTAGENT_HEALTH_BIND=0.0.0.0 \
  -p 127.0.0.1:8080:8080 -v "$PWD/runtime:/data" botagent:{{VERSION}}
curl -fsS http://127.0.0.1:8080/healthz
```

`/healthz` 应显示 `version={{VERSION}}`。平台尚未连接时 `/readyz` 返回 `503`；完成平台连接后按就绪语义检查。Web 包解压到独立静态目录后，需保留按 `/web/` 与 `/api/` 划分的鉴权与 `Cache-Control` 策略。

完整首次安装指南：[Linux Docker 首次安装](https://github.com/{{REPO}}/blob/{{TAG}}/docs/operations/linux-docker-first-run.md)。

## 既有实例升级与回退

升级前务必同时保留：旧容器镜像回滚点（`qqchat-agent:prev`）、SQLite 一致性快照（`VACUUM INTO` 备份 `data/botagent.db`）、插件目录快照（`data/plugins/`）以及旧 Web 静态资源备份。流程遵循[发布检查单](https://github.com/{{REPO}}/blob/{{TAG}}/docs/operations/release-checklist.md)。

- 物理 SQLite `user_version` 为 `15`（`preset.token-quota` `14–15` 迁移保留于宿主）。
- 在 `v1.4.0` 与当前版本之间回退时，必须按[回滚预案](https://github.com/{{REPO}}/blob/{{TAG}}/docs/operations/rollback-playbook.md)成对恢复升级前备份的 `data/botagent.db` 与 `data/plugins/`，不得仅替换镜像；既有 `1.0.0` `host-distribution://` 状态封套可平滑升级至 `1.1.0` 或干净卸载。
- 基础 `Dockerfile.app` 不包含 Python 解释器；使用 Python 扩展时需另行提供可信解释器并将 `BOTAGENT_PYTHON_EXECUTABLE` 设置为容器内绝对路径，详见[Python Worker 协议](https://github.com/{{REPO}}/blob/{{TAG}}/docs/python-worker-protocol.md)。
- 合成回归与固定夹具测试结论不代表实际生产环境或真实外部平台凭据下的行为承诺。
