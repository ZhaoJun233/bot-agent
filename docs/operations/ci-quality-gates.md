# CI 质量门禁与不稳定性治理（#113）

这份文件回答两个问题：**合并一个 PR 必须过哪些检查**、**检查红了/跳过/偶发时该怎么办**。
门禁清单必须与 `.github/workflows/ci.yml` 的实际 job 名逐字一致 —— 分支保护的必需检查是按名字匹配的，
改工作流里的 `name:` 而不改保护规则，等于让必需检查永远缺失、PR 永远合不了。

> 配套：#127 负责把这里列出的清单配置到分支保护（`dev` / `main`，`strict: true`）；
> 本文件负责清单正确与过程纪律。分支保护的当前必需检查已与本文件 §1 的 9 个项目一致。

---

## 1. 必需检查（9 项，名字不可改）

| 工作流 job | 必需检查名（分支保护按它匹配） | 覆盖内容 | 超时 |
| --- | --- | --- | --- |
| `verify` | `Build and synthetic probes` | `dotnet restore BotAgent.slnx`（NuGet 高危审计作为错误）→ `dotnet build` → **21 个 C# 探针 + 1 个 python 探针** + `PipelineEval` 性能基线 | 20 min |
| `web` | `Web build and browser tests` | `src/web` 的 typecheck、unit（39 文件 483 例）、生产构建 | 20 min |
| `e2e` ×4 | `Browser end-to-end (chromium)` / `(firefox)` / `(webkit)` / `(chromium-rate-limit)` | 真 Headless 宿主 + Playwright，同一套用例四矩阵 | 25 min |
| `integration` ×3 | `Synthetic integration test (Core & Chat (S1-S17))` / `(Media & Tools (S18-S31))` / `(Governance & Multi-Platform (S32-S55))` | **60 个集成场景**分三组跑（`BOTAGENT_IT_ONLY` 传矩阵） | 20 min/组 |

矩阵里的场景 id 与 `tests/BotAgent.IntegrationHarness/Program.cs` 的注册表必须**双向完全一致**，
ArchitectureProbe 有断言兜底（`harness 注册的 N 个集成场景与 ci.yml 矩阵完全一致`，负向实测会红）。
新增场景只改 `Program.cs` 不改矩阵 = 这个场景永远不进 CI，断言会先拦下来。

> 显示名里的 `S1-S17` / `S18-S31` / `S32-S55` 是历史范围，**不代表实际场景**：
> s56–s62 故意留在 Governance 组。别按名字理解，以矩阵的 `scenarios:` 为准。

---

## 2. 规则

1. **9 项全绿才可合并**；`strict: true` 要求分支先 rebase 到最新再跑。
2. **失败即红，不允许无说明跳过**。禁止用 `continue-on-error: true`、`if: false`、长期注释掉步骤让 CI 表面变绿；
   临时禁用必须在 PR 描述与本文档登记，并建跟踪 issue。
3. `integration: needs: verify` 是**有意**的依赖：verify 红了集成组显示 `skipped`（不是通过）。
   看到 skipped 时先去修 verify，不要把 skipped 当绿灯。
4. 矩阵一律 `fail-fast: false`：一组失败不掩盖其余组的结果。
5. 重跑失败 job 允许，但必须在 PR/issue 里写「为什么判定为偶发、跑了哪些次、结论」；禁止反复重跑把红刷成绿。
6. NuGet 审计口径：`Directory.Build.props` 的 `NuGetAudit*` + `WarningsAsErrors` 里的 `NU1903;NU1904`；
   唯一 restore 入口是 `dotnet restore BotAgent.slnx`，不要把审计降级成 advisory。

---

## 3. 不稳定性登记表

同一用例 30 天内两次红必须**修掉或建 issue**，不允许「反正重跑就过」。当前已知项：

| 现象 | 定性 / 根因 | 状态 |
| --- | --- | --- |
| Linux 上 B24「caller cancellation cleans parent and descendant process」偶发红 | 取消是异步传播，探针在 `Cancel()` 后立即断言子进程已退出 | **已修**（`2b467e4`：改成 10 秒有界轮询等待） |
| 集成 S12 偶发 `HTTP -1` / 面板拒连 | harness 写死 `18085` 被别的进程占成临时端口，`HttpListener` 绑不上（面板静默不启动） | **已修**（`dfc96a4`：`FreePort` + 单独等面板端口） |
| 集成 S26 假模型服务端口被占导致整轮 harness 崩溃 | 同类写死端口问题 | **已修**（#104 S2 期间改端口探测） |
| firefox e2e「键盘：配置保存主线全程只用键盘完成」`Shift+Tab 200 步仍未到达 config-field-privateCooldownSeconds` | `tests/e2e/keyboard.spec.ts:283` 的 Tab 顺序竞态，尚未根因 | 偶发；重跑可过。30 天内再红一次就要修或建 issue |
| 前端单测 `tests/unit/zba-timeout.test.ts` 偶发超时 | 与真实计时器竞争有关，尚未根因 | 偶发；单跑与复跑均通过。同上规则 |

新增登记项时写清：哪条检查、哪条用例、最近一次 run 链接、重跑结果、跟踪 issue。

---

## 4. 改 CI 的步骤

1. 改 `.github/workflows/ci.yml`。
2. 若改了 job 的 `name:`：**必须**同时改 `dev` 与 `main` 的分支保护必需检查（`gh api repos/<owner>/<repo>/branches/<branch>/protection`），
   否则该检查永远缺失。改完在本文档 §1 表格同步。
3. 若新增/删除集成场景：同步矩阵，跑一次 `ArchitectureProbe`（它会断言双向一致）。
4. 若新增探针工程：加进 verify 的某个 step，`ArchitectureProbe` 的「tests/ 下每个探针工程都要出现在 ci.yml」会兜底。
5. 若调整超时：写清实测耗时依据（本机 `build-all` 74 s、集成 62 场景单机约 10–15 min 量级），并确认仍在预算内。

---

## 5. 相关

- `.github/workflows/ci.yml` —— 唯一真源。
- `tests/BotAgent.ArchitectureProbe/Program.cs` —— 门禁自检（探针在 CI 出现 / 场景矩阵双向一致 / PipelineEval 被执行）。
- `CONTRIBUTING.md` §本地验证 —— 提交前该跑什么。
- `docs/baseline/report-2026-10-31.md` —— 验收基线数字。
- #127（分支保护）、#113（本文件）、#111 / #112（回归测试体系）。
