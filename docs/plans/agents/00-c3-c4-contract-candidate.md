# C3/C4精确契约候选

> 版本：C3/C4-candidate-0.3，2026-10-10；**完整文档候选，未冻结**。替换已拒绝的0.2 surface，而不是在旧矛盾规则上叠errata。不存在可调用的新Core接口；不是代码、路由/OpenAPI、Wave1验收或生产授权。
> 认领/确认/状态唯一来源：[总纲第十八节](00-orchestration-and-dependency-graph.md)。事实输入：[完整字段矩阵](00-c3-c4-field-contract-review.md)，SHA `2FC8A1C55AA1E62A741EFC0D4B4F44DFC20CC86EB19584F355BA37F9CF1BC7E1`。旧0.2-review-1 SHA `F824B65510B9C7D65677B7CACB0C4D5C3C74830544877080855C1C60768B17DC`仅历史。
> 142非秘密输入、9秘密/clear兼容输入、48仅响应和170属性全部保留。既有入口/类型是源码事实；下列新类型、数值与规则全部候选。01/06早期消费评审及02～05确认仍待真实交付；不得拿00/旧设计顾问代签。

## 1. 使用步骤与完成条件

1. 可信Host adapter认证/授权→安全Read→依版本构造完整typed稀疏Patch/允许秘密→Save→响应丢失用原key Find。新请求永远有ExpectedVersion；旧HTTP特殊兼容见第6节。CLI只有Read。
2. 保存module隐藏纯归一、递归冻结、跨对象校验、唯一数据库协调、CAS、同连接配置/秘密/元数据/version/audit/receipt提交及提交后应用账本。Core不引用Host AppSettings/Platforms/SQLite；Storage只引用Core。复用真实SettingsBox先persist后publish、AppDatabase.Write(Action<SqliteConnection>)、SettingsStore/AuditLogStore同连接原语，不嵌套旧Save。
3. C4独立管理动作seam见第8节；配置Configure只走C3，动作/环境/外部文件不假装SQLite全局原子。TTS合成target须可注入且绝不fallback宿主挂载。
4. 完成条件是本具体hash各消费者逐项确认、兼容/迁移/回退及真实合成用例被认领；静态覆盖不证明实现通过。然后00登记freeze/代码准入。阶段一开工不等于Wave1准出，02/03实施仍等Wave1验收，Web仍等全部门禁及负责人明确解锁。

## 2. C3三入口与安全结果

以下C#块是精确成员/签名**surface文档**，无方法体、完整converter或实现，不应复制当可编译源码。

```csharp
namespace BotAgent.Domain.Configuration;
public interface IConfigurationChanges
{
    ValueTask<ConfigurationReadResult> ReadAsync(
        ConfigurationCaller caller, ConfigurationReadKind kind,
        CancellationToken ct = default);
    ValueTask<ConfigurationSaveResult> SaveAsync(
        ConfigurationCaller caller, ConfigurationSaveRequest request,
        CancellationToken ct = default);
    ValueTask<ConfigurationFindResult> FindAsync(
        ConfigurationCaller caller, ConfigurationOperationKey key,
        CancellationToken ct = default);
}
public enum ConfigurationReadKind { Overview = 0, Editable = 1 }
[Flags]
public enum ConfigurationAccess
{
    None = 0, ReadOverview = 1, ReadEditable = 2,
    SaveConfiguration = 4, EditAllowedSecrets = 8
}
public sealed record ConfigurationCaller(
    string AuthorityId, string SubjectId, string ResourceId,
    ConfigurationAccess Access);
public readonly record struct ConfigurationVersion(long Value);
public readonly record struct ConfigurationGeneration(Guid Value);
public readonly record struct ConfigurationOperationKey(
    ConfigurationGeneration Generation, long IssuedUnixSeconds, Guid Nonce);
public sealed record ConfigurationSaveRequest(
    string ContractRevision, ConfigurationVersion ExpectedVersion,
    ConfigurationOperationKey Key, ConfigurationPatch Changes,
    ImmutableArray<SecretEdit> Secrets);
public sealed record ConfigurationPatch(
    BehaviorChanges? Behavior, ModelChanges? Model, AgentChanges? Agent,
    PlatformChanges? Platform, VoiceChanges? Voice, MusicChanges? Music,
    ReportingChanges? Reporting, RegisteredConfigurationChanges? Registered);
public enum EditKind { Keep = 0, Set = 1, Clear = 2 }
public readonly struct FieldEdit<T>
{
    public EditKind Kind { get; }
    public static FieldEdit<T> Keep();
    public static FieldEdit<T> Set(T value);
    public static FieldEdit<T> Clear();
    public bool TryGetValue(out T value);
}
public enum ConfigurationResultCode
{
    Ok = 0, Forbidden = 1, ValidationFailed = 2, VersionConflict = 3,
    IdempotencyConflict = 4, KeyExpired = 5, KeyGenerationMismatch = 6,
    CapacityExceeded = 7, ClockRegression = 8, StorageUnavailable = 9,
    CommitFailed = 10, CancelledBeforeCommit = 11, UnknownCommit = 12,
    NoChanges = 13, NotFound = 14, LegacyNeedsReview = 15,
    UnsupportedContractRevision = 16
}
public enum ConfigurationDecision { Changed = 0, NoChanges = 1 }
public enum CommitState { NotCommitted = 0, Committed = 1, Unknown = 2 }
public enum ApplyState
{
    NotStarted = 0, Applied = 1, PartiallyApplied = 2,
    RestartRequired = 3, Failed = 4, Unknown = 5
}
public enum ApplyStep
{
    PublishSnapshot = 0, RuntimePolicy = 1, ModelProjection = 2,
    ConcurrencyGate = 3, ContextWindow = 4, Schedulers = 5,
    TtsFileProjection = 6, RegisteredConfiguration = 7
}
public sealed record ApplyStepReport(
    ApplyStep Step, ApplyState State, string? ErrorCode);
public sealed record ConfigurationReceipt(
    ConfigurationOperationKey Key, string ContractRevision,
    ConfigurationDecision Decision, ConfigurationVersion ObservedVersion,
    ConfigurationVersion? CommittedVersion, CommitState CommitState,
    ApplyState ApplyState, ImmutableArray<ApplyStepReport> Steps);
public abstract record ConfigurationObjectAddress
{
    private ConfigurationObjectAddress() { }
    public sealed record Device(DeviceKey Key) : ConfigurationObjectAddress;
    public sealed record Policy(PolicyTarget Target) : ConfigurationObjectAddress;
    public sealed record Plugin(PluginKey Key) : ConfigurationObjectAddress;
    public sealed record Provider(ProviderKey Key) : ConfigurationObjectAddress;
    public sealed record Secret(WritableSecretSlot Slot) : ConfigurationObjectAddress;
}
public sealed record FieldAddress(
    ConfigurationFieldId Field, ConfigurationObjectAddress? Object);
public sealed record FieldError(FieldAddress Address, string Code);
public sealed record ConfigurationSaveResult(
    ConfigurationResultCode Code, bool Replayed,
    ConfigurationReceipt? Receipt, ConfigurationVersion CurrentVersion,
    ImmutableArray<FieldError> Errors);
public sealed record ConfigurationFindResult(
    ConfigurationResultCode Code, ConfigurationReceipt? Receipt);
public enum CredentialSource { None = 0, Database = 1, Environment = 2 }
public sealed record SecretMetadata(
    WritableSecretSlot Slot, bool Configured, CredentialSource Source);
public enum FieldEffect
{
    Hot = 0, Restart = 1, ExternalProjection = 2, ReadOnly = 3
}
public sealed record FieldDescriptor(
    ConfigurationFieldId Id, FieldEffect Effect, bool CanClear,
    bool Editable, bool LegacyNeedsReview);
public enum FieldVisibility { Hidden = 0, Editable = 1, NameEditable = 2 }
public enum UserValueKind { Absent = 0, Inherited = 1, Explicit = 2 }
public enum ConfigurationValueSource
{
    Default = 0, UserConfiguration = 1, Environment = 2,
    LegacyCompatibility = 3, Unclassified = 4
}
public sealed class ReadField<T>
{
    public FieldVisibility Visibility { get; }
    public string Display { get; }
    public FieldDescriptor Rule { get; }
    public UserValueKind UserMode { get; }
    public ConfigurationValueSource EffectiveSource { get; }
    public ApplyState ApplyState { get; }
    public bool TryGetEditableValue(out T value);
}
public sealed record ConfigurationReadResult(
    ConfigurationResultCode Code, ConfigurationVersion Version,
    ConfigurationGeneration Generation, string ContractRevision,
    long ServerUnixSeconds, ConfigurationView? View,
    ImmutableArray<SecretMetadata> Secrets,
    ImmutableArray<FieldDescriptor> Fields, ApplyState LatestApplyState);
public sealed record ConfigurationView(
    BehaviorView Behavior, ModelView Model, AgentView Agent,
    PlatformView Platform, VoiceView Voice, MusicView Music,
    ReportingView Reporting, RegisteredConfigurationView Registered);
```

### 2.1 不变量与wire形式

