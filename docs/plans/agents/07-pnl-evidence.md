# Agent-07：PNL 首版用户验收矩阵 · Web 侧证据登记

对应 [z-bot-panel-v0.1-beta-plan.md 四.1](../z-bot-panel-v0.1-beta-plan.md)。本表只登记 **Web 行为证据**（07 负责）；ZBA 契约证据由 06 提交，00 核对。登记 ≠ 门禁通过，签署见计划三.3。

## 登记批次

| 项 | 值 |
| --- | --- |
| 源版本 | `7a6628a`（分支 `fix/web-review-20261027`）+ 未提交工作区（12 文件，布局/美化轮次） |
| 时间 | 2026-10-27 18:38–18:41 (+08:00) |
| 构建 | `npm run build`（vue-tsc + vite）exit 0 |
| 单元/组件/契约 | `npm run test:unit`：17 文件 251 通过 |
| E2E | `npx playwright test --project=chromium`：76 通过（真实 Headless 宿主，合成数据） |
| 本批未跑 | firefox / webkit / chromium-rate-limit 三个 project（由 CI 分 job 跑，本批未登记结果） |
| 可选 SSE / MCP | MCP 管理页：未纳入本次 Beta 页面。事件恢复走 events 轮询订阅（tasks.spec.ts:73），不依赖 SSE |

路径均相对 `src/web/`。E = `tests/e2e/`，C = `tests/component/`，U = `tests/unit/`。

## 矩阵

| 编号 | Web 侧测试位置 | 本批结果 | 未覆盖分支 / 依赖他方 |
| --- | --- | --- | --- |
| PNL-01 | E overview.spec.ts:11,36,46,180,196,246,260；C overview.test.ts:45,54,59,79,85；C status-badge.test.ts 全部 | 通过 | 无 |
| PNL-02 | E capabilities.spec.ts:15,47；C capabilities-page.test.ts:78,95,167,196 | 通过 | "不支持动作被后端拒绝"属 ZBA 契约证据（06），Web 侧只证明标准模式不提交 |
| PNL-03 | E configuration.spec.ts:128；E restart.spec.ts:139（强杀并重启宿主）；C configuration-page.test.ts:160,209 | 通过（保存后回读；重启后版本与字段一致，chromium/firefox/webkit） | 生效状态独立显示仅组件级断言 |
| PNL-04 | E configuration.spec.ts:154,372；C configuration-page.test.ts:193,820；U configuration-draft.test.ts | 通过 | 字段级校验错误定位依赖后端错误码，本批无字段错误 E2E |
| PNL-05 | E configuration.spec.ts:186,325,506,553,573；C configuration-page.test.ts:248,282,311,354,487,525,615,1077,1086,1094 | 通过（响应丢失/未知/需重启/应用失败） | 需重启、应用失败仅组件级断言（真实宿主难以合成应用失败）；部分生效（applyState=2）为组件级断言 |
| PNL-06 | E configuration.spec.ts:223,245,270,295,487；E capabilities.spec.ts:64；E interaction.spec.ts:152,196；C configuration-page.test.ts:393,432,450,473,551 | 通过 | 无 |
| PNL-07 | E tasks.spec.ts:24,40,115；E interaction.spec.ts:35,62,91；C tasks-page.test.ts:96,135,188,206,242,382 | 通过 | "审批绑定本次参数"服务端校验属 06 |
| PNL-08 | E interaction.spec.ts:109；C tasks-page.test.ts:176,348 | 通过 | 远端长时间不响应取消仅以"结果未知"覆盖，无超时 E2E |
| PNL-09 | E tasks.spec.ts:73,104,141,180；C tasks-page.test.ts:323,511,528 | 通过 | 重复/遗漏事件去重以 resyncRequired 整份补读覆盖；"恢复不重做工具动作"属 06 |
| PNL-10 | E overview.spec.ts:59,167；E configuration.spec.ts:82,487；E zz-rate-limit.spec.ts:17（本批未跑，不计入本结果）；C tasks-page.test.ts:87；C configuration-page.test.ts:1121 | 通过（不含 zz-rate-limit） | 后端拒绝越权读写属 06；QQ 官方 Secret 无写入口为组件级断言 |
| PNL-11 | E configuration.spec.ts:417；E interaction.spec.ts:168；U configuration-draft.test.ts:24；C configuration-page.test.ts:654–1024 | 通过 | 原始载荷"默认不暴露"无专门断言 |
| PNL-12 | E keyboard.spec.ts 全部；E responsive.spec.ts（390/360）全部；E color-independence.spec.ts:25,48 | 通过（chromium/firefox/webkit） | "不只靠颜色"自动断言只覆盖默认可达状态（四页带 data-tone 的徽标/提示均有文字；认证错误带文字、错误码与 role=alert），罕见错误态仍建议人工抽查 |
| PNL-13 | 独立构建 `npm run build`；E overview.spec.ts:218,129；C overview.test.ts:65 | 部分 | 本地回滚产物复现、本地资源测量、线上回滚演练已补（见文末各节）；**后端兼容版本记录与发布签署**未做——属三.3 发布条件，由项目组完成 |

