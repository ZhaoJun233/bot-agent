using System.Collections.Concurrent;

namespace BotAgent.Services.Net;

/// <summary>
/// 联网研究用例（批次 4 从 <c>BotAgentHost</c> 抽出）：搜索 / 读页面的冷却、结果笔记、
/// 以及面板自测入口。
///
/// 边界：**"搜回来的东西怎么用"留在回复流程里** —— 谁触发（模型填 search/read）、
/// 要不要过能力闸门、搜完要不要再给模型一次开口机会、失败要不要记一次参与状态，
/// 都属于"一次回复该怎么走"。这里只做"出网拿资料 + 把资料攒成一段笔记"。
/// </summary>
public sealed class ResearchUseCase
{
    private readonly WebSearchService? _search;
    private readonly Action<string> _log;

    /// <summary>每个会话最近一次联网搜索的时间（冷却：搜索是真出网 + 几秒等待）。</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSearch = new();

    /// <summary>每会话攒下的"查回来的资料"：下一轮生成时注入，然后取走（一次性）。</summary>
    private readonly ConcurrentDictionary<string, string> _notes = new();

    public ResearchUseCase(WebSearchService? search, Action<string> log)
    {
        _search = search;
        _log = log;
    }

    /// <summary>搜索服务就绪（模型动作与面板自测都要先看它）。</summary>
    public bool IsReady => _search is not null;

    /// <summary>
    /// 冷却门。返回 true 表示这次可以搜（并**已经记上时间**）；false 时 <paramref name="reason" /> 给日志。
    /// </summary>
    public bool TryBeginSearch(string sourceKey, int cooldownSeconds, out string reason)
    {
        reason = string.Empty;
        var now = Clock.Now;
        var cooldown = TimeSpan.FromSeconds(Math.Max(0, cooldownSeconds));
        if (cooldown <= TimeSpan.Zero)
        {
            _lastSearch[sourceKey] = now;
            return true;
        }

        if (_lastSearch.TryGetValue(sourceKey, out var last) && now - last < cooldown)
        {
            reason = $"同会话 {cooldown.TotalSeconds:F0}s 内刚搜过";
            return false;
        }

        _lastSearch[sourceKey] = now;
        return true;
    }

    /// <summary>搜一次并把结果攒进笔记。返回结果本身（调用方据此判断有没有内容）。</summary>
    public async Task<WebSearchResult?> SearchAsync(string sourceKey, string query, CancellationToken ct = default)
    {
        if (_search is null)
        {
            return null;
        }

        var result = await _search.SearchAsync(query, ct);
        AddNote(sourceKey, result.Describe());
        return result;
    }

    /// <summary>读一个页面并把正文（或失败说明）攒进笔记。返回正文；失败为 null。</summary>
    public async Task<string?> ReadPageAsync(string sourceKey, string url, CancellationToken ct = default)
    {
        if (_search is null)
        {
            return null;
        }

        var (text, error) = await _search.ReadPageAsync(url, ct);
        var note = text is null
            ? $"读页面「{url}」失败：{error}（如实说没读到就行，别猜页面里写了什么。）"
            : $"页面 {url} 的正文（已抽取）：\n{text}";
        AddNote(sourceKey, note);
        _log(text is null ? $"[Search] 读页面失败：{error}" : $"[Search] 已读到页面正文（{text.Length} 字）");
        return text;
    }

    /// <summary>把一段资料追加到某个会话的笔记里（注入给下一轮）。</summary>
    public void AddNote(string sourceKey, string note)
        => _notes.AddOrUpdate(sourceKey, note, (_, old) => old + "\n\n" + note);

    /// <summary>
    /// 这一轮没用上（审批/提问/被守卫拦下）时把资料**原地放回**，别丢 —— 下一轮还能接着用。
    /// </summary>
    public void KeepNote(string sourceKey, string? note, string why)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        AddNote(sourceKey, note);
        // ⚠ 这句话是运维与集成测试都在看的现场（harness 按它判断"资料没被白扔"），措辞别随手改
        _log($"[Search] 查到的资料这次没说出去（{why}）→ 留着下一轮说");
    }

    /// <summary>取走这个会话的资料（一次性：注入过就不再重复注入）。</summary>
    public string? TakeNote(string sourceKey)
        => _notes.TryRemove(sourceKey, out var note) ? note : null;

    /// <summary>面板自测：真跑一次搜索，把结果（或失败原因）原样给面板看。</summary>
    public Task<WebSearchResult> TestSearchAsync(string query, CancellationToken ct)
        => _search is null
            ? Task.FromResult(new WebSearchResult(query, null, Array.Empty<WebSearchHit>(), "无", "搜索服务还没初始化"))
            : _search.SearchAsync(query, ct);

    /// <summary>面板自测：真读一个页面。</summary>
    public Task<(string? Text, string? Error)> TestReadPageAsync(string url, CancellationToken ct)
        => _search is null ? Task.FromResult<(string?, string?)>((null, "搜索服务还没初始化")) : _search.ReadPageAsync(url, ct);
}
