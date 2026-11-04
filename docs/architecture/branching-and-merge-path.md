# 分支模型与合并路径

本文件规定**代码从开发到公开仓库的唯一路径**。规则来自三处既有约定：`CONTRIBUTING.md`（分支与评审）、
工作区 `AGENTS.md`（发布纪律）、公开快照发布脚本的白名单。这里把它们合成一份可引用的规范，**不新增流程**。

---

## 1. 分支角色

| 分支 | 位置 | 角色 | 谁能写 |
| --- | --- | --- | --- |
| `main` | 公开仓库 | 对外稳定线 | 只能由 `dev` 经评审 PR 合入 |
| `dev` | 公开仓库 | 集成分支，对外发布的落点 | 发布脚本快进推送 / 评审 PR |
| `Zhaodev` | 公开仓库 | 本轮治理的**集成验证分支** | 发布脚本快进推送；阶段验收在此跑 |
| `fix/*` `feat/*` `docs/*` `chore/*` `refactor/*` | 任意 | 短期工作分支 | 开发者 |
| 私有工作区分支 | 私有远端 | 真实开发发生地（含未脱敏数据与内部文档） | 号主 |

公开仓库里**不存在**未经脱敏的源码：所有进入 `dev` / `Zhaodev` / `main` 的内容都由发布脚本
从 `git archive HEAD` 生成快照、在独立临时目录完成双重审计后普通快进推送。因此**不要**把本地工作分支
直接 `git push` 到公开仓库的任何分支。

---

## 2. 三条线的关系（2026-10-31 实测）

```
私有工作区 fix/backend-review-20261030 (49a20b4)
        │  tree 与 Zhaodev 完全一致
        ▼
公开仓库 Zhaodev (9e45fe8)  ──4 个提交──▶  dev (155bcff) ──34 个提交──▶  main (7cc69da)
```

- `Zhaodev` 比 `dev` 多 4 个提交（`9e45fe8` / `ccb1053` / `ac2ef27` / `0331d7d`，共同基线 `155bcff`）。
- `dev` 比 `main` 多 34 个提交。
- 本机 HEAD 与 `upstream/Zhaodev` 的**目录树哈希相同**（`ab0e7a5…`），即当前内容等价。

---

## 3. 合并路径（唯一合法路径）

```
工作分支 ──PR──▶ Zhaodev ──阶段验收通过──▶ dev ──评审 PR──▶ main
```

### 3.1 工作分支 → Zhaodev

1. 从 `Zhaodev` 切短分支，命名遵循 `fix/*`、`feat/*`、`docs/*`、`chore/*`、`refactor/*`。
   本轮治理的推荐命名：`chore/upgrade-dotnet10`、`fix/model-configuration`、`fix/whitelist-management`、
   `refactor/settings-store`、`refactor/reply-pipeline`、`refactor/onebot-gateway`。
2. 一个 PR 只做一件事。描述里写清**行为、风险、测试、迁移、回滚**五项。
3. 提交标题写成能独立读懂的一句话（例：`fix(persistence): reuse settings read for presence check`），
   不把无关改动捆进同一个提交。
4. 必需检查（`verify` / `web` / `e2e` 四矩阵 / `integration` 三矩阵）必须全绿；**不得绕过、不得强推**。

### 3.2 Zhaodev → dev

- 触发条件：某个**阶段**（见 #100 的四阶段排期）的全部条目完成并通过 #105 的升级回归验收。
- 方式：一个独立评审 PR，正文列出该阶段完成的 issue 编号与验收证据链接。
- 合并后 `dev` 即成为下一阶段的起点。

### 3.3 dev → main

- 单独一个评审 PR，不得与功能改动混在一起。
- 必须等 `dev` 上全部必需检查绿；`main` 已开启分支保护（`enforce_admins`、禁强推、禁删除、线性历史）。
- 发布到 `main` 之后才谈得上"对外稳定版本"。

---

## 4. 硬性约束

| 约束 | 说明 |
| --- | --- |
| 禁止直接推送 | 不直接 `git push` 到公开仓库任何分支；一律经发布脚本 |
| 禁止强推 `main` | `main` 已开 `allow_force_pushes=false` |
| 禁止绕过必需检查 | 分支保护对管理员同样生效（`enforce_admins=true`） |
| 快照必须脱敏 | 黑名单 + 按类别 + 推后复检三道闸不可关闭、不可删 |
| 提交身份不算泄露 | 公开仓库提交沿用开发机默认身份，**不要**用 `--neutral-identity` 去"消毒" |
| 未提交内容不发布 | 发布基于 `git archive HEAD`，工作区未提交改动不会被带上 |
| 治理期冻结 | #105 验收窗口内冻结 `src/` 合入 `dev`；重构 PR 只进 feature 分支 |

---

## 5. 常见误解

- **"绿了就代表生产可用"**：不是。集成 harness 是合成场景，S42 用回环假网关，S54/S55 用合成 fixture。
  真实 Open Platform 凭据、裸机 Linux、Caddy/公网反代、真实浏览器 CSRF、真实 QQ/NapCat/飞书/TTS 与生产环境
  **都不在合成测试覆盖内**，对外表述时必须写清楚。
- **"本机跑通 = CI 跑通"**：不是。本机单进程跑 53 个场景，CI 分三个矩阵分片；结论要引用实际运行记录。
- **"Zhaodev 是私有分支"**：不是。它和 `dev`、`main` 一样在公开仓库，内容同样经过脱敏审计。
