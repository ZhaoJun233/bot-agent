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
        var completed = _traces?.CompletedTotal ?? 0;
        var active = _traces?.ActiveCount ?? 0;
        var body = new StringBuilder()
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
            .Append("botagent_trace_archive_completion_tokens ").AppendLine((summary?.CompletionTokens ?? 0).ToString(CultureInfo.InvariantCulture));

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
