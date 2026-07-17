using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hope.Desktop.Services;

/// <summary>一次版本检测的结果：最新版本号、tag、更新说明，以及安装包 / 校验文件的候选下载地址。</summary>
public sealed record UpdateInfo(
    Version LatestVersion,
    string Tag,
    string Notes,
    IReadOnlyList<string> InstallerUrls,
    IReadOnlyList<string> Sha256Urls,
    string Source);

/// <summary>
/// 全量更新服务：检测最新版本并下载安装包，校验 SHA-256，再静默就地升级。
/// 数据源优先 GitHub（API / 网页重定向），并以 Gitee（gitee.com/CooloiStudio/Hope）的 Release 作为大陆兜底。
/// </summary>
public sealed class UpdateService
{
    private const string Owner = "CooloiStudio";
    private const string Repo = "Hope";
    private const string AssetName = "Hope_Setup.exe";
    private const string Sha256Name = AssetName + ".sha256";
    // 单个 URL 网络操作的尝试次数（首次 + 重试）；用于缓解大陆网络下 GitHub CDN 的瞬时连接失败。
    private const int NetworkAttempts = 3;

    private static readonly object HttpGate = new();
    private static HttpClient? _http;
    private static HttpClient? _httpNoRedirect;

    private static HttpClient Http
    {
        get { lock (HttpGate) { return _http ??= CreateClient(autoRedirect: true); } }
    }

    private static HttpClient HttpNoRedirect
    {
        get { lock (HttpGate) { return _httpNoRedirect ??= CreateClient(autoRedirect: false); } }
    }

