# 官方身份离线迁移

仅处理映射和操作者已核验的身份索引，不读取聊天、人物档案或协议事件。工具能证明结构、摘要及 raw/alias 一致，不能证明输入索引的归属判断真实。不得按当前 AppID、昵称、消息正文或新事件给旧记录自动补标签；没有证据的记录不写入索引，继续作为 legacy 保留。

构建：

```sh
dotnet build tools/BotAgent.OfficialIdentityMigration/BotAgent.OfficialIdentityMigration.csproj -c Release
```

在隔离目录保存旧映射副本和已核验索引后运行。以下路径均为示例；输出必须不存在，父目录须已存在：

```sh
dotnet tools/BotAgent.OfficialIdentityMigration/bin/Release/net10.0/BotAgent.OfficialIdentityMigration.dll preview mapping.json verified-index.json
dotnet tools/BotAgent.OfficialIdentityMigration/bin/Release/net10.0/BotAgent.OfficialIdentityMigration.dll migrate mapping.json verified-index.json candidate.json
dotnet tools/BotAgent.OfficialIdentityMigration/bin/Release/net10.0/BotAgent.OfficialIdentityMigration.dll rollback candidate.json legacy-candidate.json
```

`preview` 不写文件。`migrate` 原子生成独立候选，保存源索引 SHA256，不修改输入。`rollback` 导出包含所有旧别名和新增别名的旧格式候选；同一个 raw 已属于不同应用、类型、群或方向而对应多个 alias 时，返回 `identity_rollback_conflict`，不生成不完整文件。不要用旧备份覆盖运行期间新增的身份。

索引版本为 1。下面是纯合成单聊记录的形状，`sourceSha256` 必须换成映射文件**原始字节**的 SHA256，`alias` 必须与该 raw 的旧映射完全相同：

```json
{
  "version": 1,
  "sourceSha256": "replace-with-64-lowercase-hex-characters",
  "bindings": [
    {
      "identity": {
        "scope": {
          "appId": "app-A",
          "kind": "C2cUser",
          "conversation": null,
          "direction": "None"
        },
        "nativeId": "synthetic-user"
      },
      "alias": 8000000000000017
    }
  ]
}
```

类型包括 `Group`、`GroupMember`、`C2cUser`、`BotSelf`、`Message`。群成员、机器人自身和消息都需会话（`GroupMember` 只能是群）；会话形状为 `{"platformId":"qq.official","accountScope":"app-A","kind":"GroupChat","nativeTargetId":"synthetic-group","threadId":null}`。C2C 会话用 `PrivateChat`。消息方向必须为 `Inbound` 或 `Outbound`，其它类型用 `None`。应用与会话的 accountScope 必须相同。工具拒绝未知字段、重复 JSON 属性、重复归属和不匹配别名。

输出只包含状态、计数、SHA256。退出 0 为候选生成 / 预览成功，2 为输入或身份冲突，3 为读写不可用。任何失败都保留输入与已经存在的输出。

正式切换仍需单独执行：停止官方新任务，排空或取消动作，停写，备份映射及相关元数据；重新核对源摘要，再核对候选计数及别名保持后替换。回退也必须停写，并从当前完整表导出；不能无损导出时保留完整类型表并进入只读恢复。CLI 不执行宿主切换、不操作生产服务、不将运行进程当作可共享的第二写者。
