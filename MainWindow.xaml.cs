using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Signaller.GameLogic;
using WpfPoint = System.Windows.Point;

namespace Signaller;

public partial class MainWindow : Window
{
    private readonly GameEngine _engine;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly DispatcherTimer _updaterCleanupTimer;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly GitHubReleaseUpdater _updater = new();
    private readonly CancellationTokenSource _windowLifetime = new();
    private double _lastTickSeconds;
    private double _lastSaveSeconds;
    private bool _saveFailed;
    private bool _updateRequestInProgress;
    private AppRelease? _availableUpdate;

    public MainWindow(GameEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        Railway.Engine = _engine;

        FooterVersion.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.2.0";
        VersionText.Text = $"VERSION {FooterVersion.Text}";

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += Timer_Tick;
        _timer.Start();

        _updateCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _updateCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(_windowLifetime.Token);
        _updateCheckTimer.Start();
        _updaterCleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _updaterCleanupTimer.Tick += (_, _) => CleanupUpdaterCopies();

        RefreshBuildList();
        RefreshHud();
        ApplyMode();
        Closing += MainWindow_Closing;
        Loaded += async (_, _) => await CheckForUpdatesAsync(_windowLifetime.Token);
        Loaded += (_, _) => _updaterCleanupTimer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = _stopwatch.Elapsed.TotalSeconds;
        var delta = now - _lastTickSeconds;
        _lastTickSeconds = now;
        if (delta <= 0) return;

        var previousDistance = _engine.TrainDistance;
        var previousStopped = _engine.TrainStopped;
        _engine.Advance(delta);
        if (previousDistance != _engine.TrainDistance || previousStopped != _engine.TrainStopped)
            Railway.InvalidateVisual();
        RefreshHud();

        if (now - _lastSaveSeconds >= 30)
        {
            SaveGame();
            _lastSaveSeconds = now;
        }
        DisplayLatestNotification();
    }

