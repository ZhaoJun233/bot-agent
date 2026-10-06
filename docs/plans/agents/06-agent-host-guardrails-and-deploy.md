# Agent 6 执行手册：宿主收口、架构探针棘轮与发布验证

> **文档标识**：`docs/plans/agents/06-agent-host-guardrails-and-deploy.md`\
> **执行代号**：`Agent-06` (Host-QA-Deploy)\
> **所属波次**：**Wave 5（终局验收与发布收口波次）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **更新时间**：2026-10-06\

---

## 1. 任务目标与范围 (Objective & Scope)

作为重构序列的收口与全局验收责任人，Agent 6 负责整个系统工程的收敛、架构护栏升级与发布验证：
1. **宿主工程净化与极简收口**：将 `BotAgent.Headless` 精简为只保留入口 `Program.cs` 与组装根 `CompositionRoot.cs` 的超轻量启动器（宿主代码缩减至几百行）；
2. **架构探针 (`ArchitectureProbe`) 升级**：
   - 扩展扫描器，纳管 `src/` 下全部 6 个工程与 16 个测试工程；
   - 编写多项目单向无环依赖图 (DAG) 物理断言，严防未来任何破窗反向依赖；
   - 按照单向棘轮机制重新校准并下调 `Baseline.cs` 中的阈值；
3. **全景测试矩阵回归**：统筹回归 `SafetyProbe` (594 项)、`FrontendProbe` (365+ 项)、`ArchitectureProbe` (95+ 项) 与全套集成测试矩阵 (S21, S36, S52 等)；
4. **单进程发布与 913MB 内存约束验证**：模拟打包 `app.tar.gz` 与 `dotnet publish` 平铺产物，验证单进程常驻内存（40~60MB）与嵌入式 SPA 正常供给。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：**Wave 1 ~ Wave 4 (Agent 1 至 Agent 5) 的全部子任务必须均已完成并通过各阶段自验**。
- **输入交付物**：
  - `BotAgent.Core.dll`
  - `BotAgent.Model.dll`
  - `BotAgent.Storage.dll`
  - `BotAgent.Platforms.dll`
  - `BotAgent.Engine.dll`
  - `BotAgent.Panel.dll`

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 调整）
- `src/BotAgent.Headless/Host/Program.cs`
- `src/BotAgent.Headless/Host/CompositionRoot.cs`
- `tests/BotAgent.ArchitectureProbe/**`（升级扫描范围，更新 `Baseline.cs`）
- `BotAgent.slnx`（最终对齐与格式收尾）

### 3.2 观察与验证权限（只读执行）
- 全项目所有工程源码与测试套件
- 部署与打包脚本

---

## 4. 核心契约与设计规范 (Specifications)

### 4.1 极简宿主 `CompositionRoot.cs` 设计
`CompositionRoot.cs` 仅作为模块化单体的组装流水线，负责将各模块服务注入 ASP.NET Core / Generic Host：
```csharp
namespace BotAgent.Host;

public static class CompositionRoot
{
    public static IHost BuildHost(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args);

        builder.ConfigureServices((ctx, services) =>
        {
            // 1. 注册核心领域契约与时间/网络
            services.AddBotCore();

            // 2. 注册存储与持久化 (SQLite, Secrets, PluginStore)
            services.AddBotStorage();

            // 3. 注册大语言模型传输与思考预算
            services.AddBotModel();

            // 4. 注册聊天平台通道 (OneBot, Official, Feishu, Local)
            services.AddBotPlatforms();

            // 5. 注册回复流水线与插件引擎
            services.AddBotEngine();

            // 6. 注册管理面板 Web UI (Minimal API & SPA)
            services.AddBotPanel();
        });

        return builder.Build();
    }
}
```

### 4.2 架构探针 DAG 依赖单向约束 (DAG Guardrails)
在 `tests/BotAgent.ArchitectureProbe/` 中新增严格的工程引用拓扑校验：

