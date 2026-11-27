# 发版与 CD 操作检查单（Release Checklist）

本检查单定义 `ZhaoJun233/bot-agent` 的正式版本发布、GHCR 容器镜像交付、受控生产部署与紧急回退标准流程。

---

## 1. 发版前本地准备

1. **版本单一真源**：仅在 [Directory.Build.props](../../Directory.Build.props) 更新 `<Version>X.Y.Z</Version>`（禁止在子工程 `.csproj` 写死 `<Version>`）。
2. **更新变更日志**：在 [CHANGELOG.md](../../CHANGELOG.md) 将 `## [Unreleased]` 整理为 `## [X.Y.Z] - YYYY-MM-DD`，保留新的空 `## [Unreleased]` 段。
3. **本地门禁全绿**：
   - `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release`
   - `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release`
   - `python tests/BotAgent.ServerFilesProbe/test_server_files.py` 与 `python tests/BotAgent.ServerFilesProbe/test_cd_pipeline.py`
   - `src/web` 下执行 `npm run typecheck`、`npm run test:unit`、`npm run build`
4. **本地提交并脱敏推送至公开 `dev`**：在工作区运行 `publish-botagent.py` 干跑 0 命中后，经 `push-desensitized.py --branch dev` 推送并等待公开 `CI` 10/10 全绿。

---

## 2. 合并 `main` 与触发自动 CD 发版

1. 在公开仓发起 `dev -> main` PR，确认 `CI` 必需检查全绿并完成 Squash 合并。
2. 基于 `main` 最新提交打附注 Tag 并推送：
   ```sh
   git fetch origin main
   git tag -a vX.Y.Z origin/main -m "BotAgent vX.Y.Z"
   git push origin vX.Y.Z
   ```
3. **CD 流水线（[.github/workflows/cd.yml](../../.github/workflows/cd.yml)）自动执行**：
   - `verify-ci-gate`：核验同提交 SHA 的 `CI`（10/10 Job）全绿；
   - `build-artifacts`：调用 [build/package-release.py](../../build/package-release.py) 生成 6 件套资产包，并使用 [Dockerfile.app](../../Dockerfile.app) 启动容器执行 `/healthz` 冒烟验证；
   - `publish-release`：在 `release` Environment 发布 GitHub Release（`botagent-vX.Y.Z-linux-x64.tar.gz`、`botagent-vX.Y.Z-web.tar.gz`、`Dockerfile.app.txt`、`INSTALL.md`、`BUILD-INFO.json`、`SHA256SUMS.txt`）并重下载校验全部 SHA-256；
   - `publish-ghcr`：构建并推送 `ghcr.io/zhaojun233/bot-agent:X.Y.Z`、`:X.Y`、`:sha-<short>`（正式版同步更新 `:latest`）。

---

## 3. 生产部署审批与手动热修/回滚

1. **Tag 审批上线**：`publish-release` 与 `publish-ghcr` 完成后，`deploy-production` 会在 `production` Environment 暂停等待维护者审批；人工在 GitHub Actions 页面点击 Approve 后执行 [tools/deploy-remote.py](../../tools/deploy-remote.py) 完成双回滚点备份与无损上线。
2. **手动热修或仅构建**：在 Actions 页面运行 `CD` 工作流（`workflow_dispatch`）：
   - `action=build-only`：仅构建 6 件套产物（可选勾选 `publish_ghcr`），不动线上；
   - `action=deploy` + `scope=all|backend|web`：对指定分支（如 `dev`）经 `production` 审批后执行受控上线；
   - `action=rollback` + `rollback_stamp=<UTC>`（如 `20261126T065246Z`）：经 `production` 审批后直接调用远端双回滚快照恢复。
3. **上线后核对**：查看 `deploy-receipt` Artifact（仅含阶段状态、版本号、SHA-256 与 HTTP 状态码，零敏感泄露），并按[运维排障手册](runbook.md)巡检 `/healthz`、`/readyz` 与脱敏错误日志计数。

---

## 4. `production` Environment Secrets 清单

为保证公开仓库 Actions 日志零泄露，所有生产连接与站点参数必须存入 `production` Environment Secrets（严禁写入 Variables 或代码仓库）：

| Secret 名称 | 必需 | 说明（示例占位） |
| :--- | :---: | :--- |
| `DEPLOY_SSH_HOST` | 是 | 目标主机 IP 或域名（如 `203.0.113.10`） |
| `DEPLOY_SSH_USER` | 是 | SSH 登录用户名（具备免密 `sudo` 权限，如 `deploy`） |
| `DEPLOY_SSH_KEY` | 是 | SSH 私钥 PEM 原文 |
| `DEPLOY_SITE_HOST` | `web`/`all` 是 | Caddy 静态站点校验 `Host`（如 `panel.example.com`） |
| `DEPLOY_SSH_PORT` | 否 | SSH 端口，缺省 `22` |
| `DEPLOY_SSH_KNOWN_HOSTS` | 否 | 目标主机公钥指纹行（配置后启用严格主机指纹校验） |
| `DEPLOY_SSH_PROXY_JUMP` | 否 | 可选 SSH 跳板机（`user@bastion.example.com`） |
| `DEPLOY_SSH_PROXY_KEY` | 否 | 可选跳板机专用私钥（缺省复用 `DEPLOY_SSH_KEY`） |
| `DEPLOY_APP_DIR` | 否 | 后端部署根目录，缺省 `/opt/qqchat` |
| `DEPLOY_WEB_DIR` | 否 | 前端静态目录，缺省 `/var/www/botagent-web` |
