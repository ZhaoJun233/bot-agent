# 包内数据事务

`IPackageDataStore` 把一次同步 repository 操作交给现有数据库协调器。当前生产使用方是配额账本；P08 尚未迁入独立包，实际包清单授权及激活代次绑定仍待完成。安全回执查询已独立于业务后端，见[操作记录恢复](../operations/operation-receipt-recovery.md)。

## 接口与资源

Core 的 `IPackageDataStore.Read/Write` 接收一次 `IPackageDataSession` 回调。会话提供参数化 `Scalar/Query/Execute`、命名空间状态读写、通用用量投影、审计追加和 `RecordReceipt` 安全证据写入；结果立即物化，不返回数据库连接、事务或活动 reader。

Storage 的 `PackageDataStore` 只由可信组合代码创建，构造时复制表集合、状态键前缀、用量和审计授权。普通调用者不能在回调里扩权。现有配额绑定为：

- 表：`tenant_quotas`。
- 状态：`quota:revision:`、`quota:receipts:` 后的既有 SHA256 键。
- 用量：同事务读取通用 `token_usage` 投影；UTC 跨日可能持久化重置，所以必须在写事务中调用。不能用包 SQL 直接修改计量表。
- 审计：同事务追加既有审计链，不能用包 SQL 读取或修改审计表。
- 安全回执：可信构造绑定 owner、契约/版本与实现代次，调用只提交原调用范围、原键、请求摘要和状态。通用索引不能通过状态前缀接口覆盖，也不保存业务结果。当前配额使用 Storage 模块 MVID；实际包采用时须绑定批准的激活代次。

运行回调只组织数据库工作，不执行网络调用或插件生命周期操作。建表和迁移仍由受控冷迁移路径管理，运行回调没有 DDL 能力。

## 提交与拒绝

写入复用 `AppDatabase` 的全局写协调与 SQLite 事务。额度、修订、原键回执和审计在同一连接提交，任何一步失败整笔回滚。配额仍先检查原幂等键再比较修订；原键重放不会重新审计。

会话只能在创建线程及回调期间使用。跨线程、嵌套操作、提前释放、返回 Task/ValueTask 或逃逸后调用都会被拒绝。会话操作失败后，即使回调捕获了异常，也不能继续提交前面的部分写入。

每条包 SQL 经过 SQLite 原生 authorizer：只允许明确授权的 main 表、DML 和固定函数集合；拒绝其他表、meta 直读、schema 查询、DDL、PRAGMA、ATTACH、事务控制、扩展加载、未授权函数及触发器/视图间接访问。读事务对所有 SQL 入口都禁止写入，包括带 RETURNING 的 Scalar 调用。

会话使用与旧裸连接路径分开的连接池。SQLite 对 COUNT(*) 的授权回调可能省略数据库名；该放行依赖包池不能创建临时表或附加库的不变量。既有可信裸连接兼容回调使用前会清除当前池，连接不再回池；兼容回调之后也不接受省略数据库名的表访问。

这些约束隔离通过本接口获得的资源。进程内托管 DLL 仍与宿主同权限，不能把该接口当作任意恶意代码的安全沙箱。托管包的声明审批、资源授权和调用代次绑定见 [包资源授权](plugin-resource-authorization.md)；内置 P08 的独立包迁移仍未完成。

## 兼容与证据

宿主旧 `TenantQuotaStore` 实现已退役；ZBA 通过配额包控制贡献使用返回 `AuditEvent` 的审计回调，不接收裸连接。历史裸连接回调与同事务用量场景仅由测试适配器保留验证，不是包能力。`AuditEvent/AuditVerification/IAuditChain` 的名称与签名不变，定义在独立 Plugin.Contracts，Core/Engine 保留类型转发；审计实现位于 Storage，Headless 保留兼容类型。

原业务回执 JSON、键与 schema 不变；新增安全索引在 `operation-recovery:v1:` 元数据命名空间，与业务回执和审计同事务提交。旧记录只读投影，不回填或改写。schema 15 的用量恢复过程见 [用量恢复](../operations/token-usage-recovery.md)。

`ProductionSpecProbe --package-transactions` 用独立合成库验证 SQL 越权、池污染、会话边界、拒绝后的整体回滚及实际配额的原键重放/审计失败。完整生产探针在独立子进程运行该组，避免与故意破坏审计链的既有测试共享状态。S51 用实际宿主验证配额管理、重启回执和插件停用后的持续计量。
