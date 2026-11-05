# 插件生态 #152：M0 / M1 首批实现

## 本次边界

本次完成基线契约与首批中文统一市场，未宣称关闭整个 #152。开发在独立 `issue152-market-m1` 分支和工作树中进行，不修改 #151 的回复节奏、设置模型或发送链路；没有部署、发布 Release、推送或执行下载的社区插件。

基线：

- 本地宿主：`b80d7b271bae58bf7f0b8f4261b03f6c4ffe0199`。
- 公共宿主 dev：`dd181696afadc7a298eebda736e788f529f60b76`。
- 市场仓库：`629842c1b0187523354b7877eddffc0788c56a28`。

## 已实现

- 保留严格的 schema v1 技术目录及清单；中文资料放到独立 `presentation.json`，不向旧清单塞字段。
- 目录及已安装响应增量提供 `builtinPlugins`；内置条目没有伪造 manifest、下载地址或包哈希。成员、版本、启用状态来自宿主实际预设注册表，而非展示文件。
- 展示文件缺失、非法或无法取得时回退到旧包目录及本机内置说明；已安装接口不依赖远程网络。展示资料不能创建内置能力或授予执行权限。
- 统一市场支持中文名称、摘要、搜索与分类，以及来源、运行时、审核状态、安装状态筛选。详情包括作者、仓库、许可证、用法、配置、兼容、更新、维护、风险和权限说明。
- 内置能力只提供配置入口；可下载包保留现有信任确认和生命周期回执。Python 只作为不可安装的展示类型，不接入运行时。
- 已审核标识绑定准确版本、整包 SHA256、审核人、时间与不可变源码修订；缺失或不匹配不得继承标识。整包哈希正确不代表安全，进程内 .NET 插件不等于沙箱。

首批实际内置 ID（均为 `1.0.0`）：

| ID | 能力 |
| --- | --- |
| `preset.feature.music` | 点歌 |
| `preset.feature.voice` | 云端语音 |
| `preset.feature.stickers` | 表情包 |
| `preset.feature.poke` | 戳一戳 |
| `preset.feature.vibes` | 氛围发言 |
| `preset.feature.profiles` | 成员档案 |
| `preset.feature.websearch` | 联网研究 |
| `preset.feature.token-quota` | Token 配额 |

市场另含 `community.example.hello@1.0.0`：已有真实发布包，ZIP 为 4736 字节，仅含 `HelloPlugin.dll`，SHA256 为 `01dccbf848c9c9fbd5797e47a86339b79a4963eb585fc8157ff74768145c50cb`。本次独立复核哈希和压缩包结构，未执行、未做代码安全审核。它是 .NET 加载契约示例，不提供聊天回复、命令或后台任务；首批均不展示独立安全审核通过标识。

## 联调修复与回归重点

- 安装态只控制内置成员、版本和启用状态；同 ID / 版本的远程中文说明不会被简略本机回退覆盖。
- Wire 默认忽略 null，但内置审核记录必须保留 `sha256: null`；已单独保护该字段并验证实际序列化选项。
- 非法展示资料触发回退，不传播 `InvalidDataException` 导致整个市场不可用。
- 版本、哈希、源码修订与时间严格验证，不能使用相近版本、41 位修订或不完整时间伪装精确审核。
- 前后端均保留旧响应兼容性，既有安装、升级、回滚和卸载协议不变。

## 验证

测试均使用合成数据或公开插件产物，不连接线上机器人，不读取真实会话、成员档案或数据库。

| 检查 | 结果 |
| --- | --- |
| Headless 独立 Release 构建 | 0 警告、0 错误 |
| PluginPackageProbe | 109 项通过 |
| PluginMarketProbe | 132 项通过 |
| ZbaProbe | 418 项通过、0 失败、0 跳过 |
| ArchitectureProbe | 219 项通过、0 失败；未提高基线 |
| 前端类型检查及生产构建 | 通过 |
| 前端完整单元测试（2 workers） | 56 文件、668 项通过 |
| 市场技术目录、展示 schema 与单元测试 | 两个校验器通过，38 项测试通过 |
| 真实市场文件交叉解析 | 新宿主解析 9 项：8 内置 + 1 包；不执行插件 |
| Chromium 有头浏览器回归 | 原有 5 场景 + 新增 4 场景，共 9 项通过；含实际内置 Wire、离线回退、390px 移动端 |

前端默认并发复跑曾有 1 项既有的真实 HTTP 超时测试失败：100ms 内服务器尚未发送 headers。该测试所在文件单独复跑 2/2 通过，限制 worker 并发后完整 668 项通过；没有修改测试断言、延长 deadline 或跳过测试。

关键复跑命令（各仓库根目录执行；前端命令在 `src/web`）：

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.PluginPackageProbe -c Release
dotnet run --project tests/BotAgent.PluginMarketProbe -c Release
dotnet run --project tests/BotAgent.ZbaProbe -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe -c Release -- --repo-root .
npm run typecheck
npm run build
npm run test:unit -- --maxWorkers=2
npx playwright test tests/e2e/marketplace.spec.ts tests/e2e/plugin-market-presentation.spec.ts --project=chromium --headed
python tools/validate.py
python tools/validate_presentation.py
python -m unittest discover -s tools -p "test_*.py"
```

Windows 后端探针验证将 `TMP` / `TEMP` 指向工作区内的合成测试目录，避开受限个人临时目录；不修复或访问个人目录 ACL。浏览器配置单独创建合成数据目录，并清除生产机器人环境变量。首轮浏览器回归发现地址导入输入框被默认折叠，现恢复默认可见并放到发现区之后；原有测试断言未改，最终 9/9 通过。未运行 Firefox / WebKit 或全站 E2E。

## 后续仍待实现

M2 安装前预览与完善管理、M3 多插件/多版本仓库导入、M4 审计与撤销策略、M5 作者发布流水线、M6 Python 运行时及隔离不属于本阶段完成项。特别是展示字段不提供撤销强制执行或进程隔离保证。
