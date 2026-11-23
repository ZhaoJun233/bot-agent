using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BotAgent.Platforms.Common;

public sealed record MemoryCardButton(string Label, string Command, string Style);

/// <summary>Builds private-chat interactive memory confirmation cards and strictly validates button callbacks.</summary>
public static partial class MemoryPrivateCardHelper
{
    private const string MemoryShowPrefix = "本人偏好：";
    private const string MemoryListPrefix = "本人记忆 ";

    [GeneratedRegex(@"^//memory confirm ([a-f0-9]{32}) ([1-9][0-9]{0,8}) ([a-f0-9]{32}) allow-source (accept-group-audience|private-only)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConfirmLineRegex();

    [GeneratedRegex(@"事实 ([a-f0-9]{32}) · 版本", RegexOptions.CultureInvariant)]
    private static partial Regex FactLineRegex();

    [GeneratedRegex(@"//memory show ([a-f0-9]{32})", RegexOptions.CultureInvariant)]
    private static partial Regex ShowCommandRegex();

    [GeneratedRegex(@"^//memory (?:confirm [0-9a-f]{32} [1-9][0-9]{0,8} [0-9a-f]{32} allow-source (?:accept-group-audience|private-only)|reject [0-9a-f]{32}|forget [0-9a-f]{32}|show [0-9a-f]{32}|list(?: [1-9][0-9]{0,3})?)$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCallbackCommandRegex();

    public static bool IsSafePrivateMemoryButtonCommand(string? command)
        => !string.IsNullOrWhiteSpace(command) && SafeCallbackCommandRegex().IsMatch(command.Trim());

    public static bool TryExtractButtons(string? text, out IReadOnlyList<MemoryCardButton> buttons)
    {
        buttons = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!text.StartsWith(MemoryShowPrefix, StringComparison.Ordinal)
            && !text.StartsWith(MemoryListPrefix, StringComparison.Ordinal))
            return false;

        var list = new List<MemoryCardButton>(4);
        foreach (var rawLine in text.Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (ConfirmLineRegex().Match(trimmed) is { Success: true } confirmMatch)
            {
                var factId = confirmMatch.Groups[1].Value;
                list.Add(new("确认授权", trimmed, "primary"));
                list.Add(new("暂不启用", $"//memory reject {factId}", "default"));
                list.Add(new("遗忘删除", $"//memory forget {factId}", "danger"));
                break;
            }
        }

        if (list.Count == 0 && text.StartsWith(MemoryShowPrefix, StringComparison.Ordinal))
        {
            var factMatch = FactLineRegex().Match(text);
            if (factMatch.Success)
            {
                var factId = factMatch.Groups[1].Value;
                list.Add(new("遗忘删除", $"//memory forget {factId}", "danger"));
            }
        }
        else if (list.Count == 0 && text.StartsWith(MemoryListPrefix, StringComparison.Ordinal))
        {
            var index = 1;
            foreach (Match m in ShowCommandRegex().Matches(text))
            {
                if (list.Count >= 3) break;
                list.Add(new($"查看 #{index++}", $"//memory show {m.Groups[1].Value}", "default"));
            }
        }

        if (list.Count == 0) return false;
        buttons = list;
        return true;
    }

    public static bool TryBuildFeishuCardJson(string? text, out string cardJson)
    {
        cardJson = string.Empty;
        if (!TryExtractButtons(text, out var buttons)) return false;

        var elements = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "markdown",
                ["content"] = text!
            }
        };

        foreach (var btn in buttons)
        {
            elements.Add(new JsonObject
            {
                ["tag"] = "button",
                ["type"] = btn.Style,
                ["text"] = new JsonObject
                {
                    ["tag"] = "plain_text",
                    ["content"] = btn.Label
                },
                ["behaviors"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "callback",
                        ["value"] = new JsonObject { ["memory_cmd"] = btn.Command }
                    }
                }
            });
        }

        var card = new JsonObject
        {
            ["schema"] = "2.0",
            ["config"] = new JsonObject { ["wide_screen_mode"] = true },
            ["header"] = new JsonObject
            {
                ["title"] = new JsonObject { ["tag"] = "plain_text", ["content"] = "统一记忆私聊确认" }
            },
            ["body"] = new JsonObject { ["elements"] = elements }
        };
        cardJson = card.ToJsonString();
        return true;
    }

    public static bool TryBuildOfficialKeyboardJson(string? text, out JsonObject keyboard)
    {
        keyboard = new JsonObject();
        if (!TryExtractButtons(text, out var buttons)) return false;

        var rowButtons = new JsonArray();
        for (var i = 0; i < buttons.Count; i++)
        {
            var btn = buttons[i];
            rowButtons.Add(new JsonObject
            {
                ["id"] = $"mem_{i + 1}",
                ["render_data"] = new JsonObject
                {
                    ["label"] = btn.Label,
                    ["visited_label"] = btn.Label,
                    ["style"] = btn.Style == "primary" ? 1 : 0
                },
                ["action"] = new JsonObject
                {
                    ["type"] = 1,
                    ["permission"] = new JsonObject { ["type"] = 2 },
                    ["data"] = btn.Command,
                    ["unsupport_tips"] = "请升级 QQ 客户端后再点击确认"
                }
            });
        }

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
}
