# 插件生态 #152：M2 安全导入闭环

## 交付边界

本轮完成隔离分支上的 M2 实现与合成验证，不表示关闭整个 #152，也没有发布、部署或执行下载的社区插件。仓库多插件、多版本选择的基础能力一并纳入本轮；其他 M3–M6 能力不据此宣称完成。

- 宿主分支：`issue152-market-m2`，基线 `327037a484d5d303081ea134b73ce04999e57632`。
- 市场分支：`issue152-import-m2`，基线 `f350efcb528f364a4a615a57420be56fe2a90887`。
- 不修改 #151 的主工作树、AppSettings 模型、回复节奏或发送链路；不提高 ArchitectureProbe 基线。
- 仅使用合成令牌、临时数据根和 mock 模型。公开 Hello 产物仅校验既有哈希与压缩包结构，不执行。

## 用户流程与协议

1. 仓库导入先发现候选，再明确选择插件 ID 与版本；不猜测“第一个”或“最新”。
2. 仓库、市场目录、高级 JSON 三种入口统一先预览，展示完整清单、来源修订、SHA256、兼容性、依赖和风险。
3. 必须针对当前预览重新勾选风险确认。来源、版本、清单或安装态变化，以及过期、关闭对话框和失效响应，均不能沿用旧确认。
4. 确认请求携带预览 ID、完整清单、安装态 revision 与幂等键。首次安装需要有效预览；已完成操作按原回执重放，不因预览过期重新下载或安装。
5. 安装成功后的持久化目标状态是停用；启用仍需另行通过既有配置入口完成。

新增接口均要求服务端 Admin 权限，但发现和预览不写设置、不预留操作回执、不下载 ZIP、不加载插件。前端管理员模式不是服务端授权的替代。

| 接口 | 含义 |
| --- | --- |
| `POST /zba/v1/plugins/source/discover` | 获取固定源码修订中的插件/版本候选 |
| `POST /zba/v1/plugins/preview` | 生成调用方绑定、只读的安装影响预览 |
| `POST /zba/v1/plugins/install` | 新请求必须提供 `previewId`；缺失返回 `409 plugin_preview_required` |

详见 [API 清单](<api-surface.md>)。旧单候选 resolve 接口保持只读；升级、回滚、卸载协议未迁入这次安装预览。旧客户端不能继续发起无预览的新安装，但已有完成回执的重放兼容保留。

## 固定候选与安全边界

- 新仓库描述采用独立的 `repository.json`；旧技术目录和清单 schema 不增加字段。描述文件限制为 2 MiB、合计最多 1024 个候选，重复标识、版本、字段和清单不一致会被拒绝。
- 仓库/Release tag 解析到实际 commit；tag 不用同名 branch 代替。只有描述文件 404 才回退到同一 commit 的旧目录。
- 仓库元数据限定 GitHub 相关 HTTPS 主机，重定向同样校验；现有自定义公共 HTTPS 市场目录能力不被新策略误伤。
- 预览缓存绑定调用方、完整技术清单、来源快照和安装态；10 分钟有效，最多 64 项、合计 8 MiB 序列化载荷。进程重启或条目淘汰后重新预览。
- 确认前重新校验来源、revision、依赖和过期时间；目录内容及展示侧车变化也会使确认失效。预览中 ZIP/程序集校验仍显示待执行，不谎称已经校验文件。
- 任意外部仓库自称“官方”或“已审核”的展示资料降级为社区、未审核；审核元数据不能代替权限或沙箱。
- 通过既有授权配置协调器持久化停用意图，再发布可激活的 Pending 包。使用当前异步流内的准备标识，不把未安装 ID 全局注册。配置提交失败、取消或安装态 CAS 冲突均不能发布 Pending。
- 包准备期间不持有会阻塞配置热应用重入的锁；重新取得锁后复核 revision。陈旧的 enabled=true 在卸载后重装及重启路径上不能被继承。

**停用不等于零代码执行。** 进程内加载程序集、构造函数、模块初始化或属性读取仍可能执行第三方代码；停用阻止功能初始化不代表隔离。整包哈希正确也不代表安全。Python 执行、进程沙箱、强制撤销均未在本轮实现。

市场描述目前只列出真实的 `community.example.hello@1.0.0`。多插件、多版本覆盖来自合成夹具，不伪造新 Release。

## 当前候选验证（2026-11-06）

