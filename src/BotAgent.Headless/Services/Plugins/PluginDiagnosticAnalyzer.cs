using System.Reflection;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 规则表驱动的插件故障分析器：自主解构异常链、定级故障严重度并输出结构化诊断报告。
/// </summary>
public static class PluginDiagnosticAnalyzer
{
    public static PluginDiagnosticReport Analyze(
        string pluginId,
        string assemblyName,
        Exception ex,
        PluginLifecycleState state)
    {
        var evidence = new List<string>();
        CollectEvidence(ex, evidence);

        string errorCode;
        DiagnosticSeverity severity;
        string actionTaken;
        PluginDiagnosticRootCause rootCause;
        var remediations = new List<PluginDiagnosticRemediation>();

        switch (ex)
        {
            case BadImageFormatException badImage:
                errorCode = "ERR_BAD_IMAGE";
                severity = DiagnosticSeverity.Fatal;
                actionTaken = "Quarantine-Assembly";
                rootCause = new PluginDiagnosticRootCause("BadImageFormatException", badImage.FileName ?? assemblyName, badImage.Message);
                remediations.Add(new PluginDiagnosticRemediation("REBUILD_ASSEMBLY", "程序集架构与当前运行时不兼容，请确认平台架构并以 net8.0 重新编译。"));
                break;

            case ReflectionTypeLoadException typeLoad:
                errorCode = "ERR_TYPE_LOAD_FAILED";
                severity = DiagnosticSeverity.Isolated;
                actionTaken = "Safe-Skip";
                var firstLoaderEx = typeLoad.LoaderExceptions?.FirstOrDefault(e => e != null);
                var targetAsm = firstLoaderEx is FileNotFoundException innerFnf ? innerFnf.FileName ?? "Unknown" : assemblyName;
                rootCause = new PluginDiagnosticRootCause(
                    firstLoaderEx?.GetType().Name ?? "ReflectionTypeLoadException",
                    targetAsm,
                    firstLoaderEx?.Message ?? typeLoad.Message);

                if (firstLoaderEx is FileNotFoundException)
                {
                    remediations.Add(new PluginDiagnosticRemediation("RESTORE_DEPENDENCY", $"缺少必要依赖项 {targetAsm}，请将其放置于 runtime/plugins/ 目录。"));
                }
                else
                {
                    remediations.Add(new PluginDiagnosticRemediation("RECOMPILE_PLUGIN", "插件类型加载失败，请检查是否引用了失配的程序集版本。"));
                }
                break;

            case FileNotFoundException fnf:
                errorCode = "ERR_MISSING_DEPENDENCY";
                severity = DiagnosticSeverity.Isolated;
                actionTaken = "Safe-Skip";
                rootCause = new PluginDiagnosticRootCause("FileNotFoundException", fnf.FileName ?? "Unknown", fnf.Message);
                remediations.Add(new PluginDiagnosticRemediation("RESTORE_DEPENDENCY", $"依赖程序集 {fnf.FileName} 缺失，请补齐后重新尝试加载。"));
                break;

            case MissingMethodException mm:
                errorCode = "ERR_CONTRACT_MISMATCH";
                severity = DiagnosticSeverity.Isolated;
                actionTaken = "Safe-Skip";
                rootCause = new PluginDiagnosticRootCause("MissingMethodException", assemblyName, mm.Message);
                remediations.Add(new PluginDiagnosticRemediation("UPDATE_CONTRACT", "插件方法签名或核心接口不匹配，请对齐最新 IBotPlugin 契约重新编译。"));
                break;

            default:
                errorCode = "ERR_INIT_EXCEPTION";
                severity = DiagnosticSeverity.Isolated;
                actionTaken = "Safe-Skip";
                rootCause = new PluginDiagnosticRootCause(ex.GetType().Name, assemblyName, ex.Message);
                remediations.Add(new PluginDiagnosticRemediation("INSPECT_STACKTRACE", "插件执行初始化逻辑时抛出非受检异常，请检查插件业务逻辑与宿主端口。"));
                break;
        }

        return new PluginDiagnosticReport(
            pluginId,
            assemblyName,
            state,
            severity,
            actionTaken,
            errorCode,
            rootCause,
            remediations,
            evidence,
            DateTimeOffset.UtcNow);
    }

    private static void CollectEvidence(Exception? ex, List<string> list)
    {
        while (ex != null)
        {
            list.Add($"[{ex.GetType().Name}] {ex.Message}");
            if (ex is ReflectionTypeLoadException rtl && rtl.LoaderExceptions != null)
            {
                foreach (var sub in rtl.LoaderExceptions)
                {
                    if (sub != null) list.Add($"  -> [LoaderException] {sub.GetType().Name}: {sub.Message}");
                }
            }
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                list.Add(ex.StackTrace);
            }
            ex = ex.InnerException;
        }
    }
}