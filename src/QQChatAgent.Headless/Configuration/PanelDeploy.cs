using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using QQChatAgent.Services;

namespace QQChatAgent.Configuration;

/// <summary>
/// 面板**一键部署**（号主 2026-09-18 要的：“面板有没有提供一键部署”）。
///
/// 干什么：在面板上把一个新的 `app.tar.gz`（发布产物）交给服务器 → 服务器**重建镜像 + 替换机器人容器**。
/// 两种给产物的方式：
///   • **上传**：把 app.tar.gz 拖上去（离线也能用）；适合从这台开发机手动发。
///   • **URL 拉取**：给个地址（GitHub release 资产 / 自建文件站都行），服务器自己下载；地址会记住 —— **以后就是一个按钮**。
/// 还能**回滚**：部署前会把当前镜像打上 `qqchat-agent:prev`，一键退回去。
///
/// 为什么不自己在容器里跑 compose：
///   我们就在要被替换的那个容器里 —— `docker compose up -d` 会先把我们杀掉（命令也就断了），
///   根本跑不到“起新容器”那一步。所以真正执行重建的是**一个 helper 容器**（`docker:cli`，
///   它自带 compose）：它挂在宿主 docker 上，把我们的容器换掉，日志写到宿主机文件里，
///   面板重启后读那个文件就能看到结果。
///
/// 安全：总开关 `PanelDeployEnabled`（默认关）+ 体积上限 + gzip 魔数校验 + 部署前备份旧产物与旧镜像。
/// </summary>
public sealed partial class WebUiServer
{
    private const long DeployMaxBytes = 64L * 1024 * 1024;
    private static readonly HttpClient DeployHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>部署相关文件都在宿主机部署目录里（容器里挂在 /host/qqchat）。</summary>
    private const string DeployDir = "/host/qqchat";
    private const string DeployTar = DeployDir + "/app.tar.gz";
    private const string DeployTarPrev = DeployDir + "/app.tar.gz.prev";
    private const string DeployTarNew = DeployDir + "/app.tar.gz.new";
    private const string DeployLog = DeployDir + "/deploy.last.log";
    private const string DeployExit = DeployDir + "/deploy.last.exit";