    private void RefreshHud()
    {
        BalanceText.Text = _engine.FormattedBalance;
        IncomeText.Text = $"${_engine.IncomePerMinute:N0} / min";
        TrainStatusText.Text = _engine.TrainStopped ? "01  HELD" : "01  IN SERVICE";
        TrainStatusText.ToolTip = _engine.TrainStatus;
        ClockText.Text = _engine.FormattedGameClock;
        TrainAlertText.Text = _engine.TrainStatus;
        TrainAlertText.Visibility = _engine.TrainStopped ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMode()
    {
        var editor = _engine.Mode == GameMode.Editor;
        SignallerModeButton.IsChecked = !editor;
        EditorModeButton.IsChecked = editor;
        EditorPanel.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        EditorColumn.Width = editor ? new GridLength(252) : new GridLength(0);
        ModeEyebrow.Text = editor ? "NETWORK EXPANSION" : "NETWORK OPERATIONS";
        ModeTitle.Text = editor ? "Network editor" : "Signaller desk";
        MapSubtitle.Text = editor ? "GRID BUILD MODE" : "SIGNALLING DIAGRAM";
        CanvasHint.Text = editor
            ? "SELECT AN ITEM TO BUILD  ·  PLACE ON THE GRID  ·  DEMOLISH FOR A 50% REFUND"
            : "CLICK A SIGNAL TO CHANGE ITS ASPECT  ·  SET POINTS FOR THE MAIN LINE";
        DemolishButton.Background = _engine.DemolishMode
            ? new SolidColorBrush(Color.FromRgb(54, 35, 38))
            : new SolidColorBrush(Color.FromRgb(25, 35, 45));
        RefreshBuildListSelection();
        Railway.InvalidateVisual();
    }

    private void RefreshBuildList()
    {
        BuildList.Children.Clear();
        foreach (var item in GameEngine.BuildItems)
        {
            var label = new TextBlock
            {
                Text = item.Name,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(231, 237, 243))
            };
            var detail = new TextBlock
            {
                Text = $"${item.Cost:N0}  ·  CLICK TO PLACE",
                Margin = new Thickness(0, 3, 0, 0),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(130, 144, 157))
            };
            var icon = new Border
            {
                Width = 29,
                Height = 29,
                Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromRgb(34, 47, 56)),
                Child = new TextBlock
                {
                    Text = item.Symbol,
                    Foreground = new SolidColorBrush(Color.FromRgb(115, 224, 176)),
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    icon,
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children = { label, detail }
                    }
                }
            };
            var button = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Height = 50,
                Margin = new Thickness(0, 0, 0, 7),
                Tag = item.Kind,
                ToolTip = $"Build {item.Name} for ${item.Cost:N0}."
            };
            button.Click += BuildItemButton_Click;
            BuildList.Children.Add(button);
        }

        RefreshBuildListSelection();
    }

    private void RefreshBuildListSelection()
    {
        foreach (var child in BuildList.Children)
        {
            if (child is not Button button) continue;
            var isSelected = (BuildItemKind)button.Tag == _engine.SelectedBuildItem
                && !_engine.DemolishMode;
            button.BorderBrush = new SolidColorBrush(
                isSelected ? Color.FromRgb(115, 224, 176) : Color.FromRgb(41, 53, 65));
            button.Background = new SolidColorBrush(
                isSelected ? Color.FromRgb(32, 54, 47) : Color.FromRgb(25, 35, 45));
        }
    }

    private void BuildItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildItemKind kind }) return;
        _engine.SelectBuildItem(kind);
        ApplyMode();
        DisplayLatestNotification($"Selected {GameEngine.BuildItems.Single(item => item.Kind == kind).Name}.");
    }

    private void SignallerModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_engine is null || !IsInitialized) return;
        _engine.SetMode(GameMode.Signaller);
        ApplyMode();
    }

    private void EditorModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (_engine is null || !IsInitialized) return;
        _engine.SetMode(GameMode.Editor);
        ApplyMode();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.TogglePause();
        PauseButton.Content = _engine.IsPaused ? "▶" : "Ⅱ";
        PauseButton.ToolTip = _engine.IsPaused ? "Resume the game simulation" : "Pause the game simulation";
        SaveGame();
        DisplayLatestNotification();
    }

    private void DemolishButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.ToggleDemolishMode();
        ApplyMode();
        DisplayLatestNotification(_engine.DemolishMode
            ? "Demolish mode — click one of your placed pieces."
            : "Build mode.");
    }

    private void Railway_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var position = Railway.MouseToWorld(e.GetPosition(Railway));
        if (position is null) return;
        var point = position.Value;

        if (_engine.Mode == GameMode.Editor)
        {
            var changed = _engine.DemolishMode
                ? _engine.DemolishNearest(point.X, point.Y)
                : _engine.PlaceSelectedItem(point.X, point.Y);
            if (changed) SaveGame();
            DisplayLatestNotification();
            RefreshHud();
            Railway.InvalidateVisual();
            return;
        }

        var signal = _engine.Signals.FirstOrDefault(signal =>
        {
            var marker = _engine.SignalPosition(signal);
            return DistanceSquared(marker.X, marker.Y, point.X, point.Y) < 30 * 30;
        });
        if (signal is not null)
        {
            _engine.ToggleSignal(signal.Id);
            SaveGame();
            DisplayLatestNotification();
            Railway.InvalidateVisual();
            return;
        }

        var points = _engine.Switches.FirstOrDefault(points =>
        {
            var marker = _engine.SwitchPosition(points);
            return DistanceSquared(marker.X, marker.Y, point.X, point.Y) < 30 * 30;
        });
        if (points is not null)
        {
            _engine.ToggleSwitch(points.Id);
            SaveGame();
            DisplayLatestNotification();
            Railway.InvalidateVisual();
        }
    }

    private void Railway_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_engine.Mode != GameMode.Editor || Railway.MouseToWorld(e.GetPosition(Railway)) is not { } point)
            return;

        _engine.SetPreview(point.X, point.Y);
        Railway.InvalidateVisual();
    }

    private void Railway_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _engine.ClearPreview();
        Railway.InvalidateVisual();
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateRequestInProgress) return;
        if (_availableUpdate is null)
        {
            await CheckForUpdatesAsync(_windowLifetime.Token);
            return;
        }

        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
                throw new FileNotFoundException("The Signaller application could not be located.", executable);

            var updaterDirectory = Path.Combine(Path.GetTempPath(), "Signaller", "updater");
            Directory.CreateDirectory(updaterDirectory);
            var updaterExecutable = Path.Combine(
                updaterDirectory,
                $"Signaller-Updater-{Guid.NewGuid():N}.exe");
            File.Copy(executable, updaterExecutable);

            var updateRequest = new AppUpdateRequest(
                _availableUpdate.Version,
                _availableUpdate.InstallerUrl,
                _availableUpdate.Sha256,
                Environment.ProcessId,
                executable);
            var updateProcess = new ProcessStartInfo(updaterExecutable)
            {
                UseShellExecute = true,
                WorkingDirectory = updaterDirectory
            };
            foreach (var argument in updateRequest.ToArguments())
                updateProcess.ArgumentList.Add(argument);

            if (Process.Start(updateProcess) is null)
                throw new InvalidOperationException("The Signaller update screen could not be started.");
            Application.Current.Shutdown();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException or SecurityException)
        {
            MessageBox.Show(
                exception.Message,
                "Signaller update could not be started",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (_updateRequestInProgress) return;
        _updateRequestInProgress = true;
        UpdateButton.Content = "SEARCHING…";
        UpdateButton.ToolTip = "Checking GitHub for the latest Signaller release";

        try
        {
            var release = await _updater.CheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _availableUpdate = release;
            if (release is null)
            {
                UpdateButton.Content = "UP TO DATE";
                UpdateButton.ToolTip = "Click to check for updates now";
                DisplayLatestNotification("You have the latest version of Signaller.");
            }
            else
            {
                UpdateButton.Content = $"UPDATE · {release.Version}";
                UpdateButton.ToolTip = $"Download and install Signaller {release.Version}";
                DisplayLatestNotification($"Signaller {release.Version} is available.");
            }
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException
            or InvalidDataException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) return;
            _availableUpdate = null;
            UpdateButton.Content = "CHECK FAILED";
            UpdateButton.ToolTip = "Could not check for updates. Click to try again.";
            DisplayLatestNotification($"Could not check for updates: {exception.Message}");
        }
        finally
        {
            _updateRequestInProgress = false;
            if (!cancellationToken.IsCancellationRequested)
                UpdateButton.IsEnabled = true;
        }
    }

    private void SaveGame()
    {
        try
        {
            GameSaveStore.Save(_engine.Save());
            _lastSaveSeconds = _stopwatch.Elapsed.TotalSeconds;
            _saveFailed = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_saveFailed) return;
            _saveFailed = true;
            DisplayLatestNotification($"Could not save your game: {exception.Message}");
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _windowLifetime.Cancel();
        SaveGame();
        _timer.Stop();
        _updateCheckTimer.Stop();
        _updaterCleanupTimer.Stop();
        _updater.Dispose();
        _windowLifetime.Dispose();
    }

    private void CleanupUpdaterCopies()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Signaller", "updater");
        if (!Directory.Exists(directory))
        {
            _updaterCleanupTimer.Stop();
            return;
        }

        try
        {
            string? cleanupError = null;
            foreach (var file in Directory.EnumerateFiles(directory, "Signaller-Updater-*.exe"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException exception)
                {
                    cleanupError = exception.Message;
                }
            }

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
                _updaterCleanupTimer.Stop();
                return;
            }

            if (++_updaterCleanupAttempts >= 30)
            {
                _updaterCleanupTimer.Stop();
                DisplayLatestNotification(cleanupError is null
                    ? "The previous updater is still finishing in the background."
                    : $"Could not remove a temporary updater: {cleanupError}");
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            _updaterCleanupTimer.Stop();
            DisplayLatestNotification($"Could not clean up the temporary updater: {exception.Message}");
        }
        catch (IOException exception)
        {
            _updaterCleanupTimer.Stop();
            DisplayLatestNotification($"Could not clean up the temporary updater: {exception.Message}");
        }
    }

    private int _updaterCleanupAttempts;

    private void DisplayLatestNotification(string? fallback = null)
    {
        var messages = _engine.DrainNotifications();
        var message = messages.LastOrDefault() ?? fallback;
        if (message is null) return;

        StatusToast.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        if (_toastTimeout is not null) _toastTimeout.Stop();
        _toastTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimeout.Tick += (_, _) =>
        {
            _toastTimeout.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        _toastTimeout.Start();
    }

    private DispatcherTimer? _toastTimeout;

    private static double DistanceSquared(double x1, double y1, double x2, double y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return dx * dx + dy * dy;
    }
}

