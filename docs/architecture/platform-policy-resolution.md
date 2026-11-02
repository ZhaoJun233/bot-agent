# 平台策略的判定路径

这页解释「面板里的策略条目最后怎么变成一次放行/拦截」，以及 #129 修掉的三个坑：
空 `instanceId` 的实例目标、名单的三种空状态、名单文本的归一与快照新鲜度。
改判定前先读这一页，别只看某一个函数。

## 1. 目标（target）是什么

策略条目的目标有两种：

| 目标 | 构造 | 匹配谁 |
| --- | --- | --- |
| 账户默认 | `PolicyTarget.AccountDefault(platform, account)` | 这个平台账户下所有实例，通道级判定（没有实例身份）走它 |
| 实例 | `PolicyTarget.Instance(DriverKey(platform, account, instanceId))` | 具体一个已登记驱动 |

**空 `instanceId` 不是野目标，它本身就是内置驱动的身份。** `CompositionRoot.DeclarePlatformCatalog`
声明了 4 个驱动，其中 3 个的 `InstanceId` 就是空串：

- `qq.private / legacy / ""`
- `qq.official / legacy / ""`
- `local / legacy / ""`
- `feishu / default / feishu-main`

`PlatformSwitchSettings.ValidatePolicyEntries` 要求每个目标必须是 `DeclaredPlatformDrivers` 里的驱动，
所以「实例目标 + 空 id」只可能等于上面这些内置驱动，不可能是拼错/残留的条目标识。
有名字的实例目标（如 `feishu-main`）才是「某个具体实例」。

## 2. 通道级判定（没有实例身份时）

`PlatformPolicyResolver.ResolveForChannel` 与 `WhitelistGate.AllowsCanonical` 的规则：

1. 该平台**只登记一个**驱动，且该驱动的 `InstanceId` 为空 → 按 `Instance(该驱动)` 结算；
   这条是 #129 S3 的修复：以前一见到任何实例条目就直接失败关闭，一条空 id 条目能锁死整个平台。
2. 该平台**只登记一个**驱动，但它的 `InstanceId` 非空（如飞书）→ 按 `AccountDefault` 结算。
3. 存在**有名字**的实例条目 → 通道级判定失败关闭（`source_context_required`）。多实例隔离必须靠
   带上下文的路径，这是有意的 fail-closed。
4. 登记了多个驱动但无法确定归属 → 同样失败关闭。

带上下文的路径（`PlatformSwitchSettings.ResolvePolicyRules(settings, new PolicyTarget.Instance(context), actions)`）
不受上述限制：它本来就知道自己是谁，直接命中对应条目。

## 3. 名单的三种「空」

| 规则 | 写法 | 含义 | 读侧显示 |
| --- | --- | --- | --- |
| `ListRule.Inherit()` | kind=0 | 跟随基线（标量白名单文本） | 跟随基线 |
| `ListRule.Explicit(ids)` | kind=1 | 只收这些 id | 名单内容 |
| `ListRule.Explicit("")` | kind=1 + 空串 | **全拦**（名单外所有都拦） | 显式空（全拦名单外所有） |
| `ListRule.AllowAll()` | kind=2 | **全收**（不再看基线） | 全收（不限） |

坑在于官方通道的**标量**留空是「全收」：`PlatformSwitchSettings.ScalarListRules` 对
`qq.official` 的空文本返回 `AllowAll()`，而条目里的显式空串是「全拦」。两种「空」含义相反，
所以读侧必须给可读标签（`SettingsStore.PolicyViews` 的 display 与面板 `policySummary`），
不能都显示成空串或 `[policy]`。

`Explicit("")` **不允许**被归一成 `Inherit` 或 `AllowAll`：它是独立状态，
`ConfigurationContractTests` 有专门用例固定这一点（显式空会停止继承、并由真实闸门执行）。

## 4. 名单文本归一（#129 S8）

策略条目落库前在 `ConfigurationStore.FreezeList` 归一：按逗号/分号/空白切分、去空项、
按首次出现顺序去重、统一成逗号分隔。归一只发生在写库这一层，判定照旧按 token 集合走。
显式空串保持空串。于是「提交 `10001, 10001`」与「提交 `10001`」落成同一条记录，回读与提交一致。

## 5. 快照与新鲜度（#129 S7）

`WhitelistGate` 把三份名单解析成 `HashSet` 快照，正常路径是设置热更新的 `RuntimePolicy` 步
显式调用 `WhitelistGate.Rebuild()`。任何**绕过**热更新直接发布配置的路径都可能忘记重建，
于是面板显示「已保存」而闸门仍按旧名单拦。现在 `Rebuild` 记录它用的配置版本引用，
`AllowsSource` / `AllowsKey` / `Describe` 开头调用 `EnsureFresh()`：引用变了就加锁重建一次，
没变就是一次引用比较。热更新路径行为不变。

## 6. 改这里要看哪里

| 想改的东西 | 落点 |
| --- | --- |
| 目标匹配 / 通道级失败关闭 | `src/BotAgent.Core/Contracts/PlatformPolicyResolver.cs`、`src/BotAgent.Engine/Qq/WhitelistGate.cs` |
| 目标校验与冻结 | `src/BotAgent.Storage/ConfigurationStore.cs`（`FreezeTarget` / `FreezePolicy` / `FreezeList`） |
| 基线合成与标量同步 | `src/BotAgent.Core/Contracts/PlatformSwitchSettings.cs`、`src/BotAgent.Headless/Adapters/Persistence/PlatformScalarSync.cs` |
| 面板读侧视图与显示标签 | `src/BotAgent.Headless/Adapters/Persistence/SettingsStore.cs`（`PolicyViews`）、`src/web/src/features/configuration/PlatformPolicyEditor.vue` |
| 启动横幅 | `src/BotAgent.Headless/Services/Ops/BootReport.cs` |
