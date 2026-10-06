# Agent 5 执行手册：控制面独立、插件写路径与交互待办闭环

> **文档标识**：`docs/plans/agents/05-agent-panel-and-ux-backlog.md`\
> **执行代号**：`Agent-05` (Panel-UX)\
> **所属波次**：**Wave 4（控制面与前端交互波次）**\
> **所属方案**：BotAgent 模块化重构与功能插件化演进方案 (`modular-monolith-refactoring-plan.md`)\
> **更新时间**：2026-10-06\

---

## 1. 任务目标与范围 (Objective & Scope)

Agent 5 负责系统的管理面、Web API、静态单页应用（SPA）以及 PR #86 遗留的所有交互待办事项闭环：
1. **创建 `BotAgent.Panel` 工程**：封装内嵌 HTTP 服务器 (`WebUiServer.*.cs`)、路由注册与分发器 (`PanelRoutes.cs`)、通知机制 (`PanelNotifier.cs`) 及嵌入式静态 Web 资源；
2. **打通插件控制面写路径 (解决脱节 3)**：
   - 在 `PanelRoutes.cs` 注册 `POST /api/plugins/{id}/enable`；
   - 在 `WebUiServer.Plugins.cs` 实现状态切换并与 `PluginStore`、`PluginManager` 内存状态热联动；
   - 在前端为插件卡片增加 `<input type="checkbox" class="plugin-toggle">` 交互；
3. **闭环 PR #86 交互待办 (P1, P5, P6/P7)**：
   - **P1（底栏显隐机制重构）**：触控上滑/获焦/静置（10s/5s）隐藏，下滑/点击呼出，切页复位，首次隐藏常驻引导条；
   - **P5（双保存按钮收敛）**：清除分散的卡片内保存按钮，统一收敛为底部常驻大保存条；
   - **P6/P7（插件中心视觉美化）**：增加卡片呼吸内边距、状态运行呼吸灯、分类色标徽章；
4. **验证类待办全链路闭环 (V1-V3)**：
   - 在 `probe.mjs` 中补充 Agent Dialog 模态打开、无损归还、表单提交与多次切换的自动化断言，断言数从 350 项扩充至 365+ 项。

---

## 2. 前置依赖与输入条件 (Prerequisites)

- **前置任务**：**Wave 3 的 Agent 4 (Engine-Gating) 必须已交付完成**。
- **输入契约**：
  - `BotAgent.Core.dll`（插件契约）
  - `BotAgent.Storage.dll`（`PluginStore` 持久化）
  - `BotAgent.Engine.dll`（`PluginManager` 内存状态）

---

## 3. 文件读写权属清单 (File Ownership)

### 3.1 独占写权限（创建 / 迁移 / 修改）
- `src/BotAgent.Panel/`（新建工程）：
  - `src/BotAgent.Panel/BotAgent.Panel.csproj`
  - 迁移自 `Adapters/Panel/**`（全量 `WebUiServer.*.cs`、`PanelRoutes.cs`、`PanelDeploy.cs` 等）
  - 迁移自 `Services/Panel/**`（`PanelNotifier.cs`）
  - 托管静态资源：`src/BotAgent.Panel/wwwroot/**`（`index.html`, `app.js`, `app.css` 等）
- `tests/BotAgent.FrontendProbe/probe.mjs`（扩充 P1/P5/P6/P7/V1-V3 断言）
- `BotAgent.slnx`（挂载 `BotAgent.Panel`）
- `src/BotAgent.Headless/BotAgent.Headless.csproj`（引用 Panel）

### 3.2 只读 / 严禁触碰目录
- `src/BotAgent.Core/**`
- `src/BotAgent.Model/**`
- `src/BotAgent.Storage/**`
- `src/BotAgent.Engine/**`

---

## 4. 核心功能与交互规范 (Specifications)

### 4.1 `BotAgent.Panel.csproj` 定义与静态资源嵌入
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>BotAgent.Panel</RootNamespace>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\BotAgent.Core\BotAgent.Core.csproj" />
    <ProjectReference Include="..\BotAgent.Storage\BotAgent.Storage.csproj" />
    <ProjectReference Include="..\BotAgent.Engine\BotAgent.Engine.csproj" />
  </ItemGroup>
