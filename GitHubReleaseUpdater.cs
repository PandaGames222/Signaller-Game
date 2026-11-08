using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Signaller;

internal sealed record AppRelease(Version Version, Uri InstallerUrl);

internal sealed class GitHubReleaseUpdater : IDisposable
{
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/PandaGames222/Signaller-Game/releases/latest";
    private static readonly Uri ExpectedAssetOrigin = new("https://github.com");
    private static readonly string DownloadDirectory = Path.Combine(
        Path.GetTempPath(), "Signaller", "updates");
    private readonly HttpClient _client = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    public GitHubReleaseUpdater()
    {
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Signaller", VersionText()));
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<AppRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(ReleaseApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            return null;

        var tag = root.GetProperty("tag_name").GetString();
        if (string.IsNullOrWhiteSpace(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");

        if (!Version.TryParse(VersionText(), out var currentVersion))
            throw new InvalidDataException("This application has an invalid version.");
        if (latest <= currentVersion) return null;

        var assetName = $"Signaller-Setup-{latest}.exe";
        var downloadUrl = root
            .GetProperty("assets")
            .EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == assetName)
            .Select(asset => asset.GetProperty("browser_download_url").GetString())
            .FirstOrDefault();

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var installerUri)
            || installerUri.Scheme != Uri.UriSchemeHttps
            || installerUri.IdnHost != ExpectedAssetOrigin.IdnHost
            || !installerUri.AbsolutePath.StartsWith(
                $"/PandaGames222/Signaller-Game/releases/download/v{latest}/",
                StringComparison.Ordinal))
            throw new InvalidDataException("The latest release does not contain a valid Signaller installer.");

        return new(latest, installerUri);
    }

    public async Task<string> DownloadInstallerAsync(
        AppRelease release,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var destination = Path.Combine(DownloadDirectory, $"Signaller-Setup-{release.Version}.exe");
        var temporary = $"{destination}.{Guid.NewGuid():N}.download";

        try
        {
            using var response = await _client.GetAsync(
                release.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true);
            var buffer = new byte[64 * 1024];
            long downloaded = 0;
            int previousProgress = -1;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;

                if (totalBytes is > 0)
                {
                    var percent = (int)Math.Min(100, downloaded * 100 / totalBytes.Value);
                    if (percent != previousProgress)
                    {
                        previousProgress = percent;
                        progress.Report(percent);
                    }
                }
            }

            await target.FlushAsync(cancellationToken);
            if (downloaded < 1024 * 1024)
                throw new InvalidDataException("The downloaded installer is unexpectedly small.");

            File.Move(temporary, destination, overwrite: true);
            progress.Report(100);
            return destination;
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("The downloaded Signaller installer was not found.", installerPath);

        Process.Start(new ProcessStartInfo(installerPath)
        {
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private static string VersionText()
    {
        return typeof(GitHubReleaseUpdater).Assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("The app version could not be read.");
    }
}
