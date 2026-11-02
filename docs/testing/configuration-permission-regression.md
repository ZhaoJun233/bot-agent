# 配置与权限回归测试矩阵（#111）

> 这一页回答一个问题：**配置保存、模型三阶段、白名单与冲突语义，现在由哪些自动化用例钉住？**
> 新增/改动这些链路时，先在这里找到对应的用例并跑它；不要只跑「看起来相关」的那一个。

## 1. 判定口径

| 层 | 用什么 | 放哪 | 进 CI 的方式 |
| --- | --- | --- | --- |
| 纯函数 / 契约 | `ReviewRemediationProbe` 的进程内断言 | `tests/BotAgent.ReviewRemediationProbe/` | `Build and synthetic probes` |
| 真实宿主端到端 | `IntegrationHarness` 的 S 场景 | `tests/BotAgent.IntegrationHarness/` | 3 个 `Synthetic integration test` 矩阵 |
| 前端交互 | Playwright | `src/web/tests/e2e/` | 4 个 `Browser end-to-end` 矩阵 |
| 真实配置库读写 | 集成场景内的合成 data dir + 断言回读 | 同 S 场景 | 同上 |

全部用例只使用合成数据（`MockOpenAi`、`MockProtocol`、合成的 data dir），不连真实群聊与真实模型。

## 2. 覆盖矩阵

| 需求 | 用例 | 断言要点 |
| --- | --- | --- |
| 配置事务成功 | S21 模型配置、S54 ZBA 保存、`configuration Core surface` | 提交后 `configurationResult.currentVersion` 前进、回读值一致 |
| 配置事务失败不半写 | `--configuration-whole-rollback`（整批回滚）、`--configuration-module-atomic-fault`（失败注入 6 条） | 注入异常后整批字段保持旧值，不留中间版本 |
| 失败可分类 | `configuration Core surface` 的 HTTP 映射 | ValidationFailed→400、VersionConflict→409、Forbidden→403 |
| 模型三阶段：保存 | S21 第 2 段 | 回执 `commitState=1`，`ModelProjection` 步 `state=1`（热应用） |
| 模型三阶段：回读 | S21 第 4 段 | settings json 有 baseUrl/模型名、secrets 表有密钥、密文不落 settings |
| 模型三阶段：重启/热生效 | S21 第 3/6 段、S61 | 不重启下一请求打到新端点且带新密钥；重启后面板值压过环境变量 |
| 供应商集合热重建 | S61 | 登记/删除供应商后不重启即换路由，回执 `step=2 state=1` |
| 旧入口版本 | S62 | 带版本匹配提交、陈旧版本 409、不带版本标 `legacy_unverified` |
| 白名单解析口径 | `whitelist policy semantics`（14 条） | 空=全拦、`*`/all/ALL、分隔符、去重、非法 token 丢弃、越界丢弃 |
| 白名单增删 | S63 第 ②/④ 步 | 面板写入名单后不重启即生效；`*` 任意放行 |
| 白名单清空 | S63 第 ⑤ 步 | 清空新名单 ⇒ 回落旧单名单（不是全拦） |
| 白名单去重 | S63 第 ③ 步 | 重复/空白/中文逗号混写只处理一次 |
| 群/私名单不互相覆盖 | S3（环境变量档）、S63 第 ⑥ 步（面板档） | 群号填群聊名单不放行同号私聊；私聊名单不影响群聊 |
| 官方通道空名单=全收 | `OfficialChannelScenario` | 两份官方名单都空时全部接受（与私域「空=全拦」相反） |
| 结构化平台策略 | S52（迁移/优先级/静音/停用/持久化）、`--configuration-platform-compatibility` | 条目字段压过标量开关；旧键兼容不误判 |
| 飞书旧名单兼容 | `--configuration-feishu-whitelist` | 有名字的实例目标存在时通道级 key 判定失败关闭 |
| 冲突：并发不静默覆盖（ZBA） | S54、`configuration.spec.ts`「两个浏览器上下文同时编辑，后保存方收到 409」 | 陈旧 `expectedVersion` → 409，不回写 |
| 冲突：并发不静默覆盖（旧入口） | S62 | 旧入口补 CAS 后陈旧版本 409；不带版本显式标 `legacy_unverified` |
| 冲突：未知结果可区分 | `configuration.spec.ts`「UnknownCommit 冻结」「核对矛盾响应」 | 未知态不显示成功、给出核对入口 |
| 前端：模型设置 | `model-and-whitelist.spec.ts` 第 1 例 | 能力弹窗改模型字段 → 保存回执 → ZBA 回读一致 |
| 前端：白名单管理 | `model-and-whitelist.spec.ts` 第 2 例 | 能力弹窗改群聊白名单 → 保存回执 → ZBA 回读一致 |

## 3. 怎么跑

```bash
# 纯函数 / 契约（含白名单解析与新模块）
cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.ReviewRemediationProbe/bin/Release/net10.0/BotAgent.ReviewRemediationProbe.dll"

# 单个集成场景（先 build Headless 与 harness）
BOTAGENT_IT_ONLY=s63 cmd //c "chcp 65001 >nul && dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll"

# 前端两条端到端（chromium）
cd src/web && npx playwright test --project=chromium tests/e2e/model-and-whitelist.spec.ts
```

## 4. 已知未覆盖 / 刻意不做的

- **失败注入是进程内合成异常**（`--configuration-module-atomic-fault` 注入 `synthetic` 异常），不是真的磁盘满 / 数据库锁死；后者属 `#121` 的备份与恢复演练范围。
- **白名单判定的时间窗**：S63 断言「改完下一条消息生效」，不覆盖「保存瞬间正在处理中的那条消息」的边界（热替换按请求读快照，不做在途重判）。
- **官方通道 / 飞书通道的端到端**：官方通道有 `OfficialChannelScenario`，飞书只有契约与单测；真连开放平台需要外部凭据，不进 CI。
- **前端只覆盖两条主路径**：模型字段与群聊白名单；其余字段复用 `configuration.spec.ts` 的通用保存/冲突机制，不逐字段写 e2e。
- **并发压测**：只覆盖两个客户端各存一次的确定性冲突，不做多客户端压力测试（属 `ConcurrencyStressProbe` 的调度范围）。

## 5. 相关

- `docs/operations/ci-quality-gates.md`：9 项必需检查与不稳定登记表。
- `docs/baseline/report-2026-10-31.md`：全量基线数字与时点。
- `docs/architecture/platform-policy-resolution.md`：白名单/平台策略的判定顺序（改语义先读这页）。
- `docs/architecture/model-configuration-apply-path.md`：模型配置从保存到发送的三段链路。