## 结论

- 13 项均有 Web 侧自动化证据；PNL-13 只覆盖"不兼容拒绝 + 独立构建"，发布与回滚部分为空。
- Web → Beta 候选：工作区改动已提交为 `dd487ca`，并按该提交重建与重跑（见第 14 轮补充）；跨浏览器与原加粗未覆盖分支已在第 8–10 轮补齐。


补充（第 8 轮，2026-10-27）：`npx playwright test --project=firefox --project=webkit` 152/152 通过（6.1m，exit 0）；组件测试新增 configuration-page.test.ts:1077,1086,1094（PNL-05 需重启/失败、PNL-10 秘密边界），firefox + webkit 与 chromium 结果一致。


补充（第 9 轮，2026-10-27）：新增 E2E `tests/e2e/restart.spec.ts`（自起独立宿主 + preview，强杀后同一数据目录重启，刷新并重新认证后读回已保存版本，且不发写请求）三引擎 3/3 通过；组件测试新增 applyState=2 部分生效用例；`npm run typecheck` exit 0，单元/组件 255/255。
补充（第 10 轮，2026-10-27）：新增 E color-independence.spec.ts（PNL-12 颜色之外的文字信号），三引擎 6/6 通过（19.8s），typecheck exit 0。

补充（第 11 轮，2026-10-27）：AGENTS.md 要求的面板探针 `node tests/BotAgent.FrontendProbe/probe.mjs`（仓库内实际路径，非 `qqchat-src/` 前缀）通过 341，失败 0，exit 0。

## PNL-13 前端侧兼容版本记录（第 12 轮，2026-10-27）

只记录 Web 侧能自证的部分；回滚演练、资源测量与发布签署仍待项目组授权，本节不代表门禁通过。

| 项 | 值 |
| --- | --- |
| 源提交 | `dd487ca`（fix/web-review-20261027，含此前全部工作区改动；提交后重新构建，哈希与提交前一致） |
| 前端期望 ZBA 契约 | `zba-1.0`（src/web/src/api/compat.ts:3 `EXPECTED_CONTRACT`），不一致时禁用管理员写入口 |
| 配置契约修订 | `cfg-0.3.3` |
| 构建命令 | `npm run build`（vue-tsc --noEmit && vite build），无需重编 Headless |
| dist/index.html | sha256 `f9282bf41ad8712601caf25e241944ec83bd700306df2dc8643a15c777e6efde` |
| dist/assets/index-BPjXeJ9L.css | sha256 `752f1ad034bff516bef87e1bc1dbdc7d00705d340e030c46655e6fa3302cf612` |
| dist/assets/index-BsZXqR5o.js | sha256 `efb6ab2b5246ee1d82a0d4c0033817a347b6d4c4d39d44f60059b42e91009622` |

提交后需按新提交号重新构建并更新本表；产物哈希只对应上述源状态。

补充（第 13 轮，2026-10-27）：工作区改动已提交为 `dd487ca`。按该提交重新 `npm run build`，dist 三个产物 sha256 与上表一致；单元/组件 255/255；`npx playwright test --project=chromium --project=firefox --project=webkit` 237/237 通过（8.8m，exit 0）；面板探针 341 通过 0 失败。


