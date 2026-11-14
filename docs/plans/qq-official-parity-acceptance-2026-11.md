# QQ 官方基础能力验收

2026-11-14。此文件是公开验收摘要；专用账号、原生标识、凭据、本机路径和完整运维记录不随源码发布。

## 状态

P1–P3 基础能力及 P5 能力原因展示已完成。指定应用的专用官方群和独立私聊分别验证了文本、图片、语音、本人消息撤回及引用显示，用户确认实际表现。P5 抽屉与原因详情同样已获确认。

这是按能力签收：role/bot_state/ban/kick、Markdown/keyboard/menu 缺少逐项官方授权，继续 Deferred；可选 Webhook 未纳入。基础消息成功不意味着管理权限获准。真实行为使用固定受控输入，文本与撤回调用产品 OfficialSessionQqActionHost；没有声称测试真实 LLM 自主决策或生产迁移。

本次发布在受测官方功能基础上保留 dev 已有的段落、括号过滤、静默回复及积压引用修复。下述现场记录对应合并前的功能候选；发布组合另行构建和回归，最终结果见该提交的 CI。它不代表发布组合已部署到生产。

集成编号说明：保留 dev 既有 s66（积压引用），官方身份场景在发布组合中改为 s79；历史功能候选中的官方 s66 结果不改写。

## 证据口径

- I：OfficialIdentityProbe，Windows 361/0、Linux 358/0；差额为 Windows 恢复专用检查。
- H：IntegrationHarness 官方场景 s42/s66–s78、角色 / 审批 / 平台开关场景。最终功能候选 Linux 宿主 451/0。
- L：Linux Docker 无网络回归：Safety 737/0、Memory 33/0、Pacing 95、Lifecycle 221/0、EngineWiring 137/0、Snapshot 19；8 个候选命令及容器退出 0。
- W：前端 80 文件、1104 个单元 / 组件检查；三浏览器新增功能 E2E 3/0，包含焦点恢复、375px 窄屏和无执行写请求。
- X：附加配置 E2E 9/0（含 nameRaw、保存读回并恢复）；JSON→Vue→JSON 安全整数边界 3/0。
- R：最终功能候选真实固定序列共 8 次消息 POST、4 次上传、2 次本人消息 DELETE；均 HTTP 200、无消息重试，用户确认图片、声音、撤回和引用显示。

编号引用上述证据层次，原始报告保留在项目维护者的私有验收记录。每行 Pass（合成）只代表相应反例执行通过，不把破坏性模拟写成真实群操作；重叠用例不重复计数。

## T01–T31 覆盖映射

测试文件路径均相对于上述工作树；探针完整目录为 tests/BotAgent.OfficialIdentityProbe，宿主场景完整目录为 tests/BotAgent.IntegrationHarness。

