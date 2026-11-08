using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Windows;
using Signaller.GameLogic;

namespace Signaller;

public partial class UpdateWindow : Window
{
    private readonly AppUpdateRequest _request;
    private readonly GitHubReleaseUpdater _updater = new();
    private bool _operationInProgress;

    public UpdateWindow(AppUpdateRequest request)
    {
        InitializeComponent();
        _request = request;
        VersionBadge.Text = $"VERSION {_request.Version}";
        Closing += UpdateWindow_Closing;
        Loaded += async (_, _) => await ApplyUpdateAsync();
    }

    private async Task ApplyUpdateAsync()
    {
        if (_operationInProgress) return;
        _operationInProgress = true;
        RecoveryButtons.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateProgress.IsIndeterminate = true;
        UpdateProgress.Value = 0;
        HeadingText.Text = $"Installing version {_request.Version}";
        FooterText.Text = "PLEASE KEEP THIS WINDOW OPEN";

        try
        {
            StatusText.Text = "Waiting for Signaller to close safely…";
            await WaitForParentExitAsync(_request.ParentProcessId);

            StatusText.Text = "Downloading and verifying the update…";
            UpdateProgress.IsIndeterminate = false;
            var progress = new Progress<int>(value =>
            {
                UpdateProgress.Value = value;
                StatusText.Text = $"Downloading and verifying the update… {value}%";
            });
            var installerPath = await _updater.DownloadInstallerAsync(
                _request,
                progress,
                CancellationToken.None);
            if (!File.Exists(installerPath))
                throw new FileNotFoundException("The downloaded Signaller installer could not be found.", installerPath);

            StatusText.Text = "Installing Signaller. Your saved game is safe.";
            UpdateProgress.IsIndeterminate = true;
            var installerDirectory = Path.GetDirectoryName(installerPath);
            if (string.IsNullOrWhiteSpace(installerDirectory) || !Directory.Exists(installerDirectory))
                throw new DirectoryNotFoundException("The downloaded Signaller installer directory is unavailable.");
            var installer = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS",
                UseShellExecute = false,
                WorkingDirectory = installerDirectory
            }) ?? throw new InvalidOperationException("The Signaller installer could not be started.");
            await installer.WaitForExitAsync();
            if (installer.ExitCode != 0)
                throw new InvalidOperationException(
                    $"The installer exited with code {installer.ExitCode}. You can reopen Signaller and retry.");

            StatusText.Text = "Update complete. Starting Signaller…";
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Value = 100;
            StartInstalledGame();
            _operationInProgress = false;
            Close();
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or Win32Exception or TaskCanceledException or SecurityException)
        {
            ShowRecoveryOptions(exception);
        }
        finally
        {
            _operationInProgress = false;
        }
    }

    private void ShowRecoveryOptions(Exception exception)
    {
        HeadingText.Text = "The update could not be completed";
        StatusText.Text = "The installer could not confirm that the update completed.";
        ErrorText.Text = exception.Message;
        ErrorText.Visibility = Visibility.Visible;
        DetailsText.Text = "Retry the update, or open the version currently installed.";
        UpdateProgress.IsIndeterminate = false;
        UpdateProgress.Value = 0;
        RecoveryButtons.Visibility = Visibility.Visible;
        FooterText.Text = "YOUR SAVED GAME HAS NOT BEEN DELETED";
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await ApplyUpdateAsync();
    }

    private void OpenSignallerButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartInstalledGame();
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or IOException or UnauthorizedAccessException)
        {
            ShowRecoveryOptions(exception);
        }
    }

    private void UpdateWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_operationInProgress)
        {
            e.Cancel = true;
            StatusText.Text = "The update is in progress. Please wait until it finishes.";
            return;
        }

        _updater.Dispose();
    }

    private static async Task WaitForParentExitAsync(int processId)
    {
        try
        {
            using var parent = Process.GetProcessById(processId);
            await parent.WaitForExitAsync();
        }
        catch (ArgumentException)
        {
        }
    }

    private void StartInstalledGame()
    {
        var executable = _request.GameExecutablePath;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The installed Signaller application could not be found.", executable);

        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
                ?? throw new InvalidOperationException("The Signaller installation directory could not be determined.")
        });
        if (process is null)
            throw new InvalidOperationException("The installed Signaller application could not be started.");
        process.Dispose();
    }
}