</Project>
```

### 4.2 插件管理控制面写接口设计 (Write API)
在 `PanelRoutes.cs` 中增加路由：
```csharp
new("POST", PanelMatch.Prefix, "/api/plugins/", HandlePluginEnableAsync),
```
在 `WebUiServer.Plugins.cs` 中实现逻辑：
- 提取 URL 中的 `pluginId`（如 `preset.feature.music`）；
- 解析请求体：`{ "enabled": true/false }`；
- 调用 `_pluginManager.SetEnabled(pluginId, enabled)` 同步更新内存注册表；
- 调用 `_pluginStore.SetPluginEnabled(pluginId, enabled)` 写入 SQLite，热生效无需重启；
- 返回 JSON：`{ "success": true, "id": pluginId, "isEnabled": enabled }`。

### 4.3 P1 底栏显隐机制规范 (`app.js` / `app.css`)
1. **隐藏状态触发**：
   - 页面向上滑动（`deltaY > 10`）；
   - 输入框获取焦点（`focus` 事件）；
   - 用户无操作静置定时器：首次加载 10 秒无交互自动隐藏；滚动停止后 5 秒无交互自动隐藏；
2. **呼出状态触发**：
   - 页面向下滑动（`deltaY < -10`）；
   - 点击非底栏任意主内容空白区域；
3. **切页复位**：
   - 监听 Tab Bar 点击切页（5 个主页面），切页时强制重置为显示状态；
4. **引导提醒条**：
   - 首次隐藏时在底部弹出轻提示条（“底栏已隐藏，向下滑动或点击空白处唤出”），并写入 `localStorage.setItem("bottom_bar_hint_seen", "1")`，仅展示一次。

### 4.4 P5 双保存按钮交互收敛
- 移除各卡片内部孤立的“保存”小按钮；
- 统一收敛为悬浮固定在底栏上方或右下角的全局大保存条（带有脏数据检测提示：`未保存变更`）；
- 保存时自动收集当前激活视口的全部表单项并执行原子保存。

### 4.5 P6/P7 插件中心视觉美化 (`app.css`)
- 插件网格采用弹性栅格，每个卡片设置呼吸感内边距（`padding: 16px 20px; border-radius: 12px;`）；
- 卡片右上角增加动态运行状态指示灯（`.status-dot.active` 为柔和绿色脉冲微动效，`.status-dot.inactive` 为浅灰色）；
- 卡片标题旁附带类别徽标（`.badge.media`、`.badge.feature`、`.badge.channel`）。

---

## 5. 详细执行步骤 (Step-by-Step Instructions)

### 步骤 1：创建 `BotAgent.Panel` 工程与静态文件迁移
1. 创建 `src/BotAgent.Panel` 及工程文件；
2. 将 `src/BotAgent.Headless/Adapters/Panel/` 与 `Services/Panel/` 迁入 `src/BotAgent.Panel/`；
3. 将 `src/BotAgent.Headless/wwwroot/` 迁入 `src/BotAgent.Panel/wwwroot/`；
4. 挂入 `BotAgent.slnx` 并更新 `BotAgent.Headless` 引用。

### 步骤 2：实现插件写路由与端点
1. 在 `PanelRoutes.cs` 注册写路由；
2. 在 `WebUiServer.Plugins.cs` 实现 `HandlePluginEnableAsync`；
3. 在 `app.js` 绑定插件列表渲染时的 Switch 开关 `change` 事件，异步调用 `/api/plugins/{id}/enable`。

### 步骤 3：施工前端 UX 待办 (P1, P5, P6/P7)
1. 在 `app.js` 实现手势监听、定时器与引导提示条（P1）；
2. 调整 `index.html`，清除子卡片保存按钮，规范全局底栏常驻大保存条（P5）；
3. 在 `app.css` 完善插件卡片网格间距、状态指示灯与徽标样式（P6/P7）。

### 步骤 4：扩充 `probe.mjs` 测试集 (V1-V3)
在 `tests/BotAgent.FrontendProbe/probe.mjs` 中添加针对 Agent Dialog 模态、插件切换开关、底栏显隐手势逻辑与保存条的断言用例，确保断言数升至 365+ 项。

---

## 6. 自测命令与验收标准 (Verification & DoD)

### 6.1 验证命令集
```pwsh
# 1. 验证 Panel 独立构建
dotnet build src/BotAgent.Panel/BotAgent.Panel.csproj -c Release

# 2. 验证宿主整体构建
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 3. 运行前端探针（必须达到 365+ 项断言全通过）
node tests/BotAgent.FrontendProbe/probe.mjs

# 4. 执行 SafetyProbe 探针（确保 WebUiServer 认证鉴权未退化）
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll
```

### 6.2 交付验收准则 (DoD)
- [ ] `BotAgent.Panel` 独立构建 0 错误；
- [ ] `probe.mjs` 测试项数达到 365+ 项，0 失败；
- [ ] 底栏手势（上滑隐、下滑现、静置 10s/5s、切页复位）逻辑完整且有探针断言守护；
- [ ] 全局大保存按钮取代多卡片分散保存，交互体验统一；
- [ ] `POST /api/plugins/{id}/enable` 端点成功切换插件状态并在内存/数据库双向生效；
- [ ] 插件卡片网格样式符合现代化呼吸感视觉标准。

---

## 7. 交付物与下游交接 (Handoff Deliverables)

1. 产出物：`BotAgent.Panel.dll` 与最新的前端静态包
2. 状态标记：**Wave 4 完成，正式解锁最终 Wave 5（Agent 6: Host-QA-Deploy）**