## PNL-13 本地回滚产物复现与体积测量（第 14 轮，2026-10-27）

只在本机做，未部署、未改动任何线上环境；不代表发布签署。

- 回滚产物：在同盘 git worktree 检出上一提交 `7a6628a`，`npm ci` + `npm run build` 均 exit 0，说明回退到旧前端只需重建旧提交，无需重编 Headless。
- 体积对比（raw / gzip 字节）：

| 产物 | 7a6628a（回滚目标） | dd487ca（当前） | 差值 |
| --- | --- | --- | --- |
| index.html | 625 / 382 | 625 / 379 | 0 / -3 |
| CSS | 38540 / 8342 | 43015 / 9052 | +4475 / +710 |
| JS | 245307 / 81392 | 250534 / 83689 | +5227 / +2297 |

- 结论：本次重构 gzip 后总体积增加约 3.0 KB（约 +3.4%），无新增分块或依赖。
- 仍需项目组：线上回滚演练、运行时资源测量（内存/CPU）与发布签署。


## PNL-13 本地运行时资源测量（第 15 轮，2026-10-27）

环境：本机 Chromium（Playwright），`vite preview` 提供在 `de9e1e1` 检出构建的产物（`de9e1e1` 只改文档，前端源码与产物同 `dd487ca`），合成 Headless 宿主与合成令牌；不是线上数据，不代表发布门禁通过。临时测量用例跑完即删，未入库。

| 指标 | 值 |
| --- | --- |
| 首屏 DOMContentLoaded / load | 215 ms / 216 ms |
| 页面切换平均耗时（含可见断言） | 53 ms |
| JS 堆 已用 / 总计 | 4.16 MB / 4.82 MB |
| DOM 节点 / 事件监听器 | 2523 / 94 |
| 累计脚本 / 布局耗时 | 22 ms / 242 ms |

线上回滚演练与发布签署仍待项目组授权。


## PNL-13 线上回滚演练（2026-10-27 19:20，号主授权“执行”）

前端由生产机上 Caddy 从 `/var/www/botagent-web` 静态托管（`handle_path /web/*`），与 `qqchat-bot` 容器解耦；演练只换静态目录，不重启任何容器。

| 步骤 | 结果 |
| --- | --- |
| 演练前线上产物 | `index-Cv2WPE2g.css` / `index-DyiI_HLR.js`，sha256 前缀 dc9af666 / af04d949，与本地复现的 `7a6628a` 构建一致 |
| 备份 | `/var/www/botagent-web.pre-drill-20261027`（cp -a） |
| 部署 `dd487ca` | 解压到 stage 后 mv 原子替换；线上三文件 sha256 与本表 dd487ca 行一致；`/web/` 200，`/readyz` 200 |
| 回退 | mv 换回演练前目录；三文件 sha256 回到 7a6628a 值；`diff -r` 与备份完全一致；`/web/` 200，`/readyz` 200 |
| 部署→回退耗时 | 1768 ms（含两次 ssh 往返） |
| 容器影响 | `qqchat-bot`、`qqchat-napcat` 容器 ID 与启动时间演练前后不变 |
| 残留 | `botagent-web.drill-dd487ca`（可用于前滚）、`botagent-web.pre-drill-20261027`（备份） |

结论：前端可独立发布、独立回退，回退只操作本次交付产物，不需重编或重启后端。演练结束时线上停在回退后的 `7a6628a` 产物。发布签署由项目组发布负责人完成。

> 更正（2026-10-27 21:00）：本次演练只验了 `/web/` 返回 200，**没验 index.html 引用的 JS/CSS 能否加载**。07dff41 发布时构建漏了 `--base=/web/`，资源请求 `/assets/*` 被反代到后端，面板白屏，已回退后重发。修复：`base: '/web/'` 写进 `src/web/vite.config.ts`；新增 e2e `tests/e2e/base-path.spec.ts`（资源都在 /web/ 下且可加载）；发布后必跑 `tools/verify-web-deploy.sh`（逐个核对引用文件存在，并经 Caddy 带口令请求每个资源的状态码与类型）。
