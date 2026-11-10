# Linux Docker 首次安装与配置

本指南使用独立前端和产物式镜像，适用于 Linux x64；开发机负责构建，服务器只解压产物。先在全新数据目录演练，再按部署变更流程选择线上候选。

## 1. 准备可核对的交付包

交付包应含 `app.tar.gz`、仓库根的 `Dockerfile.app`、前端 `src/web/dist` 产物，以及源码提交、目标 RID 和文件 SHA256 清单。开发机需 .NET 10 SDK、Node.js 22.22.2+（22.x）、24.15.0+（24.x）或 26+ 与 npm；服务器需 Docker、匹配产物的 .NET 运行时镜像，以及单独提供前端的 Caddy。服务器无需安装构建 SDK。

开发机在 PowerShell 7 构建，目录使用本次独有名称：

```powershell
dotnet publish src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -r linux-x64 --self-contained false -o .local/release-example/app
tar -C .local/release-example/app -czf .local/release-example/app.tar.gz .
Copy-Item -LiteralPath Dockerfile.app -Destination .local/release-example/Dockerfile.app
Set-Location src/web
npm ci
npm run typecheck
npm run test:unit
npm run build
```

记录每条命令的退出码。只有成功的 Linux 产物进入包；Windows DLL 的探针结果不代替 Linux 镜像验证。发布仍走独立的脱敏与 CI 门禁。

## 2. 在服务器建立独立实例

将包复制到新的目录，核对清单后用原 `Dockerfile.app` 构建版本化镜像：

```sh
docker build -f Dockerfile.app -t botagent-example:verified .
mkdir -p data
chmod 700 data
```

创建仅本机可读的 `.env`。下列值是合成示例，实际令牌与密码自行生成并只在私有配置中保存：

```dotenv
BOTAGENT_DATA_DIR=/data
BOTAGENT_HEALTH_PORT=8080
BOTAGENT_HEALTH_BIND=+
QQCHAT_PANEL_PASSWORD=synthetic-change-this-password
BOTAGENT_ZBA_TOKEN=synthetic-change-this-admin-token
BOTAGENT_ZBA_READ_TOKEN=synthetic-change-this-read-token
QQCHAT_API_KEY=sk-test-replace-locally
QQCHAT_BASE_URL=https://example.com/v1
QQCHAT_MODEL=replace-with-your-model
QQCHAT_IDLE_FALLBACK=0
```

`QQCHAT_PANEL_PASSWORD` 用于首次初始化；无密码时宿主强制绑定容器内的 127.0.0.1，单独设置 bind 或 ZBA 令牌不能使 Docker 发布端口可达。初始化后密码状态持久保存。旧密码认证入口是 `POST /api/auth/login` 与 `POST /api/auth/change-password`，后者使用 `currentPassword` / `newPassword`；Web 令牌界面与该密码认证分开。由维护者在私有认证会话中完成首次密码修改。管理员令牌与只读令牌是不同权限。

```sh
chmod 600 .env
docker run -d --name botagent-example --restart unless-stopped \
  --env-file .env -p 127.0.0.1:8080:8080 \
  --mount type=bind,src="$PWD/data",dst=/data \
  botagent-example:verified
```

首次安装无需挂载 Docker socket，也无需连接真实 QQ 协议端。资源限制按主机与实际工作负载设置；合成演练的限制不构成生产容量保证。

## 3. 单独提供前端

Headless 的 `/` 不提供页面。把前端 dist 复制到 Caddy 的静态目录，通过 `/web/` 提供；`/zba/*` 与 `/api/*` 反向代理到宿主。以下为只监听回环的最小示例，路径按本机替换：

```caddy
http://127.0.0.1:8081 {
    @root path /
    redir @root /web/ 302
    handle /web/assets/* {
        root * /srv/botagent-example/web
        uri strip_prefix /web
        header Cache-Control no-cache
        file_server
    }
    handle /web/* {
        root * /srv/botagent-example/web
        uri strip_prefix /web
        header Cache-Control no-store
        try_files {path} /index.html
        file_server
    }
    handle {
        reverse_proxy 127.0.0.1:8080
    }
}
```

缺失 JS 资源必须返回 404。公网入口另按既有 HTTPS、认证和缓存规则配置；首次可经 SSH 转发访问回环端口：

```powershell
ssh -L 8081:127.0.0.1:8081 user@example.com
```

浏览器打开 `http://127.0.0.1:8081/web/`，以只读令牌核对概览，再用管理员令牌进入管理模式。不要把令牌写进 URL 或截图。

## 4. 保存模型并完成一个受控任务

在配置页填写模型名称、端点与私有凭据，保存后同时核对提交回执、运行应用状态和重新读取的配置。模型投影失败时配置可能已经提交，应按原操作键核对，不能直接换键重发；见[配置应用路径](../architecture/model-configuration-apply-path.md)。

Web 任务只在服务器 Agent 已配置并启用后可执行。先在任务页创建或选择服务端真实存在的当前服务器会话，再选择明确的只读工具作用域；空 `toolScope` 会继承白名单。提交后保留幂等键与服务端任务 ID，核对终态及预期结果。重启后 Find 可返回 `accepted_unknown` 和原受理 ID，应读取该 ID 的权威状态，不能当作未受理重发。取消请求、任务收口和外部动作回滚分别判断；见[任务恢复指南](task-recovery-guide.md)。

若启用审批，先验证待批时执行器未调用，再批准同一请求。普通 QQ `//` 与聊天回复分别验收，不套用 Web Runtime 台账结论。

## 5. 升级与恢复

保存旧镜像 ID、源码与包哈希，按[回滚预案](rollback-playbook.md)完成升级前备份。当前数据库版本为 14；历史候选的版本闸另查，禁止只凭版本号推断可回退。

先在合成数据根验证：旧候选写入配置并完成任务；停写并保留一致快照；新候选复用该根，核对配置与原任务关联；停止新候选，在另一个新目录恢复快照并启动旧候选，再核对配置和任务。保留原失败和每次命令退出码。仅换旧镜像不等于恢复数据库。

本轮镜像不默认包含 Python。启用 Python Worker 前须验证所选解释器、依赖、实际 Worker 请求与进程组清理；Ubuntu 原生探针通过不能代替目标容器。插件启用还需对固定包进行来源、许可证、权限与哈希审核，并取得维护者批准。

首次操作时核对连接、配置保存回执、任务终态与恢复结果，并记录实际完成的检查。自动合成演练、维护者实际使用和界面确认是不同证据；未执行的检查保留为未验证。
