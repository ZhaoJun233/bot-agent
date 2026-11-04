# 管线性能基线（确定性决策链）

这一页说明 `tests/BotAgent.PipelineEval` 里那条「管线性能基线」的口径、阈值怎么来的、以及
**什么时候可以上调基线**。守护值本身在 [`tests/fixtures/pipeline/baseline.json`](../../tests/fixtures/pipeline/baseline.json)。

## 1. 它量的是什么（以及不量什么）

| 量 | 不量 |
| --- | --- |
| 模型回复**已经拿到之后**的确定性决策链：解析 → 判定 → 组装（68 个合成样例全量） | 真实模型调用的往返延迟 |
| 同一进程内的纯 CPU 工作（无网络、无磁盘、无子进程） | 网络、磁盘、SQLite、协议端 |
| **相对基线**的退化（同比倍数），不是绝对性能承诺 | 任何吞吐量 / QPS 承诺 |

所以这条基线**不会**因为模型变慢、网关抖动、群里有大消息而变红；它只在**代码把决策链算慢了**时变红。
这与 `docs/baseline/report-2026-10-31.md` §6 第 6 条（真模型未评测）是两件事，不要混为一谈。

## 2. 一轮为什么是「200 遍全量样例」

68 个样例跑一遍只有 ~0.5ms，**低于计时器分辨率与 GC 噪声**，取百分位毫无意义。
所以一轮 = 把三个夹具（`on-demand.json` / `research.json` / `social.json`）的全部样例
**按原顺序连续扫 `sweepsPerRound` 遍**（默认 200 遍 ≈ 70ms）。这样单轮耗时落进毫秒级，
百分位才有意义，而噪声仍能被 15 轮采样摊掉。

## 3. 采样与判定

- **预热 2 轮**（`warmupRounds`）丢弃，只计后 **15 轮**（`measuredRounds`）。
- 百分位用 **nearest-rank**（不是插值），样本少时更保守。
- 阈值：`上限 = max(基线 * maxMultiplier, 基线 + 15ms)`，`maxMultiplier` 默认 **3.0**。
  - 倍数那一项管「真退化」（比如把正则换成回溯爆炸的实现）。
  - `+15ms` 那一项只吸收 CI 抖动：机器慢一档时基线 80ms 上下浮动十几毫秒不该变红。
- 判定只看 **P50 与 P95 是否都 ≤ 各自上限**；两者任一超限即失败。

退出码：

| 退出码 | 含义 |
| --- | --- |
| `0` | 样例全过 + 性能基线通过 |
| `1` | 样例失败，或性能超出上限（`maxMultiplier` / `+15ms` 都兜不住） |
| `2` | **失败关闭**：基线文件缺失、读不通、字段缺失或非法值（`FailClosed`） |

`2` 是刻意设计的：**基线丢了不等于没基线**，等于「没有守护值」，必须红。

## 4. 怎么改基线（纪律）

程序**不会**自动写基线（没有 `--write-baseline`，刻意不加 —— 自动写等于自己给自己发通行证）。

要改的时候：

1. 在同一台机器上连跑三次，记录 P50 / P95（输出末尾那行会打印）。
2. 判断变慢是**真的**还是**机器慢**：
   - 三次都稳定地比旧基线慢 2 倍以上 → 真退化，**先查代码**，不要改基线。
   - 三次都只慢 20% 以内且绝对值只差几毫秒 → 可能是环境漂移。
3. 只有在**确认是环境漂移**（换机器 / 换 .NET 小版本 / CI 机型变化）时才上调，并同时更新
   `recordedAt` 与 `environment` 两个字段 —— 它们是给下一个人判断「这条基线还作不作数」用的。
4. 改动随 PR 提交，**在 PR 描述里写清三次实测值**。

> 反面教材：为了让 CI 过而把 `p50Ms` 从 80 调到 300，等于把守护值调成摆设。
> 这条基线存在的意义就是「不许悄悄变慢」，调大它就等于取消它。

## 5. 负向验证（这条基线确实会红）

实测（2026-11-01，本机）：

| 注入 | P50 | P95 | 结果 |
| --- | --- | --- | --- |
| 无（正常） | 70.5ms | 84.4ms | rc=0（上限 240 / 390） |
| `ModelOutputParser.Parse` 首行 `Thread.SpinWait(1400)` | 247.8ms | 420.9ms | **rc=1**（两项都超） |
| 同上改成 `SpinWait(20000)` | 2829.8ms | — | **rc=1**（大幅超） |

即：**约 3.5 倍慢就会红**，与 `maxMultiplier=3.0` 的设计一致。

## 6. 相关位置

| 东西 | 位置 |
| --- | --- |
| 守护值 | [`tests/fixtures/pipeline/baseline.json`](../../tests/fixtures/pipeline/baseline.json) |
| 测量与判定 | `tests/BotAgent.PipelineEval/Program.cs`（`MeasurePipelinePerformance` / `RunRound` / `Percentile`） |
| 样例夹具 | `tests/fixtures/pipeline/{on-demand,research,social}.json` |
| CI 里的执行点 | `.github/workflows/ci.yml` 的 `verify` job「Deterministic pipeline evaluation」 |
| 探针守卫（基线字段必须齐 / 必须在 CI 里跑） | `tests/BotAgent.ArchitectureProbe/Program.cs` 的 `CoreInvariantAndBaselineChecks` |
| 核心不变量场景 | `tests/BotAgent.IntegrationHarness/InvariantScenario.cs`（S56–S59） |