| ID | 结果 | 实际落点与证据 |
| --- | --- | --- |
| T01 | Pass（合成）；可靠真实入站通过 | TypedIdentityChecks、ConnectionChecks、WindowsRecoveryChecks 的重启 / 重连 / 世代分离；X 的 identity-roundtrip.test.ts 覆盖 8000000000000001、8999999999999999、9007199254740991；R 分开绑定群与 C2C。 |
| T02 | Pass（合成） | TypedIdentityChecks 的 application/type/group/direction 分离、反向查找拒绝；GatewayIdentityChecks 的目标检查在 HTTP 之前；I/L。 |
| T03 | Pass（合成） | GatewayIdentityChecks 的 malformed fields 零身份分配、adapter 不可靠输入拒绝；OfficialIdentityScenario、OfficialRuntimeIdentityScenario（s79/s67）；I/H/L。 |
| T04 | Pass（合成） | TypedIdentityChecks 的不透明字符串不合并、GroupMember/C2cUser 分离；MemberRoleChecks 的应用 / 群边界与无原生后缀显示；I/L。未启用跨通道身份关联。 |
| T05 | Pass（合成） | TypedIdentityChecks、MigrationCommandChecks：来源未证实旧条目保留、批量迁移、陈旧预览拒绝、落盘失败回滚、降级保留 alias、冲突拒绝、并发与硬终止；I/L。生产数据迁移未执行。 |
| T06 | Pass（合成） | MemberRoleChecks：降权、同时间降权、过期、移除水位、乱序、应用退休与持有证据再校验；s68；I/H/L。真实管理能力仍独立 Deferred。 |
| T07 | Pass（合成） | SelfIdentityChecks：可信 self、mentions、is_you 不独立授予身份、超时负缓存、重连与移除失效；其它 bot 按用户已定契约保留普通入站，不仅凭 bot=true 丢弃；I/L。 |
| T08 | Pass（合成） | DeliveryLedgerChecks：群五份预算、C2C 四份预算与六十分钟、事件固定窗口、时间回退不延长；I/L。真实序列没有越额试探。 |
| T09 | Pass（合成） | DeliveryLedgerChecks 的并发 prepare/begin；ReplyBindingChecks、RuntimeDeliveryChecks 保持旧任务原触发；I/L。 |
| T10 | Pass（合成） | ApiResultChecks、DeliveryLedgerChecks、MessageDedupChecks：重复 / 未知不换序号重放、一次提交、重启去重；s69/s70/s73；I/H/L。 |
| T11 | Pass（合成） | ApiResultChecks 按 HTTP / 业务码区分凭据、11253、窗口、内容、限流、关系移除 / 拒收及 Unknown；仅明确凭据拒绝允许一次同触发重试；I/L。没有真实封禁或拒收试验。 |
| T12 | Pass（合成） | DeliveryAttemptChecks、RuntimeDeliveryChecks：上传和消息分开记尝试、到期禁止最终发送、撤权阻止续传、Unknown 上传终态、跨场景拒绝；I/L。 |
| T13 | Pass（合成）；群 / 私聊显示 Accepted | ReferenceChecks / s71：引用索引与被动触发独立、未知 / 跨会话拒绝、缺索引只在确认发送后降级；R 的私聊和群引用均 Success，用户确认显示。 |
| T14 | Pass（合成） | RelationshipChecks / s72：好友移除、上传期间移除、恢复不复活旧任务、通知开关不误停被动回复、成员退出不关闭整群；I/H/L。 |
| T15 | Pass（合成）；基础媒体 Accepted | ContentDeliveryChecks、MusicReceiptChecks、HostMediaChecks、s74/s75：确认发送与降级分开，Unknown 不变成功，私域对照；R 的群 / 私聊图片和语音用户确认正常。真实受限链接与音乐未额外发送。 |
| T16 | Pass（合成）；当前会话工具边界 Accepted | s76/s77/s78、OfficialSessionQqActionHost 接线覆盖 Agent / 工具 / 插件闸口；R 的文本和本人撤回直接调用相同产品工具宿主。未接入的插件任意目标动作继续拒绝。真实 LLM 自主决策链未运行。 |
| T17 | Pass（合成） | s76/s77 的数字目标、跨群 / 跨平台 / 跨应用、伪造参数拒绝；GatewayIdentityChecks、OwnRecallChecks 的类型与出站所有权证明；I/H/L。 |
| T18 | Pass（合成） | s78 的继承 / 空表 / all 及插件开关；MessagingCapabilityChecks 只投影策略而不授予外部权限；I/H/L。 |
| T19 | Pass（合成） | DeliveryAuthorizationChecks、s78、ReplyCommitBoundary 原边界反例：模型看见工具后撤权、上传前后 / 凭据重试 / 落盘等待仍重校验；I/H/L。 |
| T20 | Pass（合成）；实际成功回执核对 | s70/s76/s77/s78：结果不由字符串“拒绝”误包装成功，Unknown / Rejected / Degraded 分开；R 的最初撤回 Unknown 保留，新请求修复后成功，不覆盖旧终态。 |
| T21 | Deferred | 缺少 role/bot_state/ban/kick 的逐项应用授权、目标角色和本项目操作授权证据；未开放管理入口。基础消息成功不是这些权限的证据。 |
| T22 | Deferred | 未开放禁言 / 解禁。待获准后再实现和验证时长、add/update/del、RFC3339 及执行前角色变化，当前无对应成功声明。 |
| T23 | Deferred | 未获单人移除的内邀 / 应用权限；未执行移除、批量或拉黑。 |
| T24 | Deferred | Markdown / keyboard / menu / interaction 授权未核实；没有开放按钮动作或 ACK 入口，不以静态演示当真实交互。 |
| T25 | Pass（合成）；产品视觉 Accepted | MessagingCapabilityChecks、official-messaging 组件 / E2E、messaging-capabilities 解码；W/X 覆盖已实现与未知授权分离、刷新撤权、旧响应拒绝、无写请求、nameRaw 保存读回、窄屏和焦点；用户已确认。 |
| T26 | Pass（合成） | s79/s67/s68、GatewayIdentityChecks 的可靠身份闸口与包装后校验；官方记忆和治理不借私域身份；I/H/L。 |
| T27 | Pass（合成及交付检查） | ApiResultChecks 以含合成正文 / query / token 的拒绝及异常检查诊断输出；DeliveryLedgerChecks 确认日志账本不存原生 ID / 正文，ContentDeliveryChecks 确认 JSON 隐藏已发文字；真实回执只导出状态 / 长度 / 哈希 / 计数。 |
| T28 | Pass（合成） | DeliveryAuthorizationChecks、ReplyBindingChecks、s78 和既有 Pacing / Safety：来源停止 / 退休、准备到提交之间取消；提交后诚实 Unknown；I/H/L。 |
| T29 | 未纳入 | Webhook 是可选入口；未申请独立回调配置或切换，保持 WS。没有签名 / ACK / 双入口验收声明。 |
| T30 | Accepted（本次基础能力） | 最终候选 Windows、Linux Docker 与 R 的专用官方群 / 私聊分开验证，所有产品 DLL 对应最终清单；图片 / 语音 / 撤回 / 引用均有用户观察。受限项按 T21–T24 延期。 |
| T31 | Pass（合成） | DeliveryLedgerChecks、DeliverySnapshotChecks、IndexedStateChecks：意图写失败不提交、阶段硬终止、尝试后恢复 Unknown、终态不重放、过期旧事件拒绝；I/L。原 r2 Unknown 也未进行自动重放。 |

## 保留的反例

首次实际撤回已发生，但旧解析将 HTTP 200 空对象误判为 Unknown。官方成功示例允许 {}；旧二进制对应新回归 38/4，修复后 42/0。解析仅对撤回端点接受 HTTP 200 且对象无字段；201/204、数组、null 和非空对象仍不认成功。后续群和私聊响应均为 2 字节空对象，程序回执及用户观察一致；旧 Unknown 未重放。

一次 Linux 完整序列在 s78 的配置请求遇到 10 秒 HttpClient 超时，原 426/1 保留。同产物独立 s78 为 26/0，再次原完整序列为 451/0；没有放宽超时、资源配额或断言，偶发原因尚未确定。

## 后续能力

T21–T24 需逐项官方授权、目标角色和项目操作授权，按能力独立实施和验收。Webhook 在独立传输需求和回调配置授权明确后进入 T29。角色事件证据不自动授予 Agent 审批权限。
