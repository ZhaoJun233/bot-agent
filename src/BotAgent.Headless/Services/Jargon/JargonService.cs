using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BotAgent.Domain.Jargon;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Jargon;

/// <summary>
/// 圈子黑话/俚语管理与自动识别提取服务（对标 MaiBot Jargon 机制）。
/// 包含内存热度缓冲、Token 预算与冷却保护，严格保护用户隐私。
/// </summary>
public sealed class JargonService
{
    private readonly IJargonRepository _repo;
    private readonly SettingsBox _settings;

    // 内存频率缓冲：scope -> (phrase -> hitCount)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> _phraseCounters = new();

    // 节流冷却：scope -> 上次提炼时刻
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastExtractTime = new();

    private readonly object _bufferGate = new();
    private const int MaxTrackedScopes = 500;
    private const int MaxPhrasesPerScope = 200;
    private static readonly TimeSpan ExtractCooldown = TimeSpan.FromMinutes(15);
    private const int MinOccurrencesToPropose = 2;

    public JargonService(IJargonRepository repo, SettingsBox settings)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// 观察一条入站群聊消息，在后台提取并记录潜在黑话候选词（非阻塞、安全脱敏）。
    /// </summary>
    public void ObserveMessage(string scope, string? text)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var candidates = JargonFilterRules.ExtractCandidatePhrases(text);
        if (candidates.Count == 0)
        {
            return;
        }

        // 准入与淘汰共用锁，保证并发观察也不会突破 500 会话 / 单群 200 候选词上限。
        lock (_bufferGate)
        {
            if (!_phraseCounters.TryGetValue(scope, out var scopeCounter))
            {
                if (_phraseCounters.Count >= MaxTrackedScopes)
                {
                    var first = _phraseCounters.Keys.First();
                    _phraseCounters.TryRemove(first, out _);
                    _lastExtractTime.TryRemove(first, out _);
                }

                scopeCounter = new ConcurrentDictionary<string, int>();
                _phraseCounters[scope] = scopeCounter;
            }

            foreach (var phrase in candidates)
            {
                // 每个新词准入前淘汰最低频项，大批量或全是高频词时也保证有界。
                if (!scopeCounter.ContainsKey(phrase) && scopeCounter.Count >= MaxPhrasesPerScope)
                {
                    var leastFrequent = scopeCounter.OrderBy(pair => pair.Value)
                        .ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;
                    scopeCounter.TryRemove(leastFrequent, out _);
                }

                scopeCounter.AddOrUpdate(phrase, 1, (_, count) => count + 1);
            }
        }
    }

    /// <summary>
    /// 执行指定作用域的黑话发现与提炼沉淀（受冷却时间与频次门槛保护）。
    /// </summary>
    public async Task<int> DiscoverAndPersistAsync(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return 0;
        }

        var now = Clock.Now;
        ConcurrentDictionary<string, int> counter;
        lock (_bufferGate)
        {
            if (_lastExtractTime.TryGetValue(scope, out var last) && now - last < ExtractCooldown)
            {
                return 0; // 冷却中，避免频繁调度
            }

            if (!_phraseCounters.TryGetValue(scope, out counter!) || counter.IsEmpty)
            {
                return 0;
            }

            _lastExtractTime[scope] = now;
        }
        int persistedCount = 0;

        foreach (var kvp in counter)
        {
            var phrase = kvp.Key;
            var hits = kvp.Value;

            if (hits >= MinOccurrencesToPropose)
            {
                var existing = await _repo.GetAsync(scope, phrase).ConfigureAwait(false);
                if (existing is null)
                {
                    // 首次发现，以 Pending 待审核状态落库
                    await _repo.UpsertAsync(new JargonEntry
                    {
                        Scope = scope,
                        Phrase = phrase,
                        Meaning = $"在群聊中高频出现的流行词汇/梗（累计出现 {hits} 次）",
                        Status = JargonStatus.Pending,
                        HitCount = hits,
                        CreatedAt = now,
                        UpdatedAt = now
                    }).ConfigureAwait(false);

                    persistedCount++;
                }
                else
                {
                    // 已存在，增量更新频次
                    existing.HitCount += hits;
                    existing.UpdatedAt = now;
                    await _repo.UpsertAsync(existing).ConfigureAwait(false);
                }

                // 消费后重置内存计数（与容量检查共用锁，避免淘汰时枚举被清空）。
                lock (_bufferGate)
                {
                    counter.TryRemove(phrase, out _);
                }
            }
        }

        return persistedCount;
    }

    /// <summary>
    /// 渲染可注入 Prompt 的黑话字典说明段落（仅包含已确认或手动录入的条目）。
    /// </summary>
    public async Task<string?> RenderPromptSnippetAsync(string scope, int limit = 15)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        var confirmed = await _repo.ListConfirmedForPromptAsync(scope, limit).ConfigureAwait(false);
        if (confirmed.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("\n\n[本群圈子黑话/俚语字典]");
        sb.AppendLine("请自然理解并在契合语境时模仿群友使用以下黑话，避免机械生硬解释：");
        foreach (var item in confirmed)
        {
            var phrase = SanitizePromptText(item.Phrase);
            var meaning = SanitizePromptText(item.Meaning);
            if (!string.IsNullOrEmpty(phrase) && !string.IsNullOrEmpty(meaning))
                sb.AppendLine($"• {phrase}：{meaning}");
        }

        return sb.ToString().TrimEnd();
    }

    public void ClearScope(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return;
        lock (_bufferGate)
        {
            _phraseCounters.TryRemove(scope, out _);
            _lastExtractTime.TryRemove(scope, out _);
        }
    }

    private static string SanitizePromptText(string? raw)
        => BotAgent.Domain.Conversation.MessageMarkers
            .EscapeExternalControlTags((raw ?? string.Empty).Replace('\r', ' ').Replace('\n', ' '))
            .Trim();
}
