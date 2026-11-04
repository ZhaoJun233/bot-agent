namespace BotAgent.Domain.Reply;

/// <summary>按「读到的气氛」调发言门槛（纯函数、零 IO）：批次 1 从 BotAgentHost 原样搬来。</summary>
public static class VibeRules
{
    /// <summary>

    /// 按“读到的气氛”调门槛。返回本轮真正生效的阈值，reason 给日志用。

    /// 为什么在代码里再调一道（而不是只写在提示词里）：模型自评本来就依赖它的判断，

    /// 但它说“群里在吵架”时还想插一句的情况真出现过 —— 这时候代码得拦一下。

    /// </summary>

    public static int VibeAdjustedThreshold(string vibe, int baseThreshold, out string reason)

    {

        switch (vibe)

        {

            case "吵架":

                reason = "气氛在对线，不插嘴（除非非说不可）";

                return Math.Max(baseThreshold, 60);

            case "低落":

            case "求助":

                reason = "有人情绪不好/在求助，轻轻接一句比沉默好";

                return Math.Max(0, baseThreshold - 10);

            case "生气":

                reason = "有人在气头上，说话得稳一点";

                return baseThreshold;

            default:

                reason = string.Empty;

                return baseThreshold;

        }

    }
}