- Caller只能由可信adapter构造并重新授权；body/URL自报AuthorityId/SubjectId/Access不成为grant。ResourceId唯一已登记`bot`。主体采用稳定服务身份，不使用IP/会话cookie作幂等身份；当前共享panel认证对应panel-owner，启动迁移对应system-startup。旧无密码allow不自动得到新ZBA grant；06认证/CSRF/跨域/流认证审批门禁不被此类型代替。
- 所有数组用ImmutableArray，default数组拒绝，empty可合法；所有元素getter/init-only、嵌套递归不可变。分区null=Keep，空对象全Keep。实现首次await前检查/深拷贝冻结规范意图并接管私有秘密buffer；fingerprint、最终校验和写入使用同一owned快照。caller随后Dispose/构造with新record不改变已接管请求。
- FieldEdit默认Keep；Set严格T/non-null，引用null及非法enum拒绝；bool false/int0是Set。bool/数字Clear拒绝；普通string Clear→空字符串，model/modelBaseUrl Clear→移除用户override/继承固定环境，列表Clear→空表；Policy空表表示无显式策略/按兼容baseline继承，**不是所有平台全拦**。继承、明确拒绝、全允许用Policy规则区分。
- numeric新Set仅接受矩阵旧Clamp范围内值、Math.Max对应≥0且≤Int32.MaxValue，double有限且在原范围；旧HTTP继续原Clamp/null/ToString兼容。未修改合法旧值保留；非规范旧值标LegacyNeedsReview，不由新上限删掉。字符串旧Trim、截长、白名单原样行为由矩阵逐项映射，新的不同归一须消费者确认。
- Version非负Int64，Changed成功加一，溢出拒绝。NoChanges也在同TX写receipt/必要安全审计，但不改配置/秘密/version，不publish/外部IO：Decision=NoChanges、ObservedVersion有值、CommittedVersion=null、CommitState=NotCommitted、ApplyState=NotStarted、Steps为空。这是幂等决策成功，不是用户配置提交；同键原结果不被后来配置变化重解释。Changed的CommittedVersion仅CommitState=Committed可靠。
- UnknownCommit必须保留key供Find，不断言未提交。Find无record是NotFound且不能据此自动Save新key。提交后的ApplyState可随真实阶段报告更新，重放不再执行；CurrentVersion是当前真版本，不冒充原receipt版本。
- FieldAddress对象种类由Field限定：3设备、102策略、143插件、144provider、145秘密；其余Object=null。无任意字段路径/反射/object maps，Code固定许可ASCII码不含值、端点、异常Message或body属性名。错误最多32，超量返回安全总类码，不截断后称全部校验完。
- ReadField的精确wire closed forms共同包含visibility/display/rule/userMode/effectiveSource/applyState：Hidden无value/nameRaw；Editable增加value:T；NameEditable仅T=string、增加nameRaw:string。converter从TryGetEditableValue取得已授权编辑值，Hidden不持有可序列化raw getter。Explicit时value是用户值；Inherited/Absent时value是安全可编辑的当前effective值并明确无用户override，UI只提交dirty字段，不将全表value重写为Set。Display为effective显示，Requested/UserMode与EffectiveSource/ApplyState分别报告，环境覆盖下不能把用户值称已运行。NameRaw只在受授权编辑投影；所有Read/写隐私DTO的ToString固定安全类型摘要，不输出成员值。
- 概览的名称/名单/目录/提示词/URL仅mask Display；编辑态按字段权限定typed raw value。存储/key保持原样、编辑姓名只能nameRaw，不把显示占位符写回。URL概览不完整输出，新URL拒UserInfo。秘密只有configured/source，不给masked头尾或原值；19内部属性/48旧响应项不直接透传AppSettings。
- 本节给出wire形状而非已存在HTTP codec；06必须按这份hash确认converter/严格未知重复字段/权限。FieldEffect尚未证明Hot的字段保守Restart或LegacyNeedsReview；不是读到新SettingsBox引用就宣称实际消费者全部热更新。

## 3. 全字段typed分区

完整142项，稀疏分区Missing=Keep。以下类均init-only且所有嵌套集合/元素递归不可变；首次await前模块验证并冻结自有规范快照。ConfigurationFieldId由第4节逐项列明。

### AgentChanges / AgentView (30字段)

以下左侧为Patch的精确public成员；AgentView逐项对应，但AgentDevices投影使用ImmutableArray<DeviceConfigurationView>，绝不序列化写DTO。其余T及源属性映射见完整矩阵。

```csharp
public sealed class AgentChanges
{
    public FieldEdit<string> AgentAllowedUsers { get; init; }
    public FieldEdit<ImmutableArray<DeviceConfiguration>> AgentDevices { get; init; }
    public FieldEdit<int> AgentMaxQueued { get; init; }
    public FieldEdit<string> AgentModel { get; init; }
    public FieldEdit<string> AgentPrefix { get; init; }
    public FieldEdit<int> AgentProgressSeconds { get; init; }
    public FieldEdit<string> AgentPrompt { get; init; }
    public FieldEdit<string> AgentReasoningEffort { get; init; }
    public FieldEdit<string> AgentReasoningLevels { get; init; }
    public FieldEdit<int> AgentReplyMaxChars { get; init; }
    public FieldEdit<string> AgentServerBaseUrl { get; init; }
    public FieldEdit<int> AgentServerCommandTimeoutSeconds { get; init; }
    public FieldEdit<bool> AgentServerDocker { get; init; }
    public FieldEdit<bool> AgentServerKeepContext { get; init; }
    public FieldEdit<int> AgentServerMaxSteps { get; init; }
    public FieldEdit<string> AgentServerModel { get; init; }
    public FieldEdit<string> AgentServerQqActions { get; init; }
    public FieldEdit<string> AgentServerTools { get; init; }
    public FieldEdit<bool> AgentServerUseGate { get; init; }
    public FieldEdit<string> AgentServerWorkDir { get; init; }
    public FieldEdit<string> AgentTarget { get; init; }
    public FieldEdit<int> AgentTimeoutSeconds { get; init; }
    public FieldEdit<string> AgentTools { get; init; }
    public FieldEdit<string> AgentWorkDir { get; init; }
    public FieldEdit<bool> EnableAgentBridge { get; init; }
    public FieldEdit<bool> EnableAgentMask { get; init; }
    public FieldEdit<bool> EnableHostAgent { get; init; }
    public FieldEdit<bool> EnableServerAgent { get; init; }
    public FieldEdit<bool> PanelDeployEnabled { get; init; }
    public FieldEdit<string> PanelDeployUrl { get; init; }
}
```

### BehaviorChanges / BehaviorView (53字段)

下面是Patch完整public成员。BehaviorView逐项对应ReadField<T>、T相同；安全wire规则见第2节。

```csharp
public sealed class BehaviorChanges
{
    public FieldEdit<int> AiDesire { get; init; }
    public FieldEdit<bool> AiModeEnabled { get; init; }
    public FieldEdit<string> ApprovalApprovers { get; init; }
    public FieldEdit<string> BotPersona { get; init; }
    public FieldEdit<bool> EnableApprovals { get; init; }
    public FieldEdit<bool> EnableAtmosphereDamping { get; init; }
    public FieldEdit<bool> EnableLinkPreview { get; init; }
    public FieldEdit<bool> EnableParticipationGating { get; init; }
    public FieldEdit<bool> EnablePoke { get; init; }
    public FieldEdit<bool> EnableProactive { get; init; }
    public FieldEdit<bool> EnableProfileSummary { get; init; }
    public FieldEdit<bool> EnableQuestions { get; init; }
    public FieldEdit<bool> EnableStickers { get; init; }
    public FieldEdit<bool> EnableWebSearch { get; init; }
    public FieldEdit<bool> FilterActionNarration { get; init; }
    public FieldEdit<int> GroupCooldownSeconds { get; init; }
    public FieldEdit<int> IdleFallbackSeconds { get; init; }
    public FieldEdit<bool> IgnoreBracketMessages { get; init; }
    public FieldEdit<int> LinkPreviewMax { get; init; }
    public FieldEdit<int> LinkPreviewTimeoutSeconds { get; init; }
    public FieldEdit<int> MaxAgentSteps { get; init; }
    public FieldEdit<int> MaxConcurrentReplies { get; init; }
    public FieldEdit<int> MaxContextMessages { get; init; }
    public FieldEdit<int> MaxMessagesPerConversation { get; init; }
    public FieldEdit<int> MaxProfileChars { get; init; }
    public FieldEdit<int> MoodTtlSeconds { get; init; }
    public FieldEdit<int> ParticipationCooldownSeconds { get; init; }
    public FieldEdit<int> ParticipationMaxActiveLifetimeSeconds { get; init; }
    public FieldEdit<int> ParticipationMaxConsecutiveReplies { get; init; }
    public FieldEdit<int> ParticipationMaxExitingLifetimeSeconds { get; init; }
    public FieldEdit<int> ParticipationProbingMaxReplies { get; init; }
    public FieldEdit<int> PokeCooldownSeconds { get; init; }
    public FieldEdit<int> PrivateCooldownSeconds { get; init; }
    public FieldEdit<int> ProactiveCooldownSeconds { get; init; }
    public FieldEdit<int> ProactiveQuietSeconds { get; init; }
    public FieldEdit<int> ProfileLookupCount { get; init; }
    public FieldEdit<int> ProfileSummaryIntervalSeconds { get; init; }
    public FieldEdit<int> ProfileSummaryLines { get; init; }
    public FieldEdit<int> ProfileSummaryMaxChars { get; init; }
    public FieldEdit<int> ProfileSummaryThreshold { get; init; }
    public FieldEdit<string> ScenarioPreset { get; init; }
    public FieldEdit<int> SegmentDelayMs { get; init; }
    public FieldEdit<bool> SplitReplies { get; init; }
    public FieldEdit<int> StickerCandidates { get; init; }
    public FieldEdit<int> StickerCooldownSeconds { get; init; }
    public FieldEdit<int> StickerCurateIntervalSeconds { get; init; }
    public FieldEdit<int> StickerLibraryMax { get; init; }
    public FieldEdit<int> SuitabilityThreshold { get; init; }
    public FieldEdit<int> WebSearchCooldownSeconds { get; init; }
    public FieldEdit<int> WebSearchMaxResults { get; init; }
    public FieldEdit<string> WebSearchSources { get; init; }
    public FieldEdit<int> WebSearchTimeoutSeconds { get; init; }
    public FieldEdit<bool> WebSearchUseModelSearch { get; init; }
}
```

