namespace BotAgent.Adapters.Panel;

/// <summary>Keep the selected recovery image immutable while a helper replaces the host.</summary>
internal static class PanelDeploymentPlan
{
    internal static string BackupArguments(bool build) => build
        ? "tag qqchat-agent:latest qqchat-agent:prev"
        : "tag qqchat-agent:latest qqchat-agent:rollback-current";

    internal static bool IsImageId(string imageId) => imageId.Length == 71
        && imageId.StartsWith("sha256:", StringComparison.Ordinal)
        && imageId.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0;

    internal static string HelperScript(bool build, string rollbackImageId)
    {
        if (!build && !IsImageId(rollbackImageId)) throw new ArgumentException("invalid_rollback_image", nameof(rollbackImageId));
        const string compose = "docker compose -p qqchat --project-directory /work -f /work/docker-compose.yml";
        var operation = build ? "docker build -f Dockerfile.app -t qqchat-agent:latest /work"
            : "docker tag " + rollbackImageId + " qqchat-agent:latest";
        return "exec > /work/deploy.last.log 2>&1; " + operation + " && " + compose
            + " up -d --no-deps qqchat; result=$?; echo EXIT=$result > /work/deploy.last.exit; "
            + "echo DONE >> /work/deploy.last.log; exit $result";
    }
}
