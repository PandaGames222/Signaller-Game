namespace Signaller.GameLogic;

public sealed record AppUpdateRequest(
    Version Version,
    Uri InstallerUrl,
    string Sha256,
    int ParentProcessId,
    string GameExecutablePath)
{
    private const string ExpectedDownloadPathPrefix =
        "/PandaGames222/Signaller-Game/releases/download/v";

    public static bool TryParse(string[] arguments, out AppUpdateRequest? request)
    {
        request = null;
        if (arguments.Length == 0 || arguments[0] != "--apply-update") return false;
        if (arguments.Length != 6
            || !Version.TryParse(arguments[1], out var version)
            || !Uri.TryCreate(arguments[2], UriKind.Absolute, out var installerUri)
            || !int.TryParse(arguments[4], out var parentProcessId)
            || parentProcessId <= 0
            || !Path.IsPathFullyQualified(arguments[5])
            || !string.Equals(
                Path.GetFileName(arguments[5]),
                "Signaller.exe",
                StringComparison.OrdinalIgnoreCase)
            || arguments[3].Length != 64
            || !arguments[3].All(Uri.IsHexDigit)
            || installerUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(installerUri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                installerUri.AbsolutePath,
                $"{ExpectedDownloadPathPrefix}{version}/Signaller-Setup-{version}.exe",
                StringComparison.Ordinal))
            return false;

        request = new(version, installerUri, arguments[3], parentProcessId, arguments[5]);
        return true;
    }

    public string[] ToArguments()
    {
        return
        [
            "--apply-update",
            Version.ToString(),
            InstallerUrl.AbsoluteUri,
            Sha256,
            ParentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GameExecutablePath
        ];
    }
}
