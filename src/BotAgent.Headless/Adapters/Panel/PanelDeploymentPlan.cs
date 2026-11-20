namespace BotAgent.Adapters.Panel;

/// <summary>Freeze package writes and inspect the candidate as data before replacing the host.</summary>
internal static class PanelDeploymentPlan
{
    internal static string BackupArguments(bool build, string currentImageId)
    {
        if (!IsImageId(currentImageId)) throw new ArgumentException("invalid_current_image", nameof(currentImageId));
        return "tag " + currentImageId + (build ? " qqchat-agent:prev" : " qqchat-agent:rollback-current");
    }

    internal static bool IsImageId(string imageId) => imageId.Length == 71
        && imageId.StartsWith("sha256:", StringComparison.Ordinal)
        && imageId.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF".AsSpan()) < 0;

    internal static string HelperScript(bool build, string rollbackImageId, string currentImageId, string pluginRoot)
    {
        if (!build && !IsImageId(rollbackImageId)) throw new ArgumentException("invalid_rollback_image", nameof(rollbackImageId));
        var backup = BackupArguments(build, currentImageId);
        if (!pluginRoot.StartsWith('/') || pluginRoot.Contains('\0'))
            throw new ArgumentException("invalid_plugin_root", nameof(pluginRoot));
        var prepare = build
            ? "docker build -f \"$work/Dockerfile.app\" --iidfile \"$scratch/image\" \"$work\"\ncandidate_image=$(cat \"$scratch/image\")"
            : "candidate_image=" + rollbackImageId;
        return $$$"""
            set -eu
            work=/work
            exec > "$work/deploy.last.log" 2>&1
            current_image={{{currentImageId}}}
            plugin_root={{{Quote(pluginRoot)}}}
            scratch=
            candidate_container=
            stopped=0
            tagged=0
            adopting=0
            finish() {
                result=$?
                trap - EXIT HUP INT TERM
                if [ "$stopped" = 1 ] && [ "$adopting" = 0 ]; then
                    if [ "$tagged" = 1 ]; then
                        docker tag "$current_image" qqchat-agent:latest || result=70
                    fi
                    docker start qqchat-bot || result=70
                fi
                if [ -n "$candidate_container" ]; then docker rm -v "$candidate_container" || true; fi
                if [ -n "$scratch" ]; then
                    rm -f "$scratch/candidate.dll" "$scratch/image"
                    rmdir "$scratch" || true
                fi
                echo EXIT=$result > "$work/deploy.last.exit"
                echo DONE >> "$work/deploy.last.log"
                exit $result
            }
            trap finish EXIT
            trap 'exit 130' INT
            trap 'exit 143' HUP TERM
            scratch=$(mktemp -d "$work/.deploy-preflight.XXXXXX")
            {{{prepare}}}
            candidate_container=$(docker create --network none --entrypoint /bin/true "$candidate_image")
            docker cp "$candidate_container:/app/BotAgent.Headless.dll" "$scratch/candidate.dll"
            actual_image=$(docker inspect --format '{{.Image}}' qqchat-bot)
            if [ "$actual_image" != "$current_image" ]; then exit 4; fi
            # Stop the only writer before inspecting state. No candidate process has run.
            stopped=1
            docker stop --time 30 qqchat-bot
            docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
                --volumes-from qqchat-bot:ro \
                --mount "type=bind,src=/opt/qqchat/${scratch##*/},dst=/preflight,readonly" \
                --entrypoint dotnet "$current_image" /app/BotAgent.Headless.dll \
                --check-plugin-state "$plugin_root" --candidate /preflight/candidate.dll \
                > "$work/deploy.preflight.json"
            docker {{{backup}}}
            tagged=1
            docker tag "$candidate_image" qqchat-agent:latest
            # Once adoption starts, never automatically reopen data with the old reader.
            adopting=1
            docker compose -p qqchat --project-directory "$work" -f "$work/docker-compose.yml" up -d --no-deps qqchat
            """;
    }

    private static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
