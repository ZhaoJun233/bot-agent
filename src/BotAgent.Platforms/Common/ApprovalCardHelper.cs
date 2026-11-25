using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotAgent.Domain.Permissions;

namespace BotAgent.Platforms.Common;

/// <summary>
/// 服务端跨平台交互审批卡片构建器（严格仅允许 <see cref="ApprovalOutboundScope.IsSystemAnnouncement"/> 作用域下生成，
/// 任何模型聊天输出即使凑出相似文案也无法伪造交互审批卡片；回调绝不信任卡片负载中的角色声明，统一回服务端按 RBAC 核验）。
/// </summary>
public static partial class ApprovalCardHelper
{
    [GeneratedRegex(@"^【需要确认】.+?｜编号 ([23456789ABCDEFGHJKLMNPQRSTUVWXYZ]{6})\b", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex AnnouncementRegex();

    public static bool IsSafeApprovalButtonCommand(string? command)
        => ApprovalFlow.TryParseCommand(command) is not null;

    public static bool TryExtractRequestId(string? text, out string requestId)
    {
        requestId = string.Empty;
        if (!ApprovalOutboundScope.IsSystemAnnouncement || string.IsNullOrWhiteSpace(text))
            return false;

        var m = AnnouncementRegex().Match(text.TrimStart());
        if (!m.Success) return false;
        var candidate = m.Groups[1].Value;
        if (!ApprovalFlow.IsRequestIdShape(candidate)) return false;
        requestId = candidate;
        return true;
    }

    public static bool TryBuildFeishuCardJson(string? text, out string cardJson)
    {
        cardJson = string.Empty;
        if (!TryExtractRequestId(text, out var requestId)) return false;

        var elements = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "markdown",
                ["content"] = text!
            },
            new JsonObject
            {
                ["tag"] = "button",
                ["type"] = "primary",
                ["text"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = "批准执行"
                },
                ["behaviors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "callback",
                        ["value"] = new JsonObject { ["approval_cmd"] = $"同意 {requestId}" }
                    }
                }
            },
            new JsonObject
            {
                ["tag"] = "button",
                ["type"] = "danger",
                ["text"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = "拒绝"
                },
                ["behaviors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "callback",
                        ["value"] = new JsonObject { ["approval_cmd"] = $"拒绝 {requestId}" }
                    }
                }
            }
        };

        var card = new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject { ["wide_screen_mode"] = true },
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = $"安全审批 · {requestId}" }
            },
            ["body"] = new JsonObject { ["elements"] = elements }
        };
        cardJson = card.ToJsonString();
        return true;
    }

    public static bool TryBuildOfficialKeyboardJson(string? text, out JsonObject keyboard)
    {
        keyboard = new JsonObject();
        if (!TryExtractRequestId(text, out var requestId)) return false;

        var rowButtons = new JsonArray
        {
            BuildOfficialButton($"appr_yes_{requestId}", "批准执行", style: 1, $"/approve {requestId}"),
            BuildOfficialButton($"appr_no_{requestId}", "拒绝", style: 0, $"/deny {requestId}")
        };

        keyboard = new JsonObject
        {
            ["content"] = new JsonObject
            {
                ["rows"] = new JsonArray
                {
                    new JsonObject { ["buttons"] = rowButtons }
                }
            }
        };
        return true;
    }

    private static JsonObject BuildOfficialButton(string id, string label, int style, string data)
        => new()
        {
            ["id"] = id,
            ["render_data"] = new JsonObject
            {
                ["label"] = label,
                ["visited_label"] = label,
                ["style"] = style
            },
            ["action"] = new JsonObject
            {
                ["type"] = 1,
                ["permission"] = new JsonObject { ["type"] = 2 },
                ["data"] = data,
                ["unsupport_tips"] = "请升级 QQ 客户端后再点击审批"
            }
        };
}
