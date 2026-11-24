namespace BotAgent.Domain.Reply;

/// <summary>
/// 模型在聊天回复 JSON 中声明的一次内联只读插件工具调用（纯值形状，不构成授权）。
/// 参数值在此阶段已归一化为有界字符串，对齐 <c>ToolArguments.Validate</c> 的字符串契约。
/// </summary>
public sealed record InlineToolCall(
    string ToolId,
    IReadOnlyDictionary<string, string> Arguments)
{
    public override string ToString() => $"InlineToolCall({ToolId}, args={Arguments?.Count ?? 0})";
}