### ModelChanges / ModelView (16字段)

下面是Patch完整public成员。ModelView逐项对应ReadField<T>、T相同；安全wire规则见第2节。

```csharp
public sealed class ModelChanges
{
    public FieldEdit<bool> AdaptiveSamplingEnabled { get; init; }
    public FieldEdit<double> DefaultTemperature { get; init; }
    public FieldEdit<double> DefaultTopP { get; init; }
    public FieldEdit<double> EmotionalTemperature { get; init; }
    public FieldEdit<double> EmotionalTopP { get; init; }
    public FieldEdit<string> FastModel { get; init; }
    public FieldEdit<bool> FastReply { get; init; }
    public FieldEdit<string> FastThinkingBudget { get; init; }
    public FieldEdit<string> FastThinkingCustomBudget { get; init; }
    public FieldEdit<int> MaxTokens { get; init; }
    public FieldEdit<string> Model { get; init; }
    public FieldEdit<string> ModelBaseUrl { get; init; }
    public FieldEdit<double> RationalTemperature { get; init; }
    public FieldEdit<double> RationalTopP { get; init; }
    public FieldEdit<string> ThinkingBudget { get; init; }
    public FieldEdit<string> ThinkingCustomBudget { get; init; }
}
```

### MusicChanges / MusicView (12字段)

下面是Patch完整public成员。MusicView逐项对应ReadField<T>、T相同；安全wire规则见第2节。

```csharp
public sealed class MusicChanges
{
    public FieldEdit<bool> EnableMusic { get; init; }
    public FieldEdit<int> MusicBitrate { get; init; }
    public FieldEdit<bool> MusicKeepAudio { get; init; }
    public FieldEdit<int> MusicLibraryMax { get; init; }
    public FieldEdit<int> MusicListenCooldownSeconds { get; init; }
    public FieldEdit<int> MusicMaxAnalysisSeconds { get; init; }
    public FieldEdit<int> MusicMaxDownloadMb { get; init; }
    public FieldEdit<int> MusicNoteTtlDays { get; init; }
    public FieldEdit<bool> MusicSendAudioToModel { get; init; }
    public FieldEdit<string> MusicSources { get; init; }
    public FieldEdit<string> MusicUnderstandModel { get; init; }
    public FieldEdit<string> NeteaseBaseUrl { get; init; }
}
```

### PlatformChanges / PlatformView (16字段)

下面是Patch完整public成员。PlatformView逐项对应ReadField<T>；PlatformPolicies改用ImmutableArray<PlatformPolicyEntryView>，其余T不变。安全wire规则见第2节。

```csharp
public sealed class PlatformChanges
{
    public FieldEdit<string> FeishuApiBase { get; init; }
    public FieldEdit<string> FeishuAppId { get; init; }
    public FieldEdit<bool> FeishuEnabled { get; init; }
    public FieldEdit<string> FeishuWhitelist { get; init; }
    public FieldEdit<string> LocalChannelIds { get; init; }
    public FieldEdit<string> MessageWhitelist { get; init; }
    public FieldEdit<string> OfficialAppId { get; init; }
    public FieldEdit<bool> OfficialChatEnabled { get; init; }
    public FieldEdit<bool> OfficialEnabled { get; init; }
    public FieldEdit<bool> OfficialSandbox { get; init; }
    public FieldEdit<string> OfficialWhitelistGroups { get; init; }
    public FieldEdit<string> OfficialWhitelistPrivates { get; init; }
    public FieldEdit<ImmutableArray<PlatformPolicyEntry>> PlatformPolicies { get; init; }
    public FieldEdit<bool> PrivateChatEnabled { get; init; }
    public FieldEdit<string> WhitelistGroups { get; init; }
    public FieldEdit<string> WhitelistPrivates { get; init; }
}
```

### ReportingChanges / ReportingView (3字段)

下面是Patch完整public成员。ReportingView逐项对应ReadField<T>、T相同；安全wire规则见第2节。

```csharp
public sealed class ReportingChanges
{
    public FieldEdit<bool> HealthReportEnabled { get; init; }
    public FieldEdit<string> HealthReportTargets { get; init; }
    public FieldEdit<string> HealthReportTime { get; init; }
}
```

### VoiceChanges / VoiceView (12字段)

下面是Patch完整public成员。VoiceView逐项对应ReadField<T>、T相同；安全wire规则见第2节。

```csharp
public sealed class VoiceChanges
{
    public FieldEdit<bool> EnableVoice { get; init; }
    public FieldEdit<string> TtsApiBase { get; init; }
    public FieldEdit<string> TtsModel { get; init; }
    public FieldEdit<string> TtsProvider { get; init; }
    public FieldEdit<string> TtsServiceUrl { get; init; }
    public FieldEdit<int> VoiceEagerness { get; init; }
    public FieldEdit<string> VoiceEmotion { get; init; }
    public FieldEdit<int> VoiceMaxChars { get; init; }
    public FieldEdit<string> VoiceName { get; init; }
    public FieldEdit<int> VoicePitch { get; init; }
    public FieldEdit<int> VoiceSpeed { get; init; }
    public FieldEdit<int> VoiceVol { get; init; }
}
```


### 子对象与注册配置

```csharp
public readonly record struct DeviceKey(Guid Value);
public sealed record DeviceConfiguration(
    DeviceKey? Key, string NameRaw, bool Enable, string? Model,
    string? WorkDir, string? Tools, int TimeoutSeconds);
public sealed record DeviceConfigurationView(
    DeviceKey Key, ReadField<string> Name,
    ReadField<bool> Enable, ReadField<string> Model,
    ReadField<string> WorkDir, ReadField<string> Tools,
    ReadField<int> TimeoutSeconds, ApplyState RenameState);
public readonly record struct DriverKey(
    string PlatformId, string AccountScope, string InstanceId);
public readonly record struct ProviderKey(string Value);
public readonly record struct PluginKey(string Value);
public readonly record struct ModelCredentialKey(string Value);
public readonly record struct ActionId(string Value);
public abstract record PolicyTarget
{
    private PolicyTarget() { }
    public sealed record AccountDefault(
        string PlatformId, string AccountScope) : PolicyTarget;
    public sealed record Instance(DriverKey Key) : PolicyTarget;
}
public enum ToggleRule { Inherit = 0, Enabled = 1, Disabled = 2 }
public enum ListRuleKind { Inherit = 0, Explicit = 1, AllowAll = 2 }
public sealed class ListRule
{
    public ListRuleKind Kind { get; }
    public static ListRule Inherit();
    public static ListRule Explicit(string value);
    public static ListRule AllowAll();
    public bool TryGetExplicitValue(out string value);
}
public enum ActionRuleKind { Inherit = 0, Explicit = 1 }
public sealed record ActionRule(
    ActionRuleKind Kind, ImmutableArray<ActionId> Actions);
public enum PlatformFeatureId
{
    Text = 0, Image = 1, Voice = 2, Quote = 3, Recall = 4,
    Thread = 5, Stickers = 6, Music = 7, Poke = 8,
    LinkPreview = 9, WebSearch = 10
}
public sealed record FeatureOverride(PlatformFeatureId Feature, bool Enabled);
public sealed record PlatformPolicyConfiguration(
    ToggleRule Enabled, ToggleRule ChatEnabled,
    ListRule GroupWhitelist, ListRule PrivateWhitelist,
    ActionRule AllowedActions, ImmutableArray<FeatureOverride> Features);
public sealed record PlatformPolicyEntry(
    PolicyTarget Target, PlatformPolicyConfiguration Configuration);
public sealed record PlatformPolicyEntryView(
    PolicyTarget Target, ReadField<ToggleRule> Enabled,
    ReadField<ToggleRule> ChatEnabled,
    ReadField<ListRule> GroupWhitelist, ReadField<ListRule> PrivateWhitelist,
    ReadField<ActionRule> AllowedActions,
    ReadField<ImmutableArray<FeatureOverride>> Features);
public sealed record PolicyEdit(
    PolicyTarget Target, PlatformPolicyConfiguration Configuration);
public sealed record PluginEnabledEdit(PluginKey Key, bool Enabled);
public enum CredentialBindingEditKind
{
    KeepExisting = 0, UsePrimaryModelCredential = 1,
    Unconfigured = 2, UseRegisteredModelCredential = 3
}
public sealed class CredentialBindingEdit
{
    public CredentialBindingEditKind Kind { get; }
    public static CredentialBindingEdit KeepExisting();
    public static CredentialBindingEdit UsePrimaryModelCredential();
    public static CredentialBindingEdit Unconfigured();
    public static CredentialBindingEdit UseRegisteredModelCredential(
        ModelCredentialKey key);
    public bool TryGetRegisteredKey(out ModelCredentialKey key);
}
public sealed record ProviderMetadataPatch(
    FieldEdit<string> NameRaw, FieldEdit<int> Priority,
    FieldEdit<string> BaseUrl, FieldEdit<string> ModelName,
    FieldEdit<bool> Enabled, CredentialBindingEdit Credential);
public sealed record ProviderEdit(ProviderKey Key, ProviderMetadataPatch Changes);
public sealed record RegisteredConfigurationChanges(
    ImmutableArray<PluginEnabledEdit> Plugins,
    ImmutableArray<ProviderEdit> Providers, ImmutableArray<PolicyEdit> Policies);
public enum CredentialBindingState
{
    Unconfigured = 0, PrimaryModel = 1, RegisteredModel = 2, Unclassified = 3
}
public sealed record PluginConfigurationView(PluginKey Key, bool DesiredEnabled);
public sealed record ProviderConfigurationView(
    ProviderKey Key, ReadField<string> Name,
    ReadField<string> BaseUrl, ReadField<string> ModelName,
    int Priority, bool DesiredEnabled, CredentialBindingState Credential,
    ApplyState RuntimeApplyState);
public sealed record RegisteredConfigurationView(
    ImmutableArray<PluginConfigurationView> Plugins,
    ImmutableArray<ProviderConfigurationView> Providers);
```

