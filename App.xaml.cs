using System.IO;
using System.Text.Json;
using System.Windows;
using Signaller.GameLogic;

namespace Signaller;

public partial class App : Application
{
    private void Application_Startup(object sender, StartupEventArgs e)
    {
        GameSaveData? save;
        try
        {
            save = GameSaveStore.Load();
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

        var window = new MainWindow(new GameEngine(save));
        MainWindow = window;
        window.Show();
    }
}