| 工程 | 允许引用的工程 | 严禁引用的工程（违规立即阻断） |
| :--- | :--- | :--- |
| **`BotAgent.Core`** | 仅外部基础库 (Logging) | **严禁引用任何同级或上层工程** |
| **`BotAgent.Model`** | `BotAgent.Core` | `Storage`, `Platforms`, `Engine`, `Panel`, `Headless` |
| **`BotAgent.Storage`**| `BotAgent.Core` | `Model`, `Platforms`, `Engine`, `Panel`, `Headless` |
| **`BotAgent.Platforms`**| `BotAgent.Core` | `Model`, `Storage`, `Engine`, `Panel`, `Headless` |
| **`BotAgent.Engine`** | `Core`, `Model`, `Storage`, `Platforms` | `Panel`, `Headless` |
| **`BotAgent.Panel`**  | `Core`, `Storage`, `Engine` | `Platforms`, `Headless` |
| **`BotAgent.Headless`**| 全部工程 | 仅作为宿主根，严禁被其他任何工程反向引用 |

### 4.3 生产部署与物理红线 (AWS 913MB RAM)
- 严禁拆分为独立微服务守护进程；
- 最终产物仍为单一可执行 DLL：`BotAgent.Headless.dll`；
- 打包输出必须是单目录平铺（包含依赖的 6 个内部 DLL 与第三方运行时）；
- 单进程常驻内存指标维持在 40~60MB，在 913MB EC2 宿主上稳定无压运行。

---

## 5. 详细执行步骤 (Step-by-Step Instructions)

### 步骤 1：精简宿主代码
1. 检查 `src/BotAgent.Headless/`，确认所有非 Host 代码已全部移出至对应子模块；
2. 重写 `Program.cs` 与 `CompositionRoot.cs`，使用干净的扩展方法进行模块拼装；
3. 验证 `BotAgent.Headless` 代码量降至 500 行以内。

### 步骤 2：升级架构护栏 (`ArchitectureProbe`)
1. 修改 `SourceIndex.cs`，支持跨 `src/BotAgent.*` 多目录索引；
2. 增加对各 `.csproj` 中 `<ProjectReference>` 的静态扫描断言，确保完全符合 DAG 约束矩阵；
3. 执行 `dotnet run --project tests/BotAgent.ArchitectureProbe/ --print`，将下调后的指标回填到 `Baseline.cs`。

### 步骤 3：全景测试矩阵大回归
依次执行所有核心探针和集成测试，确保 0 红灯。

### 步骤 4：本地模拟发布打包
执行本地发布指令，检查发布包体积与文件完整性：
```pwsh
dotnet publish src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -o dist/app
```
验证 `dist/app` 中同时存在 `BotAgent.Headless.dll`、`BotAgent.Core.dll`、`BotAgent.Engine.dll` 等所有模块 DLL 及嵌入式 Web 资源。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
```pwsh
# 1. 运行全景解决方案编译
dotnet build BotAgent.slnx -c Release

# 2. 运行架构棘轮探针（95+ 项断言，含 DAG 防破窗校验）
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release

# 3. 运行前端探针（365+ 项断言）
node tests/BotAgent.FrontendProbe/probe.mjs

# 4. 运行安全与隐私探针（594 项断言）
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release

# 5. 运行集成测试核心场景集
$env:QQCHAT_IT_ONLY='s21'
dotnet run --project tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release

$env:QQCHAT_IT_ONLY='s36'
dotnet run --project tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release

$env:QQCHAT_IT_ONLY='s52'
dotnet run --project tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
```

### 6.2 交付验收准则 (DoD)
- [ ] 全解决方案 `BotAgent.slnx` 编译 0 报错 0 警告；
- [ ] `ArchitectureProbe` 全绿，DAG 单向依赖防破窗测试 100% 成立；
- [ ] `SafetyProbe` 594 项全绿；
- [ ] `FrontendProbe` 365+ 项全绿；
- [ ] 集成测试 S21、S36、S52 全部通过；
- [ ] 本地发布目录 `dist/app` 结构平铺合规，单进程运行内存实测在 40~60MB 范围；
- [ ] 无任何真实群聊发言、QQ 号、密钥等隐私泄露。

---

## 7. 终局交付物 (Final Deliverables)

1. 完整的模块化单体工程结构（`BotAgent.Core`, `Model`, `Storage`, `Platforms`, `Engine`, `Panel`, `Headless`）；
2. 全景绿灯的自动化探针报告；
3. 一份可直接投入生产构建并部署在 913MB AWS EC2 的单进程应用包。
