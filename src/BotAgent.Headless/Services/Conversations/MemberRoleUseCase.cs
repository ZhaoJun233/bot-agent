using BotAgent.Domain.Ports;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;
using System.Collections.Concurrent;

namespace BotAgent.Services.Conversations;

/// <summary>
/// 群成员身份（群主 / 管理员 / 群头衔）的用例层：消息事件自带 role；**头衔只有专门的动作才有**，
/// 所以缺的时候要后台去问协议端一次 —— 但不能每条消息都问，那会把协议端打爆（问过 3 天内不再问，
/// 见 <see cref="MemberRoleStore.NeedsRefresh" />）。
///
/// 两条纪律：
///   ① 同一人同一群**只排一次**查询（群里连发会疯狂触发）；
///   ② 回复前会**等一下**正在跑的那些查询（<see cref="WaitForPendingAsync" />，最多等 timeout）——
///      查询通常十几毫秒，等一下就能让"第一次说话那轮"也带着头衔，而不必等下一句；
///      等不到也绝不能把回复挂住。
/// </summary>
public sealed class MemberRoleUseCase
{
    private readonly IQqChatSource _source;
    private readonly IMemberRoleRepository _store;
    private readonly Action<string> _log;

    /// <summary>
    /// 正在等人去问协议端身份的 (群, 人) → 那个查询任务。
    /// 存 Task 而不是个标记：回复前会等它们一下（见 <see cref="WaitForPendingAsync" />）。
    /// </summary>
    private readonly ConcurrentDictionary<(long Group, long UserId), Task> _pending = new();

    public MemberRoleUseCase(IQqChatSource source, IMemberRoleRepository store, Action<string> log)
    {
        _source = source;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// 记下一个群成员的身份（消息事件自带 role；自带头衔的协议端就连头衔一起收下），
    /// 需要补头衔时再后台去问协议端。
    /// </summary>
    public void Remember(QqChatMessage msg)
    {
        var uid = msg.UserId.ToString();
        // 事件里带 role（一定有）、可能带 title（看协议端）；titleChecked 只在真拿到 title 时才算 true
        _store.Remember(uid, msg.GroupId, msg.SenderRole, msg.SenderTitle, msg.SenderName,
            titleChecked: !string.IsNullOrWhiteSpace(msg.SenderTitle));

        // 消息事件里没有头衔（OneBot 只保证有 role）—— 缺的话去问一次；问过就把
        // updated_unix 推后，下次 3 天内不再问（见 MemberRoleStore.FreshFor）。
        if (!string.IsNullOrWhiteSpace(msg.SenderTitle))
        {
            return;
        }

        if (!_store.NeedsRefresh(uid, msg.GroupId))
        {
            return;
        }

        // 同一人同一群只排一次（群里连发会疯狂触发）
        if (!_pending.TryAdd((msg.GroupId, msg.UserId), Task.CompletedTask))
        {
            return;
        }

        var groupId = msg.GroupId;
        var userId = msg.UserId;
        var name = msg.SenderName;
        var lookup = Task.Run(async () =>
        {
            try
            {
                var info = await _source.GetGroupMemberInfoAsync(groupId, userId, CancellationToken.None);
                if (info is not null)
                {
                    _store.Remember(uid, groupId, info.Role, info.Title, info.DisplayName, titleChecked: true);
                    if (!string.IsNullOrWhiteSpace(info.Title) || info.Role is "owner" or "admin")
                    {
                        _log($"[Role] 群 {groupId} 成员 {info.DisplayName}({userId})：" +
                             $"{info.Role switch { "owner" => "群主", "admin" => "管理员", _ => "成员" }}" +
                             (string.IsNullOrWhiteSpace(info.Title) ? string.Empty : $"，头衔「{info.Title}」"));
                    }
                }
                else
                {
                    // 协议端不支持/没这个人：至少把“问过了”记下来，否则下次发言又要问一遍
                    _store.Remember(uid, groupId, msg.SenderRole, null, name, titleChecked: true);
                }
            }
            catch (Exception ex)
            {
                _log($"[Role] 查群成员身份失败（不影响聊天）：{ex.Message}");
            }
            finally
            {
                _pending.TryRemove((groupId, userId), out _);
            }
        });

        // 把真任务放进去（TryAdd 时先占位，避免同一人连发时排队问多次）
        _pending[(groupId, userId)] = lookup;
    }

    /// <summary>等本群待补身份查完（最多等 timeout，超时就算了，别拖慢回复）。</summary>
    public async Task WaitForPendingAsync(long groupId, TimeSpan timeout)
    {
        var pending = _pending
            .Where(kv => kv.Key.Group == groupId && !kv.Value.IsCompleted)
            .Select(kv => kv.Value)
            .ToArray();
        if (pending.Length == 0)
        {
            return;
        }

        await Task.WhenAny(Task.WhenAll(pending), Clock.Delay(timeout));
    }

    /// <summary>取给提示词用的身份清单（≤ limit 人，只给"值得说的人"）。</summary>
    public string? DescribeForPrompt(long groupId, int limit, out int count)
        => _store.DescribeForPrompt(groupId, limit, out count);
}
