using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Signaller.GameLogic;

namespace Signaller;

public partial class App : Application
{
    private async void Application_Startup(object sender, StartupEventArgs e)
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

        var loadingWindow = new StartupLoadingWindow();
        MainWindow = loadingWindow;
        loadingWindow.Show();

        try
        {
            await loadingWindow.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var engine = await Task.Run(() =>
                new GameEngine(GameSaveStore.Load(GameStyle.Original), GameStyle.Original));
            var window = new MainWindow(engine);
            MainWindow = window;
            window.Show();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            loadingWindow.Close();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            loadingWindow.Close();
            MessageBox.Show(
                $"The saved game could not be loaded:\n{exception.Message}",
                "Signaller — Save error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}

internal sealed class StartupLoadingWindow : Window
{
    public StartupLoadingWindow()
    {
        Title = "Signaller — Loading";
        Width = 390;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(13, 18, 24));
        Foreground = new SolidColorBrush(Color.FromRgb(231, 237, 243));
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = true;
        Content = new Border
        {
            Margin = new Thickness(1),
            Padding = new Thickness(30),
            Background = new SolidColorBrush(Color.FromRgb(18, 27, 34)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 65, 74)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "SIGNALLER",
                        Foreground = new SolidColorBrush(Color.FromRgb(115, 224, 176)),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold
                    },
                    new TextBlock
                    {
                        Text = "Loading your railway...",
                        Margin = new Thickness(0, 9, 0, 18),
                        FontSize = 20,
                        FontWeight = FontWeights.SemiBold
                    },
                    new ProgressBar
                    {
                        Height = 4,
                        IsIndeterminate = true,
                        Foreground = new SolidColorBrush(Color.FromRgb(115, 224, 176)),
                        Background = new SolidColorBrush(Color.FromRgb(38, 51, 59))
                    }
                }
            }
        };
    }
}