- Registered三集合为按已登记key稀疏修改，空=不改、不删除未列项；Policies是102同一领域的逐对象适配，不新增FieldId。与整表PlatformPolicies同时Set/Clear重叠即拒绝；duplicate直接field/对象key拒绝。归一为内部closed typed atoms，公共消费仍七组Patch；别名重叠只允许完全相同Set/Clear/继承意图，否则AliasConflict，顺序不定优先级。
- 143/144只改已登记插件/provider配置，不安装/删除实现，不增无限对象。Provider新注册credential分支当前无已批准registry时Unsupported；ModelCredentialKey不能包含env名或secret仓储名。KeepExisting也重验凭据用途/provider/新BaseUrl端点授权及出网政策；旧任意secret:/env:Unclassified内部保留但禁止读值/路由。官方、Panel/OneBot/Agent/NapCat/Cookie秘密不用于模型。
- Unconfigured允许禁用provider暂存；需key transport的enabled provider不能通过最终整批校验。当前CompositionRoot:611～618/ModelTransport:239～243拒绝空key，**不是匿名能力**。固定主模型凭据适配只允许已确认DB覆盖和按真实BotConfig优先级的固定BOTAGENT_API_KEY/QQCHAT_API_KEY/OPENAI_API_KEY回落；不得Core动态GetEnvironmentVariable(name)。现有provider env:QQCHAT_API_KEY特殊处理与BotConfig别名差异须03明确，不只漏掉BOTAGENT别名后声称完全兼容。
- primary Model/BaseUrl及旧对应字段归一到同一用户override；Clear继承与Set恰等于环境仍是不同意图，拒冲突。primary Priority/Enabled先ReadOnly直到03确认。metadata同TX，circuit独立；启动EnsurePrimary只能受协调缺失初始化/投影，不能每次重写用户元数据。实际非primary路由被启动捕获，未证明热切换时RestartRequired，不能写DB就称Applied。
- DriverKey三元Ordinal，PlatformId先非空再真实Normalize，scope/instance保留有效原文。空Instance仅旧具体实例ID，不等于AccountDefault；多实例新登记非空且唯一。旧二元/IgnoreCase查找只能唯一已登记匹配，否则安全歧义拒绝。现有Registry、Resolver、Switch、WhitelistGate和Feishu真实消费必须同批迁移，单改DTO无隔离证据。
- Policy新Explicit("")是明确空拒绝，Inherit是继承账号default/兼容baseline，AllowAll显式全量；ActionRule Explicit(empty)明确无动作，Inherit的Actions必须空。Feature仅11项/无重复；动作须真实catalog允许，消息能力≠管理动作。旧二元策略迁AccountDefault；新实例覆盖同scope default，不跨scope回落；新账号无default采用明确安全基线，旧非canonical依赖legacy的账户先LegacyNeedsReview，不能默默拒绝旧业务。
- **保持旧已确认语义**：私域空分区回落MessageWhitelist，官方有效空全允许，本地/飞书有效空全拦。pure legacy mapper把旧空翻译对应Inherit/AllowAll/ExplicitEmpty，不因新Clear规则偷改。legacy镜像仅qq.private/legacy、qq.official/legacy、local/legacy、feishu/default，不跨scope回写；全部更改需02/06确认。
- Plugin canonical Key采用manager Ordinal、原文不变；旧store IgnoreCase/Trim迁移只接受唯一对照，大小写碰撞LegacyNeedsReview，不合并两个插件。DesiredEnabled不是Initialize/Shutdown或实际停止；initialized独立台账保证禁用后已初始化资源仍释放。
- Device Key=null仅新设备意图；第一次TX生成Guid并持久，指纹仍未分配意图，重放回原映射不再生Key。配置写NameRaw/Key，内部持久ActiveBridgeName、DesiredName、RenamePending、DesiredEnabled；未远端改名返回RestartRequired。新UI按Key保身份；旧无Key全表六字段无法推断rename，要按经04/06确认的delete+create兼容策略或显式LegacyNeedsReview，不猜同名。
- removed/disabled旧bridge连接必须显式门禁，不回落当前IsDeviceEnabled缺省true。原AgentDevices JSON禁止进ReadView；未知嵌套密码/key等新输入拒绝，合法旧六字段投影与未知兼容载荷内部保留。DeviceView仅允许字段/授权nameRaw，无原始字符串。实际修改旧路由/UI/资源需要共享补丁与真实负例，不称文档已修复。

## 4. 配置FieldId注册表

此处1–142只对应本次完整非秘密输入，稳定值候选；143=RegisteredPlugins、144=RegisteredProviders、145=AllowedSecretEdits。只读19个属性不获得可写FieldId；秘密错误使用145+安全Slot，不将秘密值/任意路径注册为field。实际enum需完整生成并经过Core工程验证，文档表不是已存在enum。