    private async Task HandlePanelDeployAsync(HttpListenerContext context, string path, string method)
    {
        if (method != "POST")
        {
            await WriteJsonAsync(context, 200, await BuildDeployStatusAsync());
            return;
        }

        if (!_settings.PanelDeployEnabled)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "面板一键部署没开：先在设置里打开「面板一键部署」（默认关）"
            });
            return;
        }

        if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase))
        {
            await PanelDeployUploadAsync(context);
            return;
        }

        if (path.EndsWith("/url", StringComparison.OrdinalIgnoreCase))
        {
            await PanelDeployFromUrlAsync(context);
            return;
        }

        if (path.EndsWith("/rollback", StringComparison.OrdinalIgnoreCase))
        {
            await PanelDeployRollbackAsync(context);
            return;
        }

        await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "unknown deploy action" });
    }

    // ───────────────────────── 状态 ─────────────────────────

    private async Task<JsonObject> BuildDeployStatusAsync()
    {
        var containerStart = await RunDockerAsync("inspect --format \"{{.State.StartedAt}}\" qqchat-bot", 15);
        var imageInfo = await RunDockerAsync("inspect --format \"{{.Id}} {{.Created}}\" qqchat-agent:latest", 15);
        var prevInfo = await RunDockerAsync("inspect --format \"{{.Id}} {{.Created}}\" qqchat-agent:prev", 15);

        var payload = new JsonObject
        {
            ["enabled"] = _settings.PanelDeployEnabled,
            ["url"] = _settings.PanelDeployUrl,
            ["dirReady"] = Directory.Exists(DeployDir),
            ["tarBytes"] = File.Exists(DeployTar) ? new FileInfo(DeployTar).Length : 0,
            ["tarUpdated"] = File.Exists(DeployTar) ? File.GetLastWriteTime(DeployTar).ToString("O") : null,
            ["containerStarted"] = containerStart.Ok ? containerStart.Output.Trim().Trim('"') : null,
            ["image"] = imageInfo.Ok ? imageInfo.Output.Trim() : null,
            ["previousImage"] = prevInfo.Ok ? prevInfo.Output.Trim() : null,
            ["docker"] = imageInfo.Ok ? "ok" : imageInfo.Output.Trim()
        };

        if (File.Exists(DeployLog))
        {
            payload["logTail"] = Tail(File.ReadAllText(DeployLog), 4000);
        }

        if (File.Exists(DeployExit))
        {
            payload["lastExit"] = File.ReadAllText(DeployExit).Trim();
        }

        return payload;
    }

    private static string Tail(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : "…（前略）\n" + text[^max..];

    // ───────────────────────── 上传 → 部署 ─────────────────────────

    private async Task PanelDeployUploadAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > DeployMaxBytes)
        {
            await WriteJsonAsync(context, 400, new JsonObject
            {
                ["error"] = $"太大：{context.Request.ContentLength64} 字节（上限 {DeployMaxBytes / 1024 / 1024}MB）"
            });
            return;
        }

        // 先落到系统临时目录并验 gzip 魔数（验不过就直接 400，不需要部署目录存在）
        var temp = Path.Combine(Path.GetTempPath(), $"qqchat-deploy-{Guid.NewGuid():N}.tar.gz");
        try
        {
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                var checkedMagic = false;
                int read;
                while ((read = await context.Request.InputStream.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > DeployMaxBytes)
                    {
                        await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "产物太大（超上限）" });
                        return;
                    }

                    if (!checkedMagic && total >= 2)
                    {
                        checkedMagic = true;
                        if (buffer[0] != 0x1f || buffer[1] != 0x8b)
                        {
                            await WriteJsonAsync(context, 400, new JsonObject
                            {
                                ["error"] = "这不是 gzip 包（app.tar.gz 应以 1f 8b 开头）：确认上传的是 dotnet publish 的产物包"
                            });
                            return;
                        }
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }

            if (new FileInfo(temp).Length < 1024)
            {
                await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "产物不像是 app.tar.gz（太小）" });
                return;
            }

            if (!Directory.Exists(DeployDir))
            {
                await WriteJsonAsync(context, 503, new JsonObject
                {
                    ["error"] = $"找不到部署目录 {DeployDir}（面板不在容器里，或挂载丢了）"
                });
                return;
            }

            File.Move(temp, DeployTarNew, overwrite: true);
            temp = string.Empty;
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = $"写入失败：{ex.Message}" });
            return;
        }
        finally
        {
            if (temp.Length > 0)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 删不掉就算了（临时文件）
                }
            }
        }

        FileLog.Write("部署", $"[部署] 收到新产物（{new FileInfo(DeployTarNew).Length} 字节），开始重建");
        var started = await StartDeployAsync(build: true);
        await WriteJsonAsync(context, started.Ok ? 200 : 500, started.Payload);
    }

    private async Task PanelDeployFromUrlAsync(HttpListenerContext context)
    {
        var body = await ReadJsonAsync(context);
        var url = (body?["url"]?.GetValue<string>() ?? _settings.PanelDeployUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "要一个 http(s) 的产物地址" });
            return;
        }

        try
        {
            using var response = await DeployHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                await WriteJsonAsync(context, 400, new JsonObject
                {
                    ["error"] = $"下载失败：HTTP {(int)response.StatusCode}"
                });
                return;
            }

            if (response.Content.Headers.ContentLength is long length && length > DeployMaxBytes)
            {
                await WriteJsonAsync(context, 400, new JsonObject
                {
                    ["error"] = $"产物太大：{length} 字节（上限 {DeployMaxBytes / 1024 / 1024}MB）"
                });
                return;
            }

            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = File.Create(DeployTarNew))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > DeployMaxBytes)
                    {
                        await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "产物太大（超上限）" });
                        return;
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(context, 500, new JsonObject { ["error"] = $"下载失败：{ex.GetType().Name} {ex.Message}" });
            return;
        }

        var check = ValidateArtifact();
        if (check is not null)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = check });
            return;
        }

        // 记住这个地址：下次点一下就能拉
        _agent.ApplyRuntimeSettings(s => s.PanelDeployUrl = url);
        FileLog.Write("部署", $"[部署] 从 {uri.Host} 拉到新产物（{new FileInfo(DeployTarNew).Length} 字节），开始重建");

        var started = await StartDeployAsync(build: true);
        await WriteJsonAsync(context, started.Ok ? 200 : 500, started.Payload);
    }

    private async Task PanelDeployRollbackAsync(HttpListenerContext context)
    {
        var prev = await RunDockerAsync("inspect --format \"{{.Id}}\" qqchat-agent:prev", 15);
        if (!prev.Ok || prev.Output.Trim().Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject { ["error"] = "没有上一个镜像可回滚（还没在这里部署过）" });
            return;
        }

        FileLog.Write("部署", "[部署] 回滚到上一个镜像（qqchat-agent:prev）");
        var started = await StartDeployAsync(build: false);
        await WriteJsonAsync(context, started.Ok ? 200 : 500, started.Payload);
    }

    /// <summary>产物合法性：gzip 魔数（app.tar.gz 必然是 gzip）。不合法就别拿去 build 了。</summary>
    private string? ValidateArtifact()
    {
        try
        {
            if (!File.Exists(DeployTarNew) || new FileInfo(DeployTarNew).Length < 1024)
            {
                return "产物不像是 app.tar.gz（太小）";
            }

            using var stream = File.OpenRead(DeployTarNew);
            var magic = new byte[2];
            if (stream.Read(magic, 0, 2) != 2 || magic[0] != 0x1f || magic[1] != 0x8b)
            {
                return "这不是 gzip 包（app.tar.gz 应该以 1f 8b 开头）：确认上传的是 dotnet publish 的产物包";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"产物校验失败：{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// 真正干活：备份旧产物 → 用新产物覆盖 → （可选）把当前镜像存成 prev →
    /// 起一个 **helper 容器**去 build + compose up（它会把我们这个容器换掉）。
    /// </summary>
    private async Task<(bool Ok, JsonObject Payload)> StartDeployAsync(bool build)
    {
        try
        {
            if (build)
            {
                if (File.Exists(DeployTar))
                {
                    File.Copy(DeployTar, DeployTarPrev, overwrite: true);
                }

                File.Move(DeployTarNew, DeployTar, overwrite: true);
            }

            // 回滚前先把当前 latest 留着（build 路径里也一样：万一新镜像起不来还能退回来）
            var tag = await RunDockerAsync("tag qqchat-agent:latest qqchat-agent:prev", 30);
            if (!tag.Ok)
            {
                // 第一次部署时可能没有 latest（理论上不会）——不致命，只记一笔
                FileLog.Write("部署", $"[部署] 标记 prev 失败（不致命）：{tag.Output.Trim()}");
            }

            var composeBase = "docker compose -p qqchat --project-directory /work -f /work/docker-compose.yml";
            var script = build
                ? "exec > /work/deploy.last.log 2>&1; echo \"== 构建 $(date -Is) ==\"; " +
                  "docker build -f Dockerfile.app -t qqchat-agent:latest /work && " +
                  composeBase + " up -d --no-deps qqchat; " +
                  "echo EXIT=$? > /work/deploy.last.exit; echo DONE >> /work/deploy.last.log"
                : "exec > /work/deploy.last.log 2>&1; echo \"== 回滚 $(date -Is) ==\"; " +
                  "docker tag qqchat-agent:prev qqchat-agent:latest && " +
                  composeBase + " up -d --no-deps qqchat; " +
                  "echo EXIT=$? > /work/deploy.last.exit; echo DONE >> /work/deploy.last.log";

            // helper 容器：它挂宿主 docker，负责把我们这个容器换掉（我们在自己肚子里跑 compose 是跑不成的）
            var run = await RunDockerAsync(
                "run --rm -d " +
                "-v /var/run/docker.sock:/var/run/docker.sock " +
                "-v /opt/qqchat:/work -w /work " +
                $"docker:cli sh -c {ShellQuote(script)}", 60);

            if (!run.Ok)
            {
                FileLog.Write("部署", $"[部署] helper 起不来：{run.Output.Trim()}");
                return (false, new JsonObject
                {
                    ["error"] = $"起不了部署容器（docker: {run.Output.Trim()})",
                    ["hint"] = "确认面板部署开关已开、且容器能访问 /var/run/docker.sock"
                });
            }

            FileLog.Write("部署", "[部署] helper 已启动（它会重建镜像并替换本容器；面板会短暂断开）");
            return (true, new JsonObject
            {
                ["started"] = true,
                ["helper"] = run.Output.Trim(),
                ["willDropConnection"] = true,
                ["note"] = "容器会被替换：页面会短暂断开，约 10–30 秒后自动回来（新容器起来后可以看部署日志）"
            });
        }
        catch (Exception ex)
        {
            FileLog.Write("部署", $"[部署] 失败：{ex.GetType().Name} {ex.Message}");
            return (false, new JsonObject { ["error"] = $"{ex.GetType().Name}: {ex.Message}" });
        }
    }

    // ───────────────────────── docker 调用 ─────────────────────────

    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";

    private static async Task<(bool Ok, string Output)> RunDockerAsync(string args, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var piece in SplitArgs(args))
        {
            psi.ArgumentList.Add(piece);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "docker 起不来");
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // 已经退了
                }

                return (false, $"docker 超时（{timeoutSeconds}s）");
            }

            var text = (await stdout) + (await stderr);
            return (process.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把参数串拆成 argv（认单/双引号；docker 的参数里空格很少，但 compose 的 sh -c 里有）。</summary>
    private static List<string> SplitArgs(string text)
    {
        var list = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        foreach (var ch in text)
        {
            if (quote != '\0')
            {
                if (ch == quote)
                {
                    quote = '\0';
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    list.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            list.Add(current.ToString());
        }

        return list;
    }
}
