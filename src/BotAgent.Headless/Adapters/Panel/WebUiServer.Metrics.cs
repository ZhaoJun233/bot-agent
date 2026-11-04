using System.Globalization;
using System.Net;
using System.Text;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private Task WriteMetricsAsync(HttpListenerContext context)
    {
        var summary = _traceArchive?.Snapshot();
        var durations = summary?.Durations ?? Array.Empty<int>();
        var started = _traces?.StartedTotal ?? 0;
        var completed = _traces?.CompletedTotal ?? 0;
        var active = _traces?.ActiveCount ?? 0;
        var body = new StringBuilder()
            .AppendLine("# HELP botagent_turns_started_total Decision turns accepted by the processing pipeline since process start.")
            .AppendLine("# TYPE botagent_turns_started_total counter")
            .Append("botagent_turns_started_total ").AppendLine(started.ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_turns_completed_total Completed decision turns since process start.")
            .AppendLine("# TYPE botagent_turns_completed_total counter")
            .Append("botagent_turns_completed_total ").AppendLine(completed.ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_turns_active Current decision turns in progress.")
            .AppendLine("# TYPE botagent_turns_active gauge")
            .Append("botagent_turns_active ").AppendLine(active.ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_total Archived slow or abnormal traces retained for seven days.")
            .AppendLine("# TYPE botagent_trace_archive_total gauge")
            .Append("botagent_trace_archive_total ").AppendLine((summary?.Count ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_latency_ms Archived trace latency quantiles in milliseconds.")
            .AppendLine("# TYPE botagent_trace_archive_latency_ms summary")
            .Append("botagent_trace_archive_latency_ms{quantile=\"0.5\"} ").AppendLine(Quantile(durations, .50).ToString(CultureInfo.InvariantCulture))
            .Append("botagent_trace_archive_latency_ms{quantile=\"0.95\"} ").AppendLine(Quantile(durations, .95).ToString(CultureInfo.InvariantCulture))
            .Append("botagent_trace_archive_latency_ms{quantile=\"0.99\"} ").AppendLine(Quantile(durations, .99).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_prompt_tokens Archived prompt token count.")
            .AppendLine("# TYPE botagent_trace_archive_prompt_tokens gauge")
            .Append("botagent_trace_archive_prompt_tokens ").AppendLine((summary?.PromptTokens ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_completion_tokens Archived completion token count.")
            .AppendLine("# TYPE botagent_trace_archive_completion_tokens gauge")
            .Append("botagent_trace_archive_completion_tokens ").AppendLine((summary?.CompletionTokens ?? 0).ToString(CultureInfo.InvariantCulture))
            // #135：归档里能确定的缓存输入 token（未知 / 字段缺失的轮次不计入，也绝不当 0）。
            .AppendLine("# HELP botagent_trace_archive_cached_prompt_tokens Archived cached prompt tokens where the upstream reported them.")
            .AppendLine("# TYPE botagent_trace_archive_cached_prompt_tokens gauge")
            .Append("botagent_trace_archive_cached_prompt_tokens ").AppendLine((summary?.CachedPromptTokens ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_cache_write_tokens Archived cache write tokens where the upstream reported them.")
            .AppendLine("# TYPE botagent_trace_archive_cache_write_tokens gauge")
            .Append("botagent_trace_archive_cache_write_tokens ").AppendLine((summary?.CacheWriteTokens ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_trace_archive_cache_unknown_turns Archived turns whose cache usage the upstream did not report.")
            .AppendLine("# TYPE botagent_trace_archive_cache_unknown_turns gauge")
            .Append("botagent_trace_archive_cache_unknown_turns ").AppendLine((summary?.CacheUnknownTurns ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_prompt_tokens_total Cumulative prompt tokens consumed since process start.")
            .AppendLine("# TYPE botagent_prompt_tokens_total counter")
            .Append("botagent_prompt_tokens_total ").AppendLine((_traces?.PromptTokensTotal ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_completion_tokens_total Cumulative completion tokens produced since process start.")
            .AppendLine("# TYPE botagent_completion_tokens_total counter")
            .Append("botagent_completion_tokens_total ").AppendLine((_traces?.CompletionTokensTotal ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_prompt_cached_tokens_total Cumulative prompt tokens served from the provider cache since process start. Turns whose provider reported no cache usage are not counted as zero (#98).")
            .AppendLine("# TYPE botagent_prompt_cached_tokens_total counter")
            .Append("botagent_prompt_cached_tokens_total ").AppendLine((_traces?.CachedPromptTokensTotal ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_prompt_cache_write_tokens_total Cumulative prompt tokens written into the provider cache since process start (only reported by some providers).")
            .AppendLine("# TYPE botagent_prompt_cache_write_tokens_total counter")
            .Append("botagent_prompt_cache_write_tokens_total ").AppendLine((_traces?.CacheWriteTokensTotal ?? 0).ToString(CultureInfo.InvariantCulture))
            .AppendLine("# HELP botagent_prompt_cache_unknown_total Completed turns whose provider did not report cache usage (denominator for the cached ratio).")
            .AppendLine("# TYPE botagent_prompt_cache_unknown_total counter")
            .Append("botagent_prompt_cache_unknown_total ").AppendLine((_traces?.CacheUnknownTurnsTotal ?? 0).ToString(CultureInfo.InvariantCulture));

        var circuits = _circuitStatusProvider?.Invoke();
        if (circuits is { Count: > 0 })
        {
            body.AppendLine("# HELP botagent_circuit_breaker_state Current state of circuit breakers.")
                .AppendLine("# TYPE botagent_circuit_breaker_state gauge");
            foreach (var c in circuits)
            {
                body.Append("botagent_circuit_breaker_state{type=\"")
                    .Append(c.Type)
                    .Append("\",id=\"")
                    .Append(c.Id)
                    .Append("\",state=\"")
                    .Append(c.State)
                    .AppendLine("\"} 1");
            }
        }

        var platforms = _platformRegistry?.GetSnapshots();
        if (platforms is { Count: > 0 })
        {
            body.AppendLine("# HELP botagent_platform_adapter_connected Connection status of registered platform adapters.")
                .AppendLine("# TYPE botagent_platform_adapter_connected gauge");
            foreach (var p in platforms)
            {
                body.Append("botagent_platform_adapter_connected{platform=\"")
                    .Append(p.PlatformId)
                    .Append("\",account=\"")
                    .Append(p.AccountScope)
                    .Append("\"} ")
                    .AppendLine(p.Connected ? "1" : "0");
            }
        }

        return WriteBytesAsync(context, 200, "text/plain; version=0.0.4; charset=utf-8", Encoding.UTF8.GetBytes(body.ToString()));
    }

    private static int Quantile(int[] values, double q)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        var index = Math.Clamp((int)Math.Ceiling(values.Length * q) - 1, 0, values.Length - 1);
        return values[index];
    }
}
