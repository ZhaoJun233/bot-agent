using System.Text;

namespace BotAgent.Services.Net;


/// <summary>一条搜索结果。</summary>
public sealed record WebSearchHit(string Title, string Url, string Snippet);

/// <summary>一次联网搜索的结果：答案（如果有）+ 若干来源。</summary>
public sealed record WebSearchResult(
    string Query,
    string? Answer,
    IReadOnlyList<WebSearchHit> Hits,
    string Provider,
    string? Error = null)
{
    public bool HasContent => !string.IsNullOrWhiteSpace(Answer) || Hits.Count > 0;

    /// <summary>拼成给模型看的一段事实（搜不到时如实说明，别让它瞎编）。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append("搜索「").Append(Query).Append("」").Append("（").Append(Provider).Append("）：\n");

        if (!string.IsNullOrWhiteSpace(Answer))
        {
            sb.Append(Answer!.Trim()).Append('\n');
        }

        if (Hits.Count > 0)
        {
            sb.Append("来源：\n");
            foreach (var hit in Hits.Take(6))
            {
                sb.Append("- ").Append(hit.Title);
                if (!string.IsNullOrWhiteSpace(hit.Snippet))
                {
                    sb.Append("：").Append(Clip(hit.Snippet, 160));
                }

                if (!string.IsNullOrWhiteSpace(hit.Url))
                {
                    sb.Append("  ").Append(hit.Url);
                }

                sb.Append('\n');
            }
        }

        if (!HasContent)
        {
            sb.Append("（没有搜到可用内容。").Append(Error ?? "上游没有返回结果").Append("）");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