| 显式值 | 旧输入key | 新分区成员 | 精确T |
| --- | --- | --- | --- |
| 1 | `adaptiveSamplingEnabled` | `ModelChanges.AdaptiveSamplingEnabled` | `bool` |
| 2 | `agentAllowedUsers` | `AgentChanges.AgentAllowedUsers` | `string` |
| 3 | `agentDevices` | `AgentChanges.AgentDevices` | `ImmutableArray<DeviceConfiguration>` |
| 4 | `agentMaxQueued` | `AgentChanges.AgentMaxQueued` | `int` |
| 5 | `agentModel` | `AgentChanges.AgentModel` | `string` |
| 6 | `agentPrefix` | `AgentChanges.AgentPrefix` | `string` |
| 7 | `agentProgressSeconds` | `AgentChanges.AgentProgressSeconds` | `int` |
| 8 | `agentPrompt` | `AgentChanges.AgentPrompt` | `string` |
| 9 | `agentReasoningEffort` | `AgentChanges.AgentReasoningEffort` | `string` |
| 10 | `agentReasoningLevels` | `AgentChanges.AgentReasoningLevels` | `string` |
| 11 | `agentReplyMaxChars` | `AgentChanges.AgentReplyMaxChars` | `int` |
| 12 | `agentServerBaseUrl` | `AgentChanges.AgentServerBaseUrl` | `string` |
| 13 | `agentServerCommandTimeoutSeconds` | `AgentChanges.AgentServerCommandTimeoutSeconds` | `int` |
| 14 | `agentServerDocker` | `AgentChanges.AgentServerDocker` | `bool` |
| 15 | `agentServerKeepContext` | `AgentChanges.AgentServerKeepContext` | `bool` |
| 16 | `agentServerMaxSteps` | `AgentChanges.AgentServerMaxSteps` | `int` |
| 17 | `agentServerModel` | `AgentChanges.AgentServerModel` | `string` |
| 18 | `agentServerQqActions` | `AgentChanges.AgentServerQqActions` | `string` |
| 19 | `agentServerTools` | `AgentChanges.AgentServerTools` | `string` |
| 20 | `agentServerUseGate` | `AgentChanges.AgentServerUseGate` | `bool` |
| 21 | `agentServerWorkDir` | `AgentChanges.AgentServerWorkDir` | `string` |
| 22 | `agentTarget` | `AgentChanges.AgentTarget` | `string` |
| 23 | `agentTimeoutSeconds` | `AgentChanges.AgentTimeoutSeconds` | `int` |
| 24 | `agentTools` | `AgentChanges.AgentTools` | `string` |
| 25 | `agentWorkDir` | `AgentChanges.AgentWorkDir` | `string` |
| 26 | `aiDesire` | `BehaviorChanges.AiDesire` | `int` |
| 27 | `aiModeEnabled` | `BehaviorChanges.AiModeEnabled` | `bool` |
| 28 | `approvalApprovers` | `BehaviorChanges.ApprovalApprovers` | `string` |
| 29 | `botPersona` | `BehaviorChanges.BotPersona` | `string` |
| 30 | `defaultTemperature` | `ModelChanges.DefaultTemperature` | `double` |
| 31 | `defaultTopP` | `ModelChanges.DefaultTopP` | `double` |
| 32 | `emotionalTemperature` | `ModelChanges.EmotionalTemperature` | `double` |
| 33 | `emotionalTopP` | `ModelChanges.EmotionalTopP` | `double` |
| 34 | `enableAgentBridge` | `AgentChanges.EnableAgentBridge` | `bool` |
| 35 | `enableAgentMask` | `AgentChanges.EnableAgentMask` | `bool` |
| 36 | `enableApprovals` | `BehaviorChanges.EnableApprovals` | `bool` |
| 37 | `enableAtmosphereDamping` | `BehaviorChanges.EnableAtmosphereDamping` | `bool` |
| 38 | `enableHostAgent` | `AgentChanges.EnableHostAgent` | `bool` |
| 39 | `enableLinkPreview` | `BehaviorChanges.EnableLinkPreview` | `bool` |
| 40 | `enableMusic` | `MusicChanges.EnableMusic` | `bool` |
| 41 | `enableParticipationGating` | `BehaviorChanges.EnableParticipationGating` | `bool` |
| 42 | `enablePoke` | `BehaviorChanges.EnablePoke` | `bool` |
| 43 | `enableProactive` | `BehaviorChanges.EnableProactive` | `bool` |
| 44 | `enableProfileSummary` | `BehaviorChanges.EnableProfileSummary` | `bool` |
| 45 | `enableQuestions` | `BehaviorChanges.EnableQuestions` | `bool` |
| 46 | `enableServerAgent` | `AgentChanges.EnableServerAgent` | `bool` |
| 47 | `enableStickers` | `BehaviorChanges.EnableStickers` | `bool` |
| 48 | `enableVoice` | `VoiceChanges.EnableVoice` | `bool` |
| 49 | `enableWebSearch` | `BehaviorChanges.EnableWebSearch` | `bool` |
| 50 | `fastModel` | `ModelChanges.FastModel` | `string` |
| 51 | `fastReply` | `ModelChanges.FastReply` | `bool` |
| 52 | `fastThinkingBudget` | `ModelChanges.FastThinkingBudget` | `string` |
| 53 | `fastThinkingCustomBudget` | `ModelChanges.FastThinkingCustomBudget` | `string` |
| 54 | `feishuApiBase` | `PlatformChanges.FeishuApiBase` | `string` |
| 55 | `feishuAppId` | `PlatformChanges.FeishuAppId` | `string` |
| 56 | `feishuEnabled` | `PlatformChanges.FeishuEnabled` | `bool` |
| 57 | `feishuWhitelist` | `PlatformChanges.FeishuWhitelist` | `string` |
| 58 | `filterActionNarration` | `BehaviorChanges.FilterActionNarration` | `bool` |
| 59 | `groupCooldownSeconds` | `BehaviorChanges.GroupCooldownSeconds` | `int` |
| 60 | `healthReportEnabled` | `ReportingChanges.HealthReportEnabled` | `bool` |
| 61 | `healthReportTargets` | `ReportingChanges.HealthReportTargets` | `string` |
| 62 | `healthReportTime` | `ReportingChanges.HealthReportTime` | `string` |
| 63 | `idleFallbackSeconds` | `BehaviorChanges.IdleFallbackSeconds` | `int` |
| 64 | `ignoreBracketMessages` | `BehaviorChanges.IgnoreBracketMessages` | `bool` |
| 65 | `linkPreviewMax` | `BehaviorChanges.LinkPreviewMax` | `int` |
| 66 | `linkPreviewTimeoutSeconds` | `BehaviorChanges.LinkPreviewTimeoutSeconds` | `int` |
| 67 | `localChannelIds` | `PlatformChanges.LocalChannelIds` | `string` |
| 68 | `maxAgentSteps` | `BehaviorChanges.MaxAgentSteps` | `int` |
| 69 | `maxConcurrentReplies` | `BehaviorChanges.MaxConcurrentReplies` | `int` |
| 70 | `maxContextMessages` | `BehaviorChanges.MaxContextMessages` | `int` |
| 71 | `maxMessagesPerConversation` | `BehaviorChanges.MaxMessagesPerConversation` | `int` |
| 72 | `maxProfileChars` | `BehaviorChanges.MaxProfileChars` | `int` |
| 73 | `maxTokens` | `ModelChanges.MaxTokens` | `int` |
| 74 | `messageWhitelist` | `PlatformChanges.MessageWhitelist` | `string` |
| 75 | `model` | `ModelChanges.Model` | `string` |
| 76 | `modelBaseUrl` | `ModelChanges.ModelBaseUrl` | `string` |
| 77 | `moodTtlSeconds` | `BehaviorChanges.MoodTtlSeconds` | `int` |
| 78 | `musicBitrate` | `MusicChanges.MusicBitrate` | `int` |
| 79 | `musicKeepAudio` | `MusicChanges.MusicKeepAudio` | `bool` |
| 80 | `musicLibraryMax` | `MusicChanges.MusicLibraryMax` | `int` |
| 81 | `musicListenCooldownSeconds` | `MusicChanges.MusicListenCooldownSeconds` | `int` |
| 82 | `musicMaxAnalysisSeconds` | `MusicChanges.MusicMaxAnalysisSeconds` | `int` |
| 83 | `musicMaxDownloadMb` | `MusicChanges.MusicMaxDownloadMb` | `int` |
| 84 | `musicNoteTtlDays` | `MusicChanges.MusicNoteTtlDays` | `int` |
| 85 | `musicSendAudioToModel` | `MusicChanges.MusicSendAudioToModel` | `bool` |
| 86 | `musicSources` | `MusicChanges.MusicSources` | `string` |
| 87 | `musicUnderstandModel` | `MusicChanges.MusicUnderstandModel` | `string` |
| 88 | `neteaseBaseUrl` | `MusicChanges.NeteaseBaseUrl` | `string` |
| 89 | `officialAppId` | `PlatformChanges.OfficialAppId` | `string` |
| 90 | `officialChatEnabled` | `PlatformChanges.OfficialChatEnabled` | `bool` |
| 91 | `officialEnabled` | `PlatformChanges.OfficialEnabled` | `bool` |
| 92 | `officialSandbox` | `PlatformChanges.OfficialSandbox` | `bool` |
| 93 | `officialWhitelistGroups` | `PlatformChanges.OfficialWhitelistGroups` | `string` |
| 94 | `officialWhitelistPrivates` | `PlatformChanges.OfficialWhitelistPrivates` | `string` |
| 95 | `panelDeployEnabled` | `AgentChanges.PanelDeployEnabled` | `bool` |
| 96 | `panelDeployUrl` | `AgentChanges.PanelDeployUrl` | `string` |
| 97 | `participationCooldownSeconds` | `BehaviorChanges.ParticipationCooldownSeconds` | `int` |
| 98 | `participationMaxActiveLifetimeSeconds` | `BehaviorChanges.ParticipationMaxActiveLifetimeSeconds` | `int` |
| 99 | `participationMaxConsecutiveReplies` | `BehaviorChanges.ParticipationMaxConsecutiveReplies` | `int` |
| 100 | `participationMaxExitingLifetimeSeconds` | `BehaviorChanges.ParticipationMaxExitingLifetimeSeconds` | `int` |
| 101 | `participationProbingMaxReplies` | `BehaviorChanges.ParticipationProbingMaxReplies` | `int` |
| 102 | `platformPolicies` | `PlatformChanges.PlatformPolicies` | `ImmutableArray<PlatformPolicyEntry>` |
| 103 | `pokeCooldownSeconds` | `BehaviorChanges.PokeCooldownSeconds` | `int` |
| 104 | `privateChatEnabled` | `PlatformChanges.PrivateChatEnabled` | `bool` |
| 105 | `privateCooldownSeconds` | `BehaviorChanges.PrivateCooldownSeconds` | `int` |
| 106 | `proactiveCooldownSeconds` | `BehaviorChanges.ProactiveCooldownSeconds` | `int` |
| 107 | `proactiveQuietSeconds` | `BehaviorChanges.ProactiveQuietSeconds` | `int` |
| 108 | `profileLookupCount` | `BehaviorChanges.ProfileLookupCount` | `int` |
| 109 | `profileSummaryIntervalSeconds` | `BehaviorChanges.ProfileSummaryIntervalSeconds` | `int` |
| 110 | `profileSummaryLines` | `BehaviorChanges.ProfileSummaryLines` | `int` |
| 111 | `profileSummaryMaxChars` | `BehaviorChanges.ProfileSummaryMaxChars` | `int` |
| 112 | `profileSummaryThreshold` | `BehaviorChanges.ProfileSummaryThreshold` | `int` |
| 113 | `rationalTemperature` | `ModelChanges.RationalTemperature` | `double` |
| 114 | `rationalTopP` | `ModelChanges.RationalTopP` | `double` |
| 115 | `scenarioPreset` | `BehaviorChanges.ScenarioPreset` | `string` |
| 116 | `segmentDelayMs` | `BehaviorChanges.SegmentDelayMs` | `int` |
| 117 | `splitReplies` | `BehaviorChanges.SplitReplies` | `bool` |
| 118 | `stickerCandidates` | `BehaviorChanges.StickerCandidates` | `int` |
| 119 | `stickerCooldownSeconds` | `BehaviorChanges.StickerCooldownSeconds` | `int` |
| 120 | `stickerCurateIntervalSeconds` | `BehaviorChanges.StickerCurateIntervalSeconds` | `int` |
| 121 | `stickerLibraryMax` | `BehaviorChanges.StickerLibraryMax` | `int` |
| 122 | `suitabilityThreshold` | `BehaviorChanges.SuitabilityThreshold` | `int` |
| 123 | `thinkingBudget` | `ModelChanges.ThinkingBudget` | `string` |
| 124 | `thinkingCustomBudget` | `ModelChanges.ThinkingCustomBudget` | `string` |
| 125 | `ttsApiBase` | `VoiceChanges.TtsApiBase` | `string` |
| 126 | `ttsModel` | `VoiceChanges.TtsModel` | `string` |
| 127 | `ttsProvider` | `VoiceChanges.TtsProvider` | `string` |
| 128 | `ttsServiceUrl` | `VoiceChanges.TtsServiceUrl` | `string` |
| 129 | `voiceEagerness` | `VoiceChanges.VoiceEagerness` | `int` |
| 130 | `voiceEmotion` | `VoiceChanges.VoiceEmotion` | `string` |
| 131 | `voiceMaxChars` | `VoiceChanges.VoiceMaxChars` | `int` |
| 132 | `voiceName` | `VoiceChanges.VoiceName` | `string` |
| 133 | `voicePitch` | `VoiceChanges.VoicePitch` | `int` |
| 134 | `voiceSpeed` | `VoiceChanges.VoiceSpeed` | `int` |
| 135 | `voiceVol` | `VoiceChanges.VoiceVol` | `int` |
| 136 | `webSearchCooldownSeconds` | `BehaviorChanges.WebSearchCooldownSeconds` | `int` |
| 137 | `webSearchMaxResults` | `BehaviorChanges.WebSearchMaxResults` | `int` |
| 138 | `webSearchSources` | `BehaviorChanges.WebSearchSources` | `string` |
| 139 | `webSearchTimeoutSeconds` | `BehaviorChanges.WebSearchTimeoutSeconds` | `int` |
| 140 | `webSearchUseModelSearch` | `BehaviorChanges.WebSearchUseModelSearch` | `bool` |
| 141 | `whitelistGroups` | `PlatformChanges.WhitelistGroups` | `string` |
| 142 | `whitelistPrivates` | `PlatformChanges.WhitelistPrivates` | `string` |

