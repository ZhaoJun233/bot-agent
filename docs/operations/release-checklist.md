# 发布检查单（release checklist）

适用对象：准备给 Bot Agent 打版本 tag、发布 GitHub Release、把代码同步到公开镜像的人。
配套文档：`docs/operations/rollback-playbook.md`（回滚）、`docs/operations/upgrade-acceptance-dotnet10.md`（运行时升级验收）。

## 0. 三条纪律

1. **版本只有一个真源**：`Directory.Build.props` 的 `<Version>`。任何 csproj 里再写 `<Version>` 都会压过它，
   ArchitectureProbe（`ReleaseGovernanceChecks`）与 CI 都会判红。
2. **tag 即发布**：`vX.Y.Z` 必须与 `<Version>` 一致。CI 在 tag 构建上断言这件事，
   不一致就直接失败——所以「改版本号 → 打 tag」这个顺序不能反。
3. **发布前留回滚点**：镜像 `qqchat-agent:prev` + 数据库快照（`VACUUM INTO`）。
   详见 `docs/operations/rollback-playbook.md`，不要临时发明回滚方式。

## 1. 发版步骤

| # | 动作 | 检查点 |
| --- | --- | --- |
| 1 | 确认 `git status` 干净、目标提交已过 CI（9 项检查全绿） | `gh api repos/ZhaoJun233/bot-agent/commits/<sha>/check-runs` |
| 2 | 改 `Directory.Build.props` 的 `<Version>` 为本次版本 | 形如 `1.3.0`，不带 `v`、不带后缀 |
| 3 | 把 `CHANGELOG.md` 的 `[Unreleased]` 改成 `[X.Y.Z] - YYYY-MM-DD`，并新开一个空的 `[Unreleased]` | 段内按 Added / Changed / Fixed / Security 分组 |
| 4 | 本地跑 `dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net10.0/BotAgent.ArchitectureProbe.dll` | 必须全绿（含版本单一真源、CHANGELOG、dependabot、镜像 digest 断言） |
| 5 | 提交（提交信息写清版本与范围） | 不要夹带无关改动 |
| 6 | `git tag vX.Y.Z && git push <远端> vX.Y.Z` | tag 推送会触发 CI（`on.push.tags: ['v*']`） |
| 7 | 等 tag 构建的 `Build and synthetic probes` 绿 | 这一步就是在验证「tag 与 `<Version>` 一致、`--version` 与 props 一致」 |
| 8 | 打 GitHub Release（正文可直接取 CHANGELOG 对应段） | `gh release create vX.Y.Z --notes-file ...` |
| 9 | 同步公开快照：`python publish-qqchat.py --push --branch Zhaodev` | 脚本会做两道脱敏审计；**先 commit 再发布** |
| 10 | 部署后用 `curl /healthz` 核对线上版本 | 返回 `{"status":"ok","version":"X.Y.Z"}`，与 tag 一致 |

> `/healthz` 的版本来自程序集（`PanelRoutes` 所在程序集的 `AssemblyVersion`），
> `--version` 走同一条来源：两者一致不是运气，是同一个真源。

## 2. CI 里已经固化的断言

| 断言 | 位置 | 失败意味着 |
| --- | --- | --- |
| 全仓只有一个 `<Version>` 且在 props 里 | ArchitectureProbe `ReleaseGovernanceChecks` + CI `Version single source` 步骤 | 有 csproj 又写了版本号 |
| `--version` 输出与 props 一致 | CI `Version single source` 步骤 | 代码里又硬编码了版本 |
| tag 与 props 一致（仅 tag 构建） | 同上 | 忘了先改 `<Version>` |
| CHANGELOG 有 `## [Unreleased]` 段 | ArchitectureProbe | 忘了维护变更日志 |
| dependabot 覆盖 nuget / github-actions / npm | ArchitectureProbe | 配置文件被删或写错生态名 |
| Dockerfile 的 FROM 全部带 `@sha256:` | ArchitectureProbe | 新加的 Dockerfile 用了浮动 tag |
| compose 里外部镜像全部带 digest | ArchitectureProbe | 改 compose 时把 digest 去掉了 |

## 3. 基镜像 digest 怎么升