public sealed class RailwaySurface : FrameworkElement
{
    private static readonly LinearGradientBrush CanvasBrush = CreateCanvasBrush();
    private static readonly SolidColorBrush AmbientNodeBrush = CreateFrozenBrush(Color.FromArgb(52, 111, 143, 148));
    private static readonly Pen AmbientGridPen = CreateFrozenPen(Color.FromArgb(15, 123, 153, 158), 1);
    private static readonly Pen EditorGridPen = CreateFrozenPen(Color.FromArgb(31, 147, 168, 180), 0.65);
    private static readonly Pen TrackShadowPen = CreateFrozenPen(Color.FromArgb(86, 2, 8, 12), 29);
    private static readonly Pen TrackBedPen = CreateFrozenPen(Color.FromRgb(39, 55, 62), 23);
    private static readonly Pen TiePen = CreateFrozenPen(Color.FromRgb(62, 78, 84), 1);
    private static readonly Pen RailPen = CreateFrozenPen(Color.FromRgb(126, 145, 149), 1.7);

    public GameEngine? Engine { get; set; }

    public WpfPoint? MouseToWorld(WpfPoint position)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var scale = Math.Min(ActualWidth / GameEngine.WorldWidth, ActualHeight / GameEngine.WorldHeight);
        var offsetX = (ActualWidth - GameEngine.WorldWidth * scale) / 2;
        var offsetY = (ActualHeight - GameEngine.WorldHeight * scale) / 2;
        var x = (position.X - offsetX) / scale;
        var y = (position.Y - offsetY) / scale;
        if (x < 0 || x > GameEngine.WorldWidth || y < 0 || y > GameEngine.WorldHeight)
            return null;
        return new WpfPoint(x, y);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(CanvasBrush, null, new Rect(RenderSize));

