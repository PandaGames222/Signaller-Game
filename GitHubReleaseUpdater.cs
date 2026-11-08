using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Signaller.GameLogic;

namespace Signaller;

internal sealed record AppRelease(Version Version, Uri InstallerUrl, string Sha256);

internal sealed class GitHubReleaseUpdater : IDisposable
{
    private const string ReleaseApiUrl =
        "https://api.github.com/repos/PandaGames222/Signaller-Game/releases/latest";
    private static string DownloadDirectory
    {
        get
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
                throw new InvalidOperationException("The Signaller update directory could not be determined.");
            return Path.Combine(localApplicationData, "Signaller", "updates");
        }
    }
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _client.GetAsync(ReleaseApiUrl, timeout.Token);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        var root = document.RootElement;
        if (!root.TryGetProperty("draft", out var draft)
            || !root.TryGetProperty("prerelease", out var prerelease)
            || !root.TryGetProperty("tag_name", out var tagElement)
            || !root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned an incomplete release record.");

        if (draft.GetBoolean() || prerelease.GetBoolean())
            return null;

        var tag = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tag)
            || !tag.StartsWith('v')
            || !Version.TryParse(tag.AsSpan(1), out var latest))
            throw new InvalidDataException("The latest GitHub release has an invalid version tag.");

        if (!Version.TryParse(VersionText(), out var currentVersion))
            throw new InvalidDataException("This application has an invalid version.");
        if (latest <= currentVersion) return null;

        var assetName = $"Signaller-Setup-{latest}.exe";
        var asset = assets
            .EnumerateArray()
            .Where(item => item.TryGetProperty("name", out var name) && name.GetString() == assetName)
            .Select(item => item)
            .FirstOrDefault();

        if (asset.ValueKind != JsonValueKind.Object
            || !asset.TryGetProperty("browser_download_url", out var downloadUrlElement)
            || !asset.TryGetProperty("digest", out var digestElement)
            || !Uri.TryCreate(downloadUrlElement.GetString(), UriKind.Absolute, out var installerUri)
            || !installerUri.AbsoluteUri.Equals(
                $"https://github.com/PandaGames222/Signaller-Game/releases/download/v{latest}/{assetName}",
                StringComparison.Ordinal)
            || !TryReadSha256(digestElement.GetString(), out var sha256))
            throw new InvalidDataException("The latest release does not contain a valid Signaller installer.");

        return new(latest, installerUri, sha256);
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateRequest request,
        IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DownloadDirectory);
        var destination = Path.Combine(DownloadDirectory, $"Signaller-Setup-{request.Version}.exe");
        var temporary = $"{destination}.{Guid.NewGuid():N}.download";

        try
        {
            using var response = await _client.GetAsync(
                request.InstallerUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is null or <= 0 or > 512L * 1024 * 1024)
                throw new InvalidDataException("The update installer has an invalid file size.");

            long downloaded = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                var previousProgress = -1;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    downloaded += read;
                    if (downloaded > totalBytes.Value)
                        throw new InvalidDataException("The update installer is larger than advertised.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    var percent = (int)Math.Min(100, downloaded * 100 / totalBytes.Value);
                    if (percent != previousProgress)
                    {
                        previousProgress = percent;
                        progress.Report(percent);
                    }
                }

                await target.FlushAsync(cancellationToken);
            }

            if (downloaded != totalBytes.Value)
                throw new InvalidDataException("The update installer download was incomplete.");
            byte[] actualHash;
            await using (var installerStream = File.OpenRead(temporary))
                actualHash = await SHA256.HashDataAsync(installerStream, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                actualHash,
                Convert.FromHexString(request.Sha256)))
                throw new InvalidDataException("The update installer failed its SHA-256 integrity check.");

            File.Move(temporary, destination, overwrite: true);
            progress.Report(100);
            return destination;
        }
        catch (Exception exception)
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (IOException cleanupException)
            {
                exception.Data["InstallerCleanupError"] = cleanupException.Message;
            }
            catch (UnauthorizedAccessException cleanupException)
            {
                exception.Data["InstallerCleanupError"] = cleanupException.Message;
            }
            throw;
        }
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

    private static bool TryReadSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.Ordinal)) return false;
        var value = digest["sha256:".Length..];
        if (value.Length != 64 || !value.All(Uri.IsHexDigit)) return false;
        sha256 = value;
        return true;
    }
}