原则：**tag 是给人看的，digest 才是给构建用的**。同一行里同时写 tag 与 digest：
`FROM mcr.microsoft.com/dotnet/runtime:10.0@sha256:...`——tag 说明意图，digest 锁定字节。

```sh
# 1) 解析当前 tag 对应的 digest（能直连 registry 时）
docker buildx imagetools inspect mcr.microsoft.com/dotnet/runtime:10.0 --format '{{json .Manifest}}' | head -c 200

# 2) 不能直连 Docker Hub 时：经任一镜像站解析，并**至少交叉核对两个来源**，
#    再把 digest 写回 Dockerfile / docker-compose.yml
# 3) 改完必须验证「按 digest 能拉到」且「tag 当前仍指向这个 digest」
```

当前钉住的基镜像（换版本时这张表要一起改）：

| 文件 | 镜像 | digest |
| --- | --- | --- |
| `Dockerfile.app` | `mcr.microsoft.com/dotnet/runtime:10.0` | `sha256:a3f9f819…0f30` |
| `src/BotAgent.Headless/Dockerfile` | `mcr.microsoft.com/dotnet/sdk:10.0`（build） | `sha256:de4fe90a…c02b` |
| `src/BotAgent.Headless/Dockerfile` | `mcr.microsoft.com/dotnet/runtime:10.0`（runtime） | `sha256:a3f9f819…0f30` |
| `tests/BotAgent.IntegrationHarness/Dockerfile` | `mcr.microsoft.com/dotnet/sdk:10.0` / `runtime:10.0` | 同上 |
| `tools/tts.Dockerfile` | `python:3.11-slim` | `sha256:673719e0…df60` |
| `tools/tts-cloud.Dockerfile` | `python:3.11-alpine` | `sha256:09d17d5e…0ed2` |
| `docker-compose.yml`（napcat） | `mlikiowa/napcat-docker:latest` | `sha256:696e9574…0fa8b` |

**例外（已写进 compose 注释与探针豁免）**：`qqchat-agent:latest` 与 `qqchat-tts:latest` 是本仓库
自己构建的产物，没有远端 digest 可钉；部署代码（`PanelDeploymentPlan` / `PanelDeploy` / 多个探针）
按名字引用它们，改名会连带改一大片。要可复现就按提交号重建，而不是指望镜像名。

> dependabot 的 docker 生态**暂未开启**：3 个 compose 镜像里有 2 个是本地构建产物，
> dependabot 解析不了。基镜像升级按本节手工做，做完顺手更新这张表。

## 4. 依赖快照

CI 的 `verify` job 每次都会产出 `dependency-snapshot` artifact（`dotnet list BotAgent.slnx package
--include-transitive` 的全文，保留 30 天）。

- 想回答「当时依赖是什么」→ 下载对应 run 的 artifact，而不是去翻 commit。
- 仓库**不**维护 `packages.lock.json`：30 个 csproj 逐个维护 lock 会和 CI 的 restore 流程打架，
  可复现性靠 digest + NuGet 审计门 + dependabot + 这个快照。

## 5. CHANGELOG 规矩

- 只维护 `[Unreleased]` + 最近两个已发布版本；更早的留在 GitHub Releases（本文件不重复搬运）。
- 每条尽量带 issue 号（`#122`）与「现象 → 结果」，不写实现细节流水账。
- 面向外部读者：不写内部路径、不写真实群号/IP/域名/凭据。

## 6. 发布前最后一眼

- [ ] `git status` 干净，tag 指向预期提交
- [ ] `Directory.Build.props` 的 `<Version>` == tag（去掉 `v`）
- [ ] `CHANGELOG.md` 有本次版本段 + 新开的 `[Unreleased]`
- [ ] 22 个探针全绿（至少 ArchitectureProbe / SafetyProbe / ReviewRemediationProbe）
- [ ] 集成 harness 全绿（`通过 N，失败 0`）
- [ ] 前端 `npm run typecheck && npm run test:unit` 通过
- [ ] 已留回滚点（`qqchat-agent:prev` + 数据库快照）
- [ ] 部署后 `curl /healthz` 的 version 与 tag 一致