        if (Engine is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        var scale = Math.Min(ActualWidth / GameEngine.WorldWidth, ActualHeight / GameEngine.WorldHeight);
        var offsetX = (ActualWidth - GameEngine.WorldWidth * scale) / 2;
        var offsetY = (ActualHeight - GameEngine.WorldHeight * scale) / 2;
        drawingContext.PushTransform(new MatrixTransform(new Matrix(scale, 0, 0, scale, offsetX, offsetY)));

        DrawAmbientGrid(drawingContext);
        if (Engine.Mode == GameMode.Editor) DrawGrid(drawingContext);
        DrawTrack(drawingContext, GameEngine.MainLoop, closed: true);
        DrawBranch(drawingContext);
        DrawStation(drawingContext, Engine);
        DrawNetworkLabels(drawingContext);
        foreach (var item in Engine.PlacedItems) DrawPlacedItem(drawingContext, item, Engine.Mode);
        foreach (var points in Engine.Switches) DrawPoints(drawingContext, Engine, points);
        foreach (var signal in Engine.Signals) DrawSignal(drawingContext, Engine, signal);
        DrawTrain(drawingContext, Engine);
        if (Engine.Mode == GameMode.Editor && double.IsFinite(Engine.PreviewX))
        {
            var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(115, 224, 176)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawEllipse(null, previewPen,
                new WpfPoint(Engine.PreviewX, Engine.PreviewY), 22, 22);
        }

        drawingContext.Pop();
    }

    private static void DrawAmbientGrid(DrawingContext drawingContext)
    {
        for (var x = 40d; x <= GameEngine.WorldWidth; x += 80)
            drawingContext.DrawLine(AmbientGridPen,
                new WpfPoint(x, 0), new WpfPoint(x, GameEngine.WorldHeight));
        for (var y = 40d; y <= GameEngine.WorldHeight; y += 80)
            drawingContext.DrawLine(AmbientGridPen,
                new WpfPoint(0, y), new WpfPoint(GameEngine.WorldWidth, y));
        for (var x = 40d; x < GameEngine.WorldWidth; x += 160)
        for (var y = 40d; y < GameEngine.WorldHeight; y += 160)
            drawingContext.DrawEllipse(AmbientNodeBrush, null, new WpfPoint(x, y), 1.4, 1.4);
    }