## 5. 秘密输入、启动投影与迁移

```csharp
public enum WritableSecretSlot
{
    ModelApiKey = 0, AgentServerKey = 1, TtsApiKey = 2,
    FeishuAppSecret = 3, FeishuEncryptKey = 4, FeishuVerificationToken = 5
}
public enum SecretEditKind { Keep = 0, Replace = 1, Delete = 2 }
public sealed class SecretEdit
{
    public WritableSecretSlot Slot { get; }
    public SecretEditKind Kind { get; }
    public static SecretEdit Keep(WritableSecretSlot slot);
    public static SecretEdit Delete(WritableSecretSlot slot);
    public static SecretEdit Replace(WritableSecretSlot slot, SensitiveInput input);
}
public sealed class SensitiveInput : IDisposable
{
    public int ByteLength { get; }
    public static SensitiveInput FromUtf8(ReadOnlySpan<byte> value);
    public override string ToString();
    public void Dispose();
}
```

- 无公开Value/UseUtf8(callback)，Storage通过Core明确internal/friend seam消费，不能把可逃逸ReadOnlyMemory借给任意调用者。factory私有复制、ToString固定[redacted]、输出serializer拒绝。首次await前writer取得私有owned副本，ct/Dispose不能擦正在提交副本，finally清零。传输string/JSON临时副本不承诺全部擦除。
- Replace(nonempty UTF8)经旧秘密Trim规则归一，Delete/Keep无值，重复slot/非法组合整批拒绝。六槽仅既有五槽加VerificationToken安全迁移候选，第六槽未取得01/02/06确认不实施；不会增加其它秘密可写能力。
- 旧兼容：Missing/null→Keep；apiKey/agentServerKey空→Delete；ttsApiKey/feishuAppSecret/feishuEncryptKey空→Keep且clear true优先；旧verificationToken空→Delete。clear类型容错只由pure LegacyPlan保留。旧audit ttsKey仅忽略的审计输入，不新造可写槽。QQ官方Secret/clear任意大小写存在含null/空/false均在副作用前拒绝，任何来源只直接读取QQCHAT_OFFICIAL_APP_SECRET，面板不收/存/导出/回显。
- VerificationToken一次迁移同TX写新slot、Initialized/Deleted标记、移除settings明文及用户版本事实；只有尚未初始化时可用固定环境首次种子，删除不会重启复活。AppSecret/EncryptKey原基础设施环境优先与Token行为首次种子不同，不混同source metadata。
- Token旧GET明文取消与UI未改omit/明确删除按钮同批；仅隐藏GET会让旧UI空串Delete。缓存旧UI兼容必须06提出版本识别或明确LegacyNeedsReview拒绝与更新路径，现有旧请求无法区分“空未改”与“有意删除”，不谎称可完全透明兼容。新行为迁移未批准/替代通路未验收前保留旧有效通路且不实施单边隐藏。
- 行为真源SQLite用户配置；19旧入口未改的属性见矩阵3.2内部carry-forward，秘密/infra不因public setter新增槽。Host environment是固定只读overlay，不普通Save回写用户配置，不绕version。Legacy importer用同验证/writer，CLI只读，文件编辑不是自动真源。
- 主model/base/key维持已有用户override胜固定环境；ApiKey/AgentServerKey Delete仅删DB覆盖，允许环境回落不等于彻底吊销。飞书AppSecret/EncryptKey维持实际基础设施环境优先、runtime捕获需Restart；Token首次播种规则单列。
- FastModel/FastReply本候选**保持旧启动环境优先**，不擅自改为仅播种。热保存与启动采用同effective projector：环境有值时用户值虽Committed但EffectiveSource=Environment，不称新值已运行；环境缺省时用户值按原业务生效。请求/userVersion、effective source与apply状态分别展示。这是消除热/重启不一致的候选，需要03/06确认，未批准不改旧源码。
- 迁移不读真实配置；只用合成旧格式及环境。新schema版本具体值由01实现评审登记，现schema9不直接提高；旧C2未来版本拒绝保留。回退新schema/secret时恢复前镜像和整套旧产物，不能混DLL或运行中替库，未经演练不说旧版本可读新DB。

## 6. 单writer、提交与兼容顺序

1. 可信授权；新strict profile解析/未知重复字段、官方Secret存在性拒绝；同步冻结typed意图/秘密所有权。纯legacy parser→LegacyPlan只有字段/旧规则，不Save、不mood/TTS、不独立secret.Save。
2. 一次Storage AppDatabase.Write内捕获返回结果（真实入口是void Action，没有臆造Write<T>）；所有startup/import/HTTP/provider/plugin配置参与者共享协调。同连接先持久key治理floor、查scope+key ledger；同参结果先于容量/CAS，异参冲突，不重新发布/应用。
3. key门禁及最终跨域校验通过→同TX写全部用户配置、允许秘密、provider/plugin元数据、递增version、安全审计、receipt/未开始steps。任何参与写失败throw整体回滚，包括秘密/审计。不能在mutate调用各自独立Save，也不嵌套AppDatabase.Write。Rejected若仅floor安全更新，结构结果返回让floor提交；没有用户配置部分提交。
4. NoChanges是最终规范意图对当前事实无变更，同TX写受保护参数与NoChanges receipt/安全结果，version不变。只保留请求不含原值；重放返回原决策，别的保存不会把原NoChanges重新执行。
5. commit之后第一执行者publish运行快照，再执行被登记应用步骤；外部/非幂等阶段先记Started、再结果，崩溃Unknown不自动重做。配置和发布/重建/裁消息/清台账/调度/TTS/mood不是同事务；Replay/Find不触发这些副作用。后续step账本写失败保留Unknown，不伪称应用或回滚。
6. apply报告最多八种步骤是聚合结果：任何目标失败/需重启/unknown不得该步骤或总状态标全Applied。每对象状态通过C4 Inspect恢复，不能用八条回执掩盖128对象未完成。新版本到来不把旧未应用版本自动标Applied，superseded仍保留真实历史事实。
7. 请求ct在提交开始前CancelledBeforeCommit；提交点之后不由HTTP取消异常推断NotCommitted。DB commit错误有不确定窗口则UnknownCommit+原key，Find核对；不盲换key。提交后的必要账本/安全收尾不依赖请求ct，资源仍有有界任务/停机语义。
8. 重启仅恢复最新Committed用户配置及安全可重建runtime投影；不重放裁剪、清审批、通知或外部动作。TTS按version只合并一次，Unknown非幂等写不自重做。需幂等marker/temp替换须00/06另审，不把现有WriteAllText当原子。
9. 旧HTTP无ExpectedVersion：纯LegacyPlan由writer在锁/同TX最新baseline上按旧稀疏规则合并，不能强加新CAS让旧client失效；新的Save总要求版本。旧客户端若原来没有key，兼容adapter只能分配单次请求key，不宣称网络自动重试已幂等；新UI必须持有原key/Find。
10. 旧mood独立操作：LegacySaveOutcome(ConfigurationResult,LegacyActionResults)，每动作独立namespace key、状态NotStarted/Applied/Failed/Unknown及安全错误；配置NoChanges也不绕过动作幂等。不会提交配置后声称mood成功或同一个SQLite回滚，响应丢失必须查原事实。
11. 审计只FieldId/允许对象种类或保护标识、version/decision/slot/action/结果，不写值、URL、客户端任意字段名；不提前result=applied。HMAC只内部请求等同性，现有SHA256链不冒称HMAC/DB加密。

## 7. key时间、恢复与预算

### 7.1 时序及优先级