    private static HttpClient CreateClient(bool autoRedirect)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = autoRedirect };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Hope-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// 丢弃长寿命 HttpClient（休眠唤醒后系统网络栈常处于半开状态，复用旧客户端易触发异常甚至运行时故障）。
    /// 进行中的请求可能失败；调用方应在冷却后再检查更新。
    /// </summary>
    public static void InvalidateHttpClients()
    {
        lock (HttpGate)
        {
            try { _http?.Dispose(); } catch { /* ignore */ }
            try { _httpNoRedirect?.Dispose(); } catch { /* ignore */ }
            _http = null;
            _httpNoRedirect = null;
        }
        DesktopLog.Info("UpdateService: HttpClient invalidated");
    }

    /// <summary>
    /// 单个 URL 网络操作的重试包装：仅对「非主动取消」的瞬时异常（连接重置/超时/TLS 抖动等）重试，
    /// 真取消立即抛出。重试间隔指数退避（0.8s / 1.6s …）。注意：明确的非 2xx 响应应由 <paramref name="op"/>
    /// 自行返回（如 null）而非抛异常，以免对确定性的 404 做无谓重试。
    /// </summary>
    private static async Task<T> WithRetryAsync<T>(Func<Task<T>> op, string what, CancellationToken ct, int attempts = NetworkAttempts)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return await op().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                DesktopLog.Warn($"UpdateService: {what} 第 {attempt}/{attempts} 次失败：{ex.Message}");
                if (attempt < attempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt), ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException($"{what} 失败");
    }

    private static Task WithRetryAsync(Func<Task> op, string what, CancellationToken ct, int attempts = NetworkAttempts) =>
        WithRetryAsync(async () => { await op().ConfigureAwait(false); return true; }, what, ct, attempts);

    /// <summary>当前桌面端版本（取程序集 Major.Minor.Build）。</summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, v.Build);
        }
    }

    /// <summary>
    /// 检测最新版本。依次尝试：①GitHub API ②GitHub 网页 302 重定向 ③Gitee API（大陆兜底）。
    /// 任一通道成功即返回，并尽量补齐 Gitee 下载地址作为下载兜底；全部失败返回 null。
    /// </summary>
    public async Task<UpdateInfo?> CheckLatestAsync(CancellationToken ct)
    {
        try
        {
            UpdateInfo? primary = null;
            foreach (var strategy in new Func<CancellationToken, Task<UpdateInfo?>>[]
                     {
                         TryGitHubApiAsync,
                         TryGitHubWebRedirectAsync,
                         TryGiteeApiAsync,
                     })
            {
                try
                {
                    primary = await strategy(ct).ConfigureAwait(false);
                    if (primary != null)
                    {
                        DesktopLog.Info($"UpdateService: latest={primary.LatestVersion} via {primary.Source}");
                        break;
                    }
                }
                // 仅当我们的 token 真被取消才中止；HttpClient.Timeout 抛的 TaskCanceledException
                // （ct 未取消）按普通失败处理，继续尝试下一通道（如 Gitee），否则一个超时就会废掉兜底。
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { DesktopLog.Warn($"UpdateService strategy failed: {ex.Message}"); }
            }

            if (primary == null)
            {
                DesktopLog.Warn("UpdateService: all version-check strategies failed");
                return null;
            }

            return await WithGiteeFallbackAsync(primary, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // 休眠唤醒后偶发极端故障：丢弃 HttpClient，避免后续继续复用坏连接。
            DesktopLog.Error("UpdateService.CheckLatestAsync failed", ex);
            InvalidateHttpClients();
            return null;
        }
    }

    // 通道①：GitHub API releases/latest（信息最全：tag + body + 资产直链）。
    private async Task<UpdateInfo?> TryGitHubApiAsync(CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tg) ? tg.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || !TryParseTag(tag, out var ver)) return null;

        var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        var (installer, sha) = ExtractAssets(root, BuildGitHubDownloadUrl(tag, AssetName), BuildGitHubDownloadUrl(tag, Sha256Name));

        return new UpdateInfo(ver, tag, notes.Trim(), new[] { installer }, new[] { sha }, "github-api");
    }

    // 通道②：GitHub 网页 releases/latest 的 302 重定向，从 Location 解析 tag（API 被墙但网页可达时）。
    private async Task<UpdateInfo?> TryGitHubWebRedirectAsync(CancellationToken ct)
    {
        var url = $"https://github.com/{Owner}/{Repo}/releases/latest";
        using var resp = await HttpNoRedirect.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 期望 302 重定向到 /releases/tag/vX.Y.Z；从 Location 解析 tag。
        string? location = resp.Headers.Location?.ToString();
        if (string.IsNullOrWhiteSpace(location))
            location = resp.RequestMessage?.RequestUri?.ToString();
        if (string.IsNullOrWhiteSpace(location)) return null;

        var m = Regex.Match(location, @"/releases/tag/(v?\d+\.\d+\.\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var tag = m.Groups[1].Value;
        if (!TryParseTag(tag, out var ver)) return null;

        return new UpdateInfo(ver, tag, "",
            new[] { BuildGitHubDownloadUrl(tag, AssetName) },
            new[] { BuildGitHubDownloadUrl(tag, Sha256Name) },
            "github-web");
    }

    // 通道③：Gitee API releases/latest（大陆可达兜底；要求 Gitee 同名仓库已同步对应 Release 与资产）。
    private async Task<UpdateInfo?> TryGiteeApiAsync(CancellationToken ct)
    {
        var (ver, tag, notes, installer, sha) = await FetchGiteeLatestAsync(ct).ConfigureAwait(false);
        if (ver == null || tag == null) return null;

        var installers = new List<string>();
        if (installer != null) installers.Add(installer);
        installers.Add(BuildGitHubDownloadUrl(tag, AssetName)); // GitHub 作为次选

        var shas = new List<string>();
        if (sha != null) shas.Add(sha);
        shas.Add(BuildGitHubDownloadUrl(tag, Sha256Name));

        return new UpdateInfo(ver, tag, notes, installers, shas, "gitee-api");
    }

    // 当主通道来自 GitHub 时，best-effort 追加 Gitee 资产地址作为下载兜底。
    private async Task<UpdateInfo> WithGiteeFallbackAsync(UpdateInfo primary, CancellationToken ct)
    {
        if (primary.Source.StartsWith("gitee")) return primary;

        try
        {
            var (ver, tag, _, installer, sha) = await FetchGiteeLatestAsync(ct).ConfigureAwait(false);
            // 仅当 Gitee 最新版与主通道一致时才追加，避免版本错配。
            if (ver != null && ver == primary.LatestVersion)
            {
                var installers = new List<string>(primary.InstallerUrls);
                var shas = new List<string>(primary.Sha256Urls);
                if (installer != null && !installers.Contains(installer)) installers.Add(installer);
                if (sha != null && !shas.Contains(sha)) shas.Add(sha);
                return primary with { InstallerUrls = installers, Sha256Urls = shas };
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { DesktopLog.Warn($"UpdateService: gitee fallback resolve failed: {ex.Message}"); }

        return primary;
    }

    // 调 Gitee API 取最新发布的 版本/tag/说明/安装包URL/校验URL（任一缺失返回 null 对应项）。
    private async Task<(Version?, string?, string, string?, string?)> FetchGiteeLatestAsync(CancellationToken ct)
    {
        var url = $"https://gitee.com/api/v5/repos/{Owner}/{Repo}/releases/latest";
        using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, null, "", null, null);

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = root.TryGetProperty("tag_name", out var tg) ? tg.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || !TryParseTag(tag, out var ver)) return (null, null, "", null, null);

        var notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "";

        string? installer = null, sha = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (string.IsNullOrEmpty(dl)) continue;
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                name ??= LastPathSegment(dl);
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase) ||
                    dl.EndsWith("/" + AssetName, StringComparison.OrdinalIgnoreCase)) installer = dl;
                else if (name.Equals(Sha256Name, StringComparison.OrdinalIgnoreCase) ||
                         dl.EndsWith("/" + Sha256Name, StringComparison.OrdinalIgnoreCase)) sha = dl;
            }
        }
        return (ver, tag, notes, installer, sha);
    }

    // 从 GitHub release JSON 的 assets 中提取安装包与校验文件直链，缺失时回退到约定 URL。
    private static (string installer, string sha) ExtractAssets(JsonElement root, string fallbackInstaller, string fallbackSha)
    {
        string? installer = null, sha = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var dl = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                if (name == null || dl == null) continue;
                if (name.Equals(AssetName, StringComparison.OrdinalIgnoreCase)) installer = dl;
                else if (name.Equals(Sha256Name, StringComparison.OrdinalIgnoreCase)) sha = dl;
            }
        }
        return (installer ?? fallbackInstaller, sha ?? fallbackSha);
    }

    /// <summary>
    /// 下载安装包到本地（按候选地址顺序兜底 + SHA-256 校验）。成功返回安装包本地路径，失败抛异常。
    /// </summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Hope", "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"Hope_Setup_{info.Tag}.exe");

        string? expectedSha = await TryFetchSha256Async(info.Sha256Urls, ct).ConfigureAwait(false);

        // 复用本地缓存：若已存在该版本安装包（如上次会话已下载但未安装），用 SHA-256 校验，
        // 通过则直接复用、跳过下载；校验不通过或无法校验则删除后重新下载，避免使用损坏/半截文件。
        if (File.Exists(target))
        {
            if (expectedSha != null && FileSha256.Matches(target, expectedSha))
            {
                DesktopLog.Info($"UpdateService: 复用本地已校验缓存安装包 {target}");
                return target;
            }
            DesktopLog.Warn($"UpdateService: 本地缓存安装包校验不通过/无法校验，删除后重新下载：{target}");
            try { File.Delete(target); } catch (Exception ex) { DesktopLog.Warn($"UpdateService: 删除缓存失败 {ex.Message}"); }
        }

        Exception? last = null;
        foreach (var url in info.InstallerUrls)
        {
            try
            {
                var tmp = target + ".part";
                await WithRetryAsync(() => DownloadToFileAsync(url, tmp, progress, ct), $"下载安装包 {url}", ct).ConfigureAwait(false);

                if (expectedSha != null)
                {
                    if (!FileSha256.Matches(tmp, expectedSha))
                    {
                        File.Delete(tmp);
                        last = new InvalidOperationException($"SHA-256 校验不通过（{url}）");
                        DesktopLog.Warn($"UpdateService: sha256 mismatch from {url}");
                        continue;
                    }
                    DesktopLog.Info("UpdateService: sha256 verified");
                }
                else if (new FileInfo(tmp).Length < 512 * 1024)
                {
                    File.Delete(tmp);
                    last = new InvalidOperationException($"下载内容过小，疑似无效（{url}）");
                    continue;
                }

                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
                DesktopLog.Info($"UpdateService: downloaded installer to {target} via {url}");
                return target;
            }
            // 仅真取消才中止；超时（HttpClient.Timeout→TaskCanceledException，ct 未取消）当作该通道失败，
            // 记录后继续尝试下一候选地址（GitHub→Gitee），不让一次超时废掉整个兜底。
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                DesktopLog.Warn($"UpdateService: download failed from {url}: {ex.Message}");
            }
        }
        throw last ?? new InvalidOperationException("所有下载通道均失败");
    }

    private static async Task DownloadToFileAsync(string url, string path, IProgress<double>? progress, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0) progress?.Report((double)read / total.Value);
        }
    }

    private async Task<string?> TryFetchSha256Async(IReadOnlyList<string> urls, CancellationToken ct)
    {
        foreach (var url in urls)
        {
            try
            {
                var hash = await WithRetryAsync<string?>(async () =>
                {
                    using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) return null; // 明确响应：此 URL 无该文件，不重试，换下一通道
                    var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var m = Regex.Match(text, @"\b([A-Fa-f0-9]{64})\b");
                    return m.Success ? m.Groups[1].Value : null;
                }, $"获取 sha256 {url}", ct).ConfigureAwait(false);
                if (hash != null) return hash;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { DesktopLog.Warn($"UpdateService: fetch sha256 failed {url}: {ex.Message}"); }
        }
        DesktopLog.Warn("UpdateService: no sha256 available, will fall back to size check");
        return null;
    }

    /// <summary>
    /// 启动安装包静默就地升级，并在安装完成后重新拉起桌面端，随后退出当前进程。
    /// 通过 cmd 串联「安装 → 重新启动」，不依赖安装器的重启管理器，最稳妥。
    /// </summary>
    public static void LaunchInstallerAndExit(string installerPath, Action quit)
    {
        var exePath = Environment.ProcessPath
                      ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

        // /SILENT 显示进度但无交互；/CLOSEAPPLICATIONS 配合 setup.iss 的 AppMutex 关闭运行中的实例。
        // 重启交由下面的 cmd `start` 负责（setup.iss 设 RestartApplications=no，避免重复拉起）。
        var innoArgs = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";
        var cmdArgs = $"/c \"\"{installerPath}\" {innoArgs} && start \"\" \"{exePath}\"\"";

        DesktopLog.Info($"UpdateService: launching installer {installerPath}");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = cmdArgs,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        quit();
    }

    private static string BuildGitHubDownloadUrl(string tag, string asset) =>
        $"https://github.com/{Owner}/{Repo}/releases/download/{tag}/{asset}";

    private static string LastPathSegment(string url)
    {
        var i = url.LastIndexOf('/');
        return i >= 0 && i < url.Length - 1 ? url[(i + 1)..] : url;
    }

    private static bool TryParseTag(string tag, out Version version)
    {
        var t = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(t, out version!);
    }
}
