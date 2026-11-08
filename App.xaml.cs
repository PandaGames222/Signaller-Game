using System.IO;
using System.Text.Json;
using System.Windows;
using Signaller.GameLogic;

namespace Signaller;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        if (e.Args.Length > 0)
        {
            if (!AppUpdateRequest.TryParse(e.Args, out var updateRequest) || updateRequest is null)
            {
                MessageBox.Show(
                    "The update request is invalid. Start Signaller normally and try the update again.",
                    "Signaller — Update error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
                return;
            }

            var updateWindow = new UpdateWindow(updateRequest);
            MainWindow = updateWindow;
            updateWindow.Show();
            return;
        }

        GameEngine engine;
        try
        {
            engine = new GameEngine(GameSaveStore.Load(GameStyle.Original), GameStyle.Original);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            MessageBox.Show(
                $"The saved game could not be loaded:\n{exception.Message}",
                "Signaller — Save error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(engine);
        MainWindow = window;
        window.Show();
    }
}
