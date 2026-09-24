namespace BotAgent.Services.Qq;

/// <summary>
/// 登录的 QQ 号（协议端上报优先，其次配置；0 = 还没拿到）。
///
/// 为什么单拎成一个小对象：它被**很多组件**读（提示词、戳一戳的门、agent 动作的 sender/this、
/// 会话里"这条是不是我自己发的"），以前是 BotAgentHost 的私有字段 —— 于是每个要用它的组件都只能
/// 通过回调间接拿到宿主。现在是共享状态，谁都能读，装配点按依赖顺序传。
/// </summary>
public sealed class BotIdentity
{
    private long _selfId;

    public long SelfId => Interlocked.Read(ref _selfId);

    public void Set(long id) => Interlocked.Exchange(ref _selfId, id);
}