    private static void DrawGrid(DrawingContext drawingContext)
    {
        for (var x = 20d; x < GameEngine.WorldWidth; x += GameEngine.GridSize)
            drawingContext.DrawLine(EditorGridPen,
                new WpfPoint(x, 0), new WpfPoint(x, GameEngine.WorldHeight));
        for (var y = 20d; y < GameEngine.WorldHeight; y += GameEngine.GridSize)
            drawingContext.DrawLine(EditorGridPen,
                new WpfPoint(0, y), new WpfPoint(GameEngine.WorldWidth, y));
    }

    private static void DrawTrack(DrawingContext drawingContext, IReadOnlyList<TrackPoint> points, bool closed)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
        using (var context = geometry.Open())
        {
            context.BeginFigure(ToPoint(points[0]), false, closed);
            context.PolyLineTo(points.Skip(1).Select(ToPoint).ToArray(), true, false);
        }
        geometry.Freeze();
        drawingContext.DrawGeometry(null, TrackShadowPen, geometry);
        drawingContext.DrawGeometry(null, TrackBedPen, geometry);

        for (var distance = 8d; distance < PolylineLength(points, closed); distance += 17)
        {
            var position = PositionAlong(points, distance, closed);
            var dx = -Math.Sin(position.Angle) * 8;
            var dy = Math.Cos(position.Angle) * 8;
            drawingContext.DrawLine(TiePen,
                new WpfPoint(position.X - dx, position.Y - dy),
                new WpfPoint(position.X + dx, position.Y + dy));
        }