- key格式`cfg1:generationN:issuedUnixSeconds:nonceN`≤96 ASCII，两个Guid非零，秒为非负有效Int64且checked加TTL。绑定可信AuthorityId+SubjectId+ResourceId+操作kind；ContractRevision≤32 ASCII及ExpectedVersion、Keep/Set/Clear、secret/action、canonical对象全部进入内部HMAC-SHA256指纹，不返回摘要或原请求。
- Storage独立32byte随机HMAC key，不借官方Secret或panel token；每次**受控Host激活**在开放任何新写前同TX建立新generation/key。旧generation有receipt仍核old受保护参数并查询/返回，缺失返回GenerationMismatch永不新执行；不能仅Restore换代而允许备份启动重用旧epoch。无运行中替库/双Host支持。
- 唯一已注入真实IClock.Now.ToUnixTimeSeconds和IClock.TickCount（单调毫秒）；不new系统clock。激活锚点F0=max(持久floor,nowUnix)，T0=TickCount。每次治理F=max(持久floor,nowUnix,F0+floor((TickCount-T0)/1000))；checked溢出/Tick倒退拒ClockRegression，不减floor。wall落后floor>300s拒绝新请求；已存在同参receipt仍可安全查询。
- Save/Find用真实Write持久floor后再返回KeyExpired，F≥issued+86400为Expired，issued>F+300拒ValidationFailed，边界均精确。返回Rejected不throw，才能提交floor；真实TX故障回滚则StorageUnavailable/CommitFailed/Unknown，不能假称已永久判过期。Read/CLI完全不写floor，ServerUnixSeconds只作提示。
- 授权/格式/codec检查后，在治理TX更新floor→查record、核指纹（Save）→过期返回Expired；存在合法未过期record返回原决策，不做CAS/容量判断。无record且非当前代GenerationMismatch；当前代再看clock regression/future/容量/CAS/最终校验。Find不存在当前有效key=NotFound但不是“肯定从未提交”；不得Find触发apply。
- 24h到期同样拒已有receipt的重新执行/常规结果取回；持续apply事实可从Read/C4安全状态看。过期Pending/Unknown仍保留占容量直至明确终态，不能删后再execute。old receipt codec/HMAC key保留到该代全部记录可移除，不能新版本升级先删codec造成错误接受。
- floor/HMAC/generation都在DB备份内，整库回退不能单凭自身检测历史时间。受控激活新代保证丢失receipt的旧key不执行，但不保证所有回退后的老Expired仍字面分类Expired；可能GenerationMismatch。要准确永久分类需外部可信单调源另审。此限定不把NoRedo降为可重做，也不捏造现有Restore API：AppDatabase只有Backup已定位。
- expiry/record比较须严格无秘密输出；Find权限与Save一样重新核对subject/resource。Forbidden不泄record存在；格式/缺省初始化不得建立无限主体缓存。

### 7.2 数值候选与兼容profile

数值是新strict profile提案，尚未有资源测量批准/边界运行证据；**不对旧合法请求无声施加新限额**。

| 项目 | precise候选值 | 验证与兼容 |
| --- | --- | --- |
| 新JSON | 累计UTF8 1,048,576byte / depth16 / 同时最多4个解析准入 | chunked累计，不只信ContentLength；slots在完整读取前申请、finally释放；4MiB只是body字节不等于堆预算 |
| ordinary string | UTF8≤32768byte；agentPrompt保留旧≤8000 UTF16 units | 旧更小字段限制保留，未修改旧carry-forward不丢失；新不同裁剪必须确认 |
| model/URL | 1024/2048 UTF16 units | URL拒UserInfo，http(s)与实际SSRF/目标策略一起校验，合法旧query兼容不误删 |
| secret | 每槽16384 UTF8 byte、6槽、总≤98304byte | 槽重复拒绝，writer副本资源需预算；无公开值/长度敏感打印 |
| Device | 128项；name128 UTF16 units；OrdinalIgnoreCase唯一 | timeout0继承或30..7200，新strict；旧不同合法值CompatibilityProfile保留/需审查，不冒称新上限原已有 |
| Policy | 32条+逐对象32条；ID80 ASCII许可字符 | group/private4096 UTF16 units；feature≤32/name≤64/action≤64且action≤80字符；known enum/catalog并验 |
| Plugin/Provider | 128/64 edits，key128 UTF16 units | 已登记原文key，未知/duplicate拒；不创建任意credential/对象 |
| 合计修改 | 142直接槽+32policy+128plugin+64provider+6secret≤372意图 | 结构表/逐对象重叠拒；防通过空分区/keep灌无限数组 |
| Config/management账本 | 所有代、所有命名空间合计4096条，每Authority+Subject+Resource256条 | 同参重放先查；未过期/未终态不驱逐；满拒新执行。不仅当前generation |
| receipt | 每条8192byte、fingerprint32byte、step8/errors32 | 固定安全类型，超过返回安全失败不做截断伪成功；约32MiB活动payload不是DB总盘或堆预算 |
| HMAC key | 当前+有receipt的代≤4097代、每代32byte | raw上限131104byte，无receipt才删oldkey/codec；计算与治理元数据另计资源 |
| List page | default50；1..100；cursor≤512byte | caller+kind+catalog revision+位置签名，无持久paging session；stale重读 |
| key/TTL | ≤96 ASCII；TTL86400s；未来≤300s | same key异参拒绝、时间checked、回拨/重启/备份负例；mgmt命名空间同样受控 |

旧ReadJsonAsync:854～865当前ReadToEnd无body上限。旧CompatibilityProfile精确保留142/9输入的旧类型/null/Clamp/长度行为；隐私/官方Secret硬红线不兼容放行。给旧body新增限额、Token缓存UI变化、设备rename迁移及new ZBA认证是明确兼容/安全批准项，不靠“最大1MiB”断言自动批准。

## 8. C4能力、动作与恢复

稳定标识沿用已登记Driver三元、Plugin.Id、Provider.Id、DeviceKey；显示名不改key。MCP当前无真实实现，仅以后已登记ID，不生成已支持项。当前消息PlatformCapabilities不是管理能力。

```csharp
namespace BotAgent.Domain.Management;
public enum ManagedKind
{
    Driver = 0, Plugin = 1, Provider = 2, AgentDevice = 3, McpServer = 4
}
public abstract record ManagedObjectKey
{
    private ManagedObjectKey() { }
    public sealed record Driver(DriverKey Key) : ManagedObjectKey;
    public sealed record Plugin(PluginKey Key) : ManagedObjectKey;
    public sealed record Provider(ProviderKey Key) : ManagedObjectKey;
    public sealed record Device(DeviceKey Key) : ManagedObjectKey;
    public sealed record Mcp(string RegisteredId) : ManagedObjectKey;
}
public enum ManagedAction
{
    Inspect = 0, ValidateConfiguration = 1, Configure = 2,
    Start = 3, Stop = 4, TestConnection = 5,
    Connect = 6, Disconnect = 7, Cancel = 8
}
public enum ManagedRuntimeState
{
    Unknown = 0, NotLoaded = 1, Disabled = 2, Starting = 3,
    Running = 4, Stopping = 5, Disconnected = 6, Faulted = 7
}
public enum ManagedResultCode
{
    Ok = 0, Forbidden = 1, NotFound = 2, AmbiguousLegacyIdentity = 3,
    UnsupportedAction = 4, VersionConflict = 5, ValidationFailed = 6,
    StaleCursor = 7, InvalidCursor = 8, IdempotencyConflict = 9,
    KeyExpired = 10, KeyGenerationMismatch = 11, CapacityExceeded = 12,
    StorageUnavailable = 13, Unknown = 14
}
public sealed record ActionCapability(
    ManagedAction Action, bool Supported, string ReasonCode);
public sealed record ManagedDescriptor(
    ManagedObjectKey Key, string DisplayName, bool? DesiredEnabled,
    ManagedRuntimeState RuntimeState, ImmutableArray<ActionCapability> Actions,
    bool CredentialConfigured, string? ErrorCode, string DescriptorRevision);
public sealed record ManagedPageRequest(ManagedKind? Kind, int Limit, string? Cursor);
public sealed record ManagedPage(
    ManagedResultCode Code, ImmutableArray<ManagedDescriptor> Items,
    string? NextCursor, string DescriptorRevision);
public sealed record ManagedInspectResult(
    ManagedResultCode Code, ManagedDescriptor? Descriptor);
public sealed record ManagedValidationResult(
    ManagedResultCode Code, ImmutableArray<FieldError> Errors);
public enum DriverCommand { Start = 0, Stop = 1, TestConnection = 2 }
public enum PluginCommand { Start = 0, Stop = 1 }
public enum ProviderCommand { TestConnection = 0 }
public enum McpCommand { Connect = 0, Disconnect = 1, TestConnection = 2 }
public abstract record ManagedCommand
{
    private ManagedCommand() { }
    public sealed record Driver(DriverKey Key, DriverCommand Action) : ManagedCommand;
    public sealed record Plugin(PluginKey Key, PluginCommand Action) : ManagedCommand;
    public sealed record Provider(ProviderKey Key, ProviderCommand Action) : ManagedCommand;
    public sealed record Mcp(string RegisteredId, McpCommand Action) : ManagedCommand;
}
public readonly record struct ManagedOperationKey(
    ConfigurationGeneration Generation, long IssuedUnixSeconds, Guid Nonce);
public sealed record ManagedExecuteRequest(
    string ContractRevision, string ExpectedDescriptorRevision,
    ManagedOperationKey Key, ManagedCommand Command);
public enum ManagedOperationState
{
    NotStarted = 0, Running = 1, Completed = 2, Failed = 3,
    CancelRequested = 4, Cancelled = 5, Unknown = 6
}
public sealed record ManagedReceipt(
    ManagedOperationKey Key, ManagedObjectKey Target,
    ManagedAction Action, string DescriptorRevision,
    ManagedOperationState State, string? ErrorCode);
public sealed record ManagedOperationResult(
    ManagedResultCode Code, bool Replayed, ManagedReceipt? Receipt);
public sealed record ManagedCancellationResult(
    ManagedResultCode Code, ManagedOperationState State);
public interface IManagedCapabilities
{
    ValueTask<ManagedPage> ListAsync(ConfigurationCaller caller,
        ManagedPageRequest request, CancellationToken ct = default);
    ValueTask<ManagedInspectResult> InspectAsync(ConfigurationCaller caller,
        ManagedObjectKey key, CancellationToken ct = default);
    ValueTask<ManagedValidationResult> ValidateAsync(ConfigurationCaller caller,
        ConfigurationVersion expected, ConfigurationPatch changes,
        CancellationToken ct = default);
    ValueTask<ManagedOperationResult> ExecuteAsync(ConfigurationCaller caller,
        ManagedExecuteRequest request, CancellationToken ct = default);
    ValueTask<ManagedOperationResult> QueryAsync(ConfigurationCaller caller,
        ManagedOperationKey key, CancellationToken ct = default);
    ValueTask<ManagedCancellationResult> CancelAsync(ConfigurationCaller caller,
        ManagedOperationKey key, CancellationToken ct = default);
}
```

- List/Inspect纯安全查询不读秘密正文；DesiredEnabled取真实配置而非当前Snapshots硬编码true，RuntimeState/connection独立。Validate针对整batch跨域纯校验、expected不保留版本、不写DB/秘密/外部测试；Save仍最终重验，不设第二条Configure写路。Validate不能保证随后Save一定成功。
- cursor signed closed payload绑定caller/resource/kind/catalog revision/排序位置≤512byte，目录变化StaleCursor，limit0默认50，1..100否则reject；不持久无界分页会话。实现codec/hashkey及06wire要确认后落地，不创造HTTP路径。
- ManagedCommand按对象闭集；Device当前仅Inspect/Validate/Configure，没有虚构桥热启停动作。当前缺管理adapter的Driver/Plugin/Provider/MCP动作明确Unsupported，未登记NotFound，**interface存在不等于动作支持**。各后续波次必须通过真实adapter/mocks/取消与释放后才能Supported=true，MCP Wave4必选不豁免。
- Execute只使用已登记对象参数，无任意URL/env/secret输入；ExpectedDescriptorRevision不匹配VersionConflict，避免之前Unsupported的旧意图在能力改变后变成新执行。Unsupported/纯validation拒绝未被接受且无副作用；不宣称已有accepted receipt。真支持动作只有持久Accepted/NotStarted后才外部执行，不接受后内存失踪。
- 管理key独立`mg1`namespace、同第7节generation/TTL/floor/HMAC、同一总体4096/256有界预算；绑定caller/resource/target/action/descriptor revision。配置key不能作管理key。Replay/Query绝不重发远程动作；Unknown不自动重试，取消不撤已完成远端副作用。
- Query/Cancel重新核授权；Cancel仅请求协作取消，CancelRequested不冒称Cancelled，ct停止等待不等于远端已撤销；重复Cancel不会重复发非幂等外部动作，仍查询同key。需要协议cancel时通过05已冻结真实版本，不臆造SDK。
- Driver停止退订、结束owned任务/释放连接，投递已发不可保证撤回；Plugin initialized台账独立enabled、禁用后不新调工具/停机释放已初始化对象；Provider不强套Start/Stop，测试取消释放本地request/stream而不改circuit配置；MCP连接/会话/取消/释放后续真实adapter验收。报告未知，不把本地任务完成当远端成功。
- C4协议/type是否足以01管理准入及谁实现unsupported adapter由真消费者确认；一律不得以readonly catalog或当前Unsupported替代将来验收。Core仅必要跨模块contract，不搬Engine插件实现进Core。

## 9. 消费确认、用例与共享补丁

### 9.1 角色确认表（等待真实报告）

| 角色 | 本具体版本必须核对 | 状态 |
| --- | --- | --- |
| 01 | Core依赖与完整类型、整TX/NoChanges、floor/epoch/HMAC/capacity、迁移/schema/内部原语、C4管理seam | 未确认；00/设计顾问不代签 |
| 06早期 | typed wire/owned secrets、旧HTTP与UI兼容、身份及安全方案边界、提交/Find/部分应用、TTS替换seam及回退 | 未确认；不等于Wave5准出 |
| 02 | 三元及PolicyTarget、旧名单继承/官方empty、Resolver/Feishu/switch真实消费，飞书secret/env | 未确认 |
| 03 | primary意图/credentials runtime、fast旧环境投影、路由热/重启、circuit独立 | 未确认 |
| 04 | DeviceKey/nameRaw/rename与旧连接门禁、插件ID/初始化收尾、mood/TTS独立动作 | 未确认 |
| 05 | C4预留MCP闭集及取消/释放、unsupported当前边界，后续Wave4不得豁免 | 未确认 |

“确认”只能含实际执行者、时间、本文件hash、手册需求逐项、源码调用点、接受/异议及具体建议，不仅同意方向。角色技术审查不是负责人对兼容业务、安全方案或资源测量预算的批准；仍需批准项由00集中提报，不重复询问例行文档继续授权。

### 9.2 合成行为矩阵与失败归属

- C3：完整多分区合法+秘密/provider/plugin一批；末段非法类型/跨字段拒全批；每一个实际TX写点故障（settings、secret、metadata、version、ledger、audit）全回滚；失败不publish，秘密失败不得吞。
- 冻结/wire：首次await后改源数组/Dispose输入不能改变fingerprint/持久值；所有Read/ToString/JSON秘密无值/头尾，Hidden无value/nameRaw，device未知嵌套拒，nameRaw编辑不回写mask。
- 并发/幂等：同version竞争；同key同参/异参、合法不同规范表达、primaryClear/Set不同意图；NoChanges响应丢失→别人改配置→原key返回原决策；ct提交前/跨提交点、commit不确定供Find，重放无二次发布/清理/文件IO。
- 时间/容量：fake真实IClock24h/未来300边界、wall回拨/Tick倒退/算术溢出、floor事务失败后不能假Expired；受控重启新代、整体备份回退无旧key重做；跨代/命名空间/主体满额、Pending/Unknown不驱逐、codec/HMAC保留和有界清理。
- 对象：同scope多实例/账号default vs空instance、旧official空允许/私域继承/local与飞书空deny、legacy不跨scope镜像、IgnoreCase→Ordinal碰撞；provider历史secret/env引用越权、Keep改BaseUrl送key拒绝、Unconfigured transport拒、primary只读项/非primaryRestartRequired、fast环境重启一致。
- 兼容：完整142/9旧missing/null/Clamp/清除优先、19内部载荷与48readonly、无ExpectedVersion的旧稀疏writer；Token首次种子/删除不复活/缓存旧UI空串；旧device全表无法识别rename时明确拒审或批准delete-create、禁用/移除连接不default-enable；旧secret/用户配置schema合成迁移+整套旧产物/前镜像回退。
- 应用/C4：各publish/运行重建/外部TTS失败Committed事实留存、Find查询最新真实step、不自动重做unknown；mood配置组合两事实，TTS同version合并一次且只合成临时target；unsupported不得远程IO；真实supported动作每个启停/取消/资源释放/远端unknown专项。
- 只使用现有真实ReviewRemediation/SettingsScope/Safety/Production/Architecture等工程和IntegrationHarness，新增测试文件/公共入口先认领；IT前单独Rebuild Headless，修改UI运行真实FrontendProbe。上述用例**未执行**，没有编造新测试名/工程/全绿。
- 当前静态缺口归既有源码或0.2设计，非已复现产品故障；未来新失败按本批实际改路径/hash归属，未定位或兼容不明冻结受影响下游，保留架构棘轮/断言，不提高阈值换绿。

### 9.3 路径与回退安排

新Core/Storage文件仍是候选：`src/BotAgent.Core/Domain/Configuration/ConfigurationContracts.cs`、`Domain/Ports/IConfigurationChanges.cs`、`Domain/Management/ManagedCapability.cs`、`src/BotAgent.Storage/ConfigurationStore.cs`；最终划分由01/00确认，不当已有入口/自动写权。C4动作契约归Core跨模块最小类型，真实implementation按各波次而非搬全部到Storage。

00最终共享补丁队列为真实`src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs`、`AuditLogStore.cs`、`BotConfig.cs`、`ModelProviderStore.cs`，`Services/AppSettings.cs`、`SettingsBox.cs`、`Settings/SettingsHotReload.cs`，`Adapters/Panel/WebUiServer.Settings.cs`、`WebUiServer.cs`、`wwwroot/app.js`及`Host/CompositionRoot.cs`；平台/模型/Engine源改动须本波次逐文件认领/交接。没有本批代码写权，不删除旧保存通路，不同时给多个执行者写同文件。

本轮只替换00自有候选与追加总纲；原0.2-review-1文本/hash保留内存作仅本批逆向，回退前确认后续依赖，不整份覆盖他人新改。未来数据/产物回退必须前镜像+相容整套版本演练，不声称文档逆向是生产恢复。提交/推送/部署/发布/生产均无授权；最终命令、hash及正式消费报告登记只在总纲。
