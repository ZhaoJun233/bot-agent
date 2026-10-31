## 变更摘要

<!-- 一两句话说清"改了什么、为什么"。关联 issue 用 #123 这种写法。 -->

关联 issue：

## 验证命令与结果

<!-- 贴命令 + 实际输出摘要，不要只写"已验证"。 -->

| 命令 | 结果 |
| --- | --- |
| `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release` | |
| `dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release` | |
| `(cd src/web && npm run typecheck && npm run test:unit)` | |

## 重构前后 harness 场景对比

<!--
  **改动了 src/ 里任何一行，这一段都必须填。**
  证据形态：同一组集成场景在重构前 / 重构后的通过数与差异项。
  只改文档、只改 CI、只改测试本身时可以写"不涉及 src/ 改动"。
-->

| 场景 ID | 重构前 | 重构后 | 差异说明 |
| --- | --- | --- | --- |
| | | | |

**断言改动声明**：<!-- 本次是否修改了任何探针/场景里的断言？改了哪一条、为什么。重构 PR 内修改断言属于红线行为，必须单独说明。 -->

## 回滚点

<!-- 出问题怎么退：revert 哪个提交、数据库要不要动、容器怎么回。 -->

- 回滚提交：
- 数据影响：<!-- 无 schema 变更 / 有迁移，见 docs/operations/rollback-playbook.md -->
- 停写前置条件：