        DrawRails(drawingContext, points, closed, 5);
        DrawRails(drawingContext, points, closed, -5);
    }

    private static void DrawRails(DrawingContext drawingContext, IReadOnlyList<TrackPoint> points, bool closed, double offset)
    {
        for (var index = 0; index < points.Count; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
            var dx = -Math.Sin(angle) * offset;
            var dy = Math.Cos(angle) * offset;
            if (index >= points.Count - 1 && !closed) break;
            var nextAngle = Math.Atan2(
                points[(index + 2) % points.Count].Y - end.Y,
                points[(index + 2) % points.Count].X - end.X);
            var nextDx = -Math.Sin(nextAngle) * offset;
            var nextDy = Math.Cos(nextAngle) * offset;
            drawingContext.DrawLine(RailPen,
                new WpfPoint(start.X + dx, start.Y + dy),
                new WpfPoint(end.X + nextDx, end.Y + nextDy));
        }
    }

    private static void DrawBranch(DrawingContext drawingContext)
    {
        TrackPoint[] siding =
        [
            new(700, 120),
            new(755, 193),
            new(700, 260)
        ];
        DrawTrack(drawingContext, siding, closed: false);
        var sidingLabel = CreateText("SIDING", 8, Color.FromRgb(156, 174, 176), FontWeights.Medium);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(23, 35, 42)),
            new Pen(new SolidColorBrush(Color.FromRgb(61, 81, 90)), 1),
            new Rect(668, 249, 66, 26), 5, 5);
        drawingContext.DrawText(sidingLabel, new WpfPoint(681, 257));
    }

    private static void DrawStation(DrawingContext drawingContext, GameEngine engine)
    {
        var position = engine.PositionAt(1319);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(28, 41, 49)),
            new Pen(new SolidColorBrush(Color.FromRgb(80, 99, 108)), 1),
            new Rect(295, 402, 116, 35), 7, 7);
        drawingContext.DrawText(CreateText("GREYFIELD", 10, Color.FromRgb(209, 225, 220), FontWeights.SemiBold),
            new WpfPoint(310, 406));
        drawingContext.DrawText(CreateText("STATION", 8, Color.FromRgb(129, 146, 143), FontWeights.Normal),
            new WpfPoint(310, 420));
        var marker = new Pen(new SolidColorBrush(Color.FromRgb(115, 224, 176)), 1);
        drawingContext.DrawLine(marker, new WpfPoint(position.X - 15, position.Y),
            new WpfPoint(position.X + 15, position.Y));
    }

    private static void DrawNetworkLabels(DrawingContext drawingContext)
    {
        DrawTextCentered(drawingContext, "NORTH LOOP", 420, 65, 9, Color.FromRgb(101, 118, 129));
        DrawTextCentered(drawingContext, "EAST JUNCTION", 867, 284, 9, Color.FromRgb(101, 118, 129));
        DrawTextCentered(drawingContext, "WEST APPROACH", 91, 287, 9, Color.FromRgb(101, 118, 129));
    }

    private static void DrawSignal(DrawingContext drawingContext, GameEngine engine, Signal signal)
    {
        var point = engine.SignalPosition(signal);
        var color = signal.Aspect switch
        {
            SignalAspect.Green => Color.FromRgb(115, 224, 176),
            SignalAspect.Yellow => Color.FromRgb(245, 187, 97),
            _ => Color.FromRgb(242, 107, 114)
        };
        drawingContext.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(119, 132, 140)), 2),
            new WpfPoint(point.X, point.Y + 9), new WpfPoint(point.X, point.Y + 29));
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(32, 44, 52)),
            new Pen(new SolidColorBrush(Color.FromRgb(96, 113, 123)), 1),
            new Rect(point.X - 8, point.Y - 11, 16, 23), 5, 5);
        var light = new SolidColorBrush(color);
        var glow = new RadialGradientBrush(Color.FromArgb(60, color.R, color.G, color.B), Colors.Transparent);
        drawingContext.DrawEllipse(glow, null, new WpfPoint(point.X, point.Y - 2), 9, 9);
        drawingContext.DrawEllipse(light, null, new WpfPoint(point.X, point.Y - 2), 4, 4);
        DrawTextCentered(drawingContext, signal.Id, point.X, point.Y - 21, 8, Color.FromRgb(169, 181, 189));
        DrawTextCentered(drawingContext, signal.Aspect.ToString().ToUpperInvariant(),
            point.X, point.Y + 32, 7, color);
    }

    private static void DrawPoints(DrawingContext drawingContext, GameEngine engine, Points points)
    {
        var position = engine.SwitchPosition(points);
        var color = points.Direction == PointDirection.Straight
            ? Color.FromRgb(115, 224, 176)
            : Color.FromRgb(245, 187, 97);
        var pen = new Pen(new SolidColorBrush(color), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        drawingContext.DrawLine(pen, new WpfPoint(position.X - 11, position.Y - 7),
            new WpfPoint(position.X + 11, position.Y + 7));
        drawingContext.DrawLine(pen, new WpfPoint(position.X - 11, position.Y + 7),
            new WpfPoint(position.X + 11, position.Y - 7));
        drawingContext.DrawEllipse(new SolidColorBrush(color), null,
            new WpfPoint(position.X, position.Y), 3.5, 3.5);
        DrawTextCentered(drawingContext,
            $"{points.Id} · {points.Direction.ToString().ToUpperInvariant()}",
            position.X, position.Y - 22, 8, Color.FromRgb(169, 181, 189));
    }

    private static void DrawTrain(DrawingContext drawingContext, GameEngine engine)
    {
        var position = engine.PositionAt(engine.TrainDistance);
        drawingContext.PushTransform(new RotateTransform(position.Angle * 180 / Math.PI, position.X, position.Y));
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(232, 169, 78)),
            null, new Rect(position.X - 20, position.Y - 8, 40, 16), 5, 5);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(38, 51, 59)),
            null, new Rect(position.X - 12, position.Y - 5, 20, 10), 2, 2);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 225, 163)),
            null, new Rect(position.X + 14, position.Y - 3, 3, 6));
        drawingContext.Pop();
        drawingContext.DrawEllipse(
            new SolidColorBrush(engine.TrainStopped
                ? Color.FromRgb(242, 107, 114)
                : Color.FromRgb(232, 169, 78)),
            null,
            new WpfPoint(position.X, position.Y - 19), 3, 3);
    }

    private static void DrawPlacedItem(DrawingContext drawingContext, PlacedItem item, GameMode mode)
    {
        var color = item.Kind switch
        {
            BuildItemKind.Signal => Color.FromRgb(242, 107, 114),
            BuildItemKind.Station => Color.FromRgb(136, 185, 176),
            BuildItemKind.Depot => Color.FromRgb(232, 169, 78),
            _ => Color.FromRgb(130, 147, 156)
        };
        var pen = new Pen(new SolidColorBrush(color), 2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        switch (item.Kind)
        {
            case BuildItemKind.StraightTrack:
                for (var offset = -5; offset <= 5; offset += 5)
                    drawingContext.DrawLine(pen, new WpfPoint(item.X - 17, item.Y + offset),
                        new WpfPoint(item.X + 17, item.Y + offset));
                break;
            case BuildItemKind.CurvedTrack:
                drawingContext.DrawGeometry(null, pen, CurvedTrack(item.X, item.Y, 20));
                drawingContext.DrawGeometry(null, pen, CurvedTrack(item.X, item.Y, 25));
                break;
            case BuildItemKind.Switch:
                drawingContext.DrawLine(pen, new WpfPoint(item.X - 16, item.Y + 7),
                    new WpfPoint(item.X + 15, item.Y - 7));
                drawingContext.DrawLine(pen, new WpfPoint(item.X - 16, item.Y - 7),
                    new WpfPoint(item.X + 15, item.Y + 7));
                break;
            case BuildItemKind.Signal:
                drawingContext.DrawEllipse(new SolidColorBrush(color), null,
                    new WpfPoint(item.X, item.Y), 6, 6);
                drawingContext.DrawLine(pen, new WpfPoint(item.X, item.Y + 6),
                    new WpfPoint(item.X, item.Y + 21));
                break;
            default:
                drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(20, 29, 36)),
                    pen, new Rect(item.X - 18, item.Y - 10, 36, 20), 4, 4);
                DrawTextCentered(drawingContext,
                    item.Kind == BuildItemKind.Station ? "STN" : "DEPOT",
                    item.X, item.Y - 5, 8, color);
                break;
        }

        if (mode == GameMode.Editor)
        {
            var hitboxPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 242, 107, 114)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawEllipse(null, hitboxPen, new WpfPoint(item.X, item.Y), 22, 22);
        }
    }

    private static StreamGeometry CurvedTrack(double x, double y, double radius)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new WpfPoint(x - 6, y + 6 - radius), false, false);
            context.ArcTo(new WpfPoint(x - 6 + radius, y + 6),
                new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static void DrawTextCentered(
        DrawingContext drawingContext,
        string text,
        double centerX,
        double topY,
        double fontSize,
        Color color)
    {
        var formatted = CreateText(text, fontSize, color, FontWeights.Normal);
        drawingContext.DrawText(formatted, new WpfPoint(centerX - formatted.Width / 2, topY));
    }

    private static FormattedText CreateText(string text, double fontSize, Color color, FontWeight weight)
    {
        return new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            new SolidColorBrush(color),
            1.0);
    }

    private static WpfPoint ToPoint(TrackPoint point) => new(point.X, point.Y);

    private static LinearGradientBrush CreateCanvasBrush()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(20, 32, 38),
            Color.FromRgb(14, 22, 29),
            new WpfPoint(0, 0),
            new WpfPoint(1, 1));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Color color, double thickness)
    {
        var pen = new Pen(CreateFrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static double PolylineLength(IReadOnlyList<TrackPoint> points, bool closed)
    {
        var result = 0d;
        var count = closed ? points.Count : points.Count - 1;
        for (var index = 0; index < count; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            result += Math.Sqrt((end.X - start.X) * (end.X - start.X)
                + (end.Y - start.Y) * (end.Y - start.Y));
        }
        return result;
    }

    private static TrackPosition PositionAlong(IReadOnlyList<TrackPoint> points, double distance, bool closed)
    {
        var remaining = distance;
        var count = closed ? points.Count : points.Count - 1;
        for (var index = 0; index < count; index++)
        {
            var start = points[index];
            var end = points[(index + 1) % points.Count];
            var segmentLength = Math.Sqrt((end.X - start.X) * (end.X - start.X)
                + (end.Y - start.Y) * (end.Y - start.Y));
            if (remaining <= segmentLength || index == count - 1)
            {
                var progress = Math.Clamp(remaining / segmentLength, 0, 1);
                return new(
                    start.X + (end.X - start.X) * progress,
                    start.Y + (end.Y - start.Y) * progress,
                    Math.Atan2(end.Y - start.Y, end.X - start.X));
            }
            remaining -= segmentLength;
        }
        return new(points[0].X, points[0].Y, 0);
    }

}
