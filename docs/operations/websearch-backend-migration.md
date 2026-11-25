# 搜索协调包迁移状态

`backend/BotAgent.Package.WebSearch.csproj` 是第二个独立生产包工程，版本由包自己的 `Version` 决定。独立二进制构建提供 BotAgent.Core.dll、BotAgent.Plugin.Contracts.dll、BotAgent.Research.Contracts.dll，不依赖 Engine、Storage 或 Headless 工程。搜索提供商、页面解析、链接预览、冷却、一次性笔记、诊断协调及聊天工具均在包内。默认宿主从独立 ZIP 加载，`ResearchUseCase` 只保留当前代次的能力路由。

七个公开类型由 `contracts/research` 的 BotAgent.Research.Contracts 1.0.0 持有；Core 和既有 Engine 入口保留类型转发。`IResearchDiagnostics` 区分面板自测与会话笔记：自测不写笔记。清单的共享契约绑定 owner、版本、哈希；独立 provider 可引用宿主批准的契约而无需反向依赖研究业务包。该引用要求 reader 9；普通工具声明仍要求 reader 8。详见 `docs/architecture/research-contracts.md`。

实际包通过 `IPluginDependencies.Get<T>(providerId)` 获取清单中直接声明、固定版本的提供方。能力绑定消费方的当前激活代次；初始化阶段不能解析，消费方或提供方换代后旧能力失效。每次贡献调用持有消费方及传递依赖租约，异步搜索完成后释放。包通过公共时钟能力读取时间，不接收整个宿主配置或注册表。

`providerId` 由现有插件设置传入，必须与清单依赖对应。默认发行包直接使用经权限批准的模型、公共页面传输、工具执行和窄化研究选项能力。初始化仅保存能力；不读时钟、不出网。所有新贡献仍只允许冷采用，不宣称热切换保留在途回复或笔记。禁用业务时控制面仍可提供独立诊断。

合成验证使用分别编译的实际研究包与传输适配夹具 DLL，调用包内 `WebSearchService`；HTTP 返回本地合成内容。覆盖冷却边界、搜索/页面解析、危险 URL、笔记一次性消费/保留、诊断无笔记、退役排空和旧代次拒绝。保存迁移前真实 Core 与研究 ZIP，验证旧包仍可通过类型转发调用当前宿主，并验证实际 reader 8 安全拒绝 reader 9 状态。公共网络传输仍由宿主批准并提供；未使用真实服务或会话。

后续 D1.3 仍需迁移设置/env/schema、诊断 HTTP/DTO/UI 等完整资源所有权。九个面板字段现为即时生效，通用插件设置为重启采用，迁移必须明确唯一写入者与兼容语义。独立契约和默认发行包不代表 P07/P08 或完整 R2 已完成；运行时 UI、组合恢复及系统模块拆分继续按执行清单推进。
