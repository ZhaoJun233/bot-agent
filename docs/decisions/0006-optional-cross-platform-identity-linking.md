# ADR-0006：可选的显式跨平台本人身份关联边界

- 状态：Phase 5（v2.0）实施基线；遵循 [ADR-0004](0004-unified-memory-authorization.md)、[ADR-0005](0005-memory-tool-coexistence-taint-tiers.md) 与仓库 [AGENTS.md](../../AGENTS.md) 全部隐私红线。
- 关联：`IdentityLinkModels`、`IdentityLinkStore`、`IdentityLinkService`、`IdentityCommandService`、`PersonalTodoHostPortsAdapter`、`KnowledgeHostPortsAdapter`、`WorkspaceHttpContract`。

---

## 1. 决策摘要

跨平台本人身份关联**默认关闭**，且**禁止**按昵称、用户名、邮箱或平台 ID 做任何猜测式或隐式自动合并。仅当本人明确在系统开启跨端绑定开关，并亲手在两个可信私聊端点之间完成一次性双向挑战-响应确认后，才允许把该两端点绑定到同一个逻辑 `CanonicalPersonId`。

## 2. 双向挑战-响应协议

1. **发起（`//identity link start`）**：仅限 `IsReliableIdentity == true` 的私聊对话；宿主在纯内存中生成 120 秒有效的单次随机 HMAC 挑战码，绑定源端点 `(PlatformId, AccountScope, InstanceId, NativeUserId)`。进程重启后所有未完成挑战自动作废。
2. **确认（`//identity link confirm <code>`）**：必须在另一条不同私聊端点由可靠身份提交；服务端校验挑战码存在、未过期、未使用且非同一端点，原子写入绑定账本并立即销毁挑战码（防重放、禁一码多用）。
3. **解除（`//identity unlink`）**：任意已绑定端执行解除命令后，仅解除当前端与同一 `CanonicalPersonId` 的关联（孤立单端自动回收），共享读写凭据即刻失效并退回本端私有作用域。

## 3. 允许与绝对禁止的共享边界

| 数据或权限类别 | 是否允许跨端共享 | 实现约束 |
| --- | :---: | --- |
| 个人待办（`personal_todos`） | 可选开启（默认随开关控制） | 仅在 `Enabled && ShareTodos` 时按 `shared:<id>` 读写，且仅限可靠私聊与本人 Web 工作台访问 |
| 个人知识库（`knowledge:scope`） | 可选开启 | 仅在 `Enabled && ShareKnowledge` 且当前为可靠私聊时映射至 `shared:<id>`；群聊知识库永不参与 |
| 私聊 L1/L2 偏好（`preferences`） | 可选开启 | 严格遵守 ADR-0005 敏感度分级，仅限显式确认后的个人偏好项 |
| 历史聊天记录与消息正文 | **绝对禁止** | `ConversationRegistry` / `Conversations` 物理键与消息内容永不跨平台互通或混读 |
| 群内 RBAC 角色（`GroupManager` 等） | **绝对禁止** | `AccessRoleResolver` 不消费 `IdentityLinkService` |
| 人类审批权与 `//` 运维提权 | **绝对禁止** | `ApprovalUseCase` 与 `AgentCommandService` 永远只按当次入站平台的原始可信身份裁决 |
| 统一记忆管理员（`BOTAGENT_UNIFIED_MEMORY_ADMIN`） | **绝对禁止** | 管理员身份由独立环境配置精确锁定，不因跨端绑定而扩展到其他平台或账号 |