| 检查 | 原生命令结果 |
| --- | --- |
| Headless 独立 Release 构建 | 0 警告、0 错误 |
| PluginMarketProbe | 184 项通过 |
| PluginPackageProbe | 109 项通过 |
| ZbaProbe | 423 通过、0 失败、1 环境性跳过 |
| ArchitectureProbe | 219 通过；未提高基线 |
| 前端干净安装、typecheck、生产 build | 全部通过 |
| 前端完整 unit（2 workers） | 60 文件、736 项通过 |
| 市场技术清单、展示侧车、仓库描述及测试 | 校验通过、70 项测试通过 |
| 实际宿主解析实际市场描述 | 1 个候选、6 个身份/产物字段一致；不下载或执行插件 |
| Chromium + Firefox + WebKit 默认无头回归 | 相同两份测试文件、相同断言，每个引擎 10 项，共 30/30 通过 |
| Chromium + WebKit 有头回归 | 两份测试文件、每个引擎 10 项，共 20/20 通过 |
| Firefox 单独有头复跑 | 6 通过、4 个点击超时；未宣称该引擎验收通过 |

浏览器覆盖插件/版本选择、完整预览、显式确认、候选变更失效、高级 JSON，以及真实合成宿主的无预览/篡改拒绝。测试中的模拟确认响应用于验证绑定请求和拒绝后的不自动重试；真实安装默认停用及重启行为由宿主探针验证。

首轮浏览器 23/30 通过。新测试的模拟目录缺少必需状态字段，导致严格解码拒绝；随后只修正夹具并增加静态类型，未改断言、强制点击、延长点击超时或跳过失败测试。原始测试文件、失败日志及截图保留。一次后续运行因前轮测试端口尚在清理而未启动；没有复用或杀掉未知监听进程。

Firefox 有头运行的四次超时发生在导航或管理员前置点击。随后以相同用例、断言和单 worker 运行默认无头三引擎矩阵，30/30 通过，无产品、用例或等待策略改动。这将当前失败限定在有头运行条件，但不能确定深层根因；有头失败独立保留，不用其他模式的绿灯覆盖。仓库外的一次有界诊断未复现：页面 visible、focused，普通脚本 44ms 返回，两帧心跳 6ms 返回，原生点击成功，未触发窗口置前分支。这不足以确定根因，不能断言是已知 Windows 问题。诊断不是验收用例，也不替代原断言。

ZbaProbe 的跳过原文：`HttpListener smoke: HttpListenerException (loopback bind not permitted in this environment)`。浏览器用本次独立构建的 Headless DLL 启动真实合成宿主，已执行新增 API wire 拒绝路径；这不消除上述独立 smoke 的跳过事实。

## 复跑

宿主在仓库根目录执行，前端在其自身目录执行；请先清除真实机器人/模型环境变量并使用临时数据根。浏览器配置会创建合成数据，不能换成线上面板。

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.PluginMarketProbe -c Release
dotnet run --project tests/BotAgent.PluginPackageProbe -c Release
dotnet run --project tests/BotAgent.ZbaProbe -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe -c Release
# 再次单独构建宿主，确保浏览器不会拿到陈旧 DLL
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
cd src/web
npm ci
npm run typecheck
npm run test:unit -- --maxWorkers=2
npm run build
# 默认无头三引擎矩阵：30/30 通过
npx playwright test tests/e2e/marketplace.spec.ts tests/e2e/plugin-import.spec.ts --workers=1 --project=chromium --project=firefox --project=webkit
npx playwright test tests/e2e/marketplace.spec.ts tests/e2e/plugin-import.spec.ts --headed --workers=1 --project=chromium --project=webkit
# Firefox 当前有未解决的点击超时，单列而非跳过
npx playwright test tests/e2e/marketplace.spec.ts tests/e2e/plugin-import.spec.ts --headed --workers=1 --project=firefox
```

## 未完成与发布门禁

- M0 的 SDK / 目标框架 / OS 完整执行矩阵仍待验证。本轮环境为 Windows、Node 24.22.1、npm 11.19.1、.NET SDK 10.0.101、Python 3.13.12、Playwright 1.64.1；不能把 net8.0 资产解析或 net10.0 宿主构建冒充完整兼容矩阵。
- 默认无头三引擎指定用例已通过；Firefox 有头点击超时尚未形成修复闭环，不宣称全部运行条件或全站 E2E 通过。
- M4 安全审核与强制撤销、M5 作者发布流水线、M6 Python 运行时及隔离，以及 M3 其余未覆盖的生态工作仍须另行交付。
- 本轮没有用户视觉签收，没有自动合并、提交、推送、Release 或部署。工程测试结果不自动解锁发布。