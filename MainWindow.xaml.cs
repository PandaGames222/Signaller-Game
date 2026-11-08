using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Media;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Signaller.GameLogic;
using WpfPoint = System.Windows.Point;

namespace Signaller;

public partial class MainWindow : Window
{
    private GameEngine _engine;
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
    private bool _modeLoadInProgress;
    private bool _gameStarting;
    private bool _inGame;
    private bool _loadingSettings;
    private bool _soundEnabled = true;
    private AppRelease? _availableUpdate;
    private TrainService? _selectedTrain;
    private Signal? _selectedSignal;
    private double _lastDeskRefreshSeconds;
    private readonly Dictionary<FrameworkElement, int> _motionVersions = [];
    private int _lastDisplayedBalance = -1;

    public MainWindow(GameEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        Railway.Engine = _engine;
        _engine.TrainEnteredZone += Engine_TrainEnteredZone;
        _engine.TrainStationChanged += Engine_TrainStationChanged;
        try
        {
            _soundEnabled = AppSettingsStore.Load().TrainEntrySoundEnabled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            MessageBox.Show(
                $"The saved preferences could not be loaded:\n{exception.Message}",
                "Signaller — Settings warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        FooterVersion.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "1.8.0";
        VersionText.Text = $"VERSION {FooterVersion.Text}";
        _loadingSettings = true;
        EntrySoundCheckBox.IsChecked = _soundEnabled;
        _loadingSettings = false;

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
        ShowMenuPanel(HomePanel);
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

        if (_inGame) _engine.Advance(delta);
        if (_inGame && !_engine.IsPaused && _engine.Mode == GameMode.Signaller
            && _engine.Style == GameStyle.Original)
            Railway.InvalidateVisual();
        if (_inGame && _engine.Style == GameStyle.Regular)
            RegularRailway.InvalidateVisual();
        RefreshHud();
        RefreshTrainDetails();
        RefreshSignalDetails();

        if (_inGame && now - _lastSaveSeconds >= 30)
        {
            SaveGame();
            _lastSaveSeconds = now;
        }
        DisplayLatestNotification();
        if (_inGame && _engine.Style == GameStyle.Regular
            && now - _lastDeskRefreshSeconds >= 1)
        {
            RefreshRegularDesk();
            _lastDeskRefreshSeconds = now;
        }
    }

    private void RefreshHud()
    {
        if (_lastDisplayedBalance >= 0 && _lastDisplayedBalance != _engine.Balance)
            PulseElement(BalanceText);
        _lastDisplayedBalance = _engine.Balance;
        BalanceText.Text = _engine.FormattedBalance;
        IncomeText.Text = $"${_engine.IncomePerMinute:N0} / min";
        RouteCountText.Text = $"GREYFIELD CONTROL AREA  ·  {_engine.RouteCount} ROUTES";
        var heldTrains = _engine.Trains.Count(train => train.Stopped);
        TrainStatusText.Text = $"{_engine.TrainCount:00}  ACTIVE";
        if (heldTrains > 0) TrainStatusText.Text += $" · {heldTrains} HELD";
        TrainStatusText.ToolTip = _engine.TrainStatus;
        ClockText.Text = _engine.FormattedGameClock;
        TrainAlertText.Text = heldTrains == 0 ? string.Empty : $"{heldTrains} TRAIN(S) HELD";
        TrainAlertText.Visibility = heldTrains > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMode()
    {
        var editor = _engine.Mode == GameMode.Editor;
        var editorWasVisible = EditorPanel.Visibility == Visibility.Visible;
        SignallerModeButton.IsChecked = !editor;
        EditorModeButton.IsChecked = editor;
        EditorPanel.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
        if (editor && !editorWasVisible) AnimateElementIn(EditorPanel, -14, 0);
        EditorColumn.Width = editor ? new GridLength(252) : new GridLength(0);
        ModeEyebrow.Text = editor ? "NETWORK EXPANSION" : "NETWORK OPERATIONS";
        ModeTitle.Text = editor ? "Network editor"
            : _engine.Style == GameStyle.Regular ? "Control desk" : "Signaller desk";
        MapSubtitle.Text = editor ? "GRID BUILD MODE"
            : _engine.Style == GameStyle.Regular ? "LIVE OPERATIONS BOARD" : "SIGNALLING DIAGRAM";
        CanvasHint.Text = editor
            ? "CONNECT TRACK FROM DEPOT TO STATION  ·  R TO ROTATE  ·  DEMOLISH FOR A 50% REFUND"
            : _engine.Style == GameStyle.Regular
                ? "CLICK TRAINS OR SIGNALS FOR DETAILS  ·  SCROLL TO ZOOM  ·  RIGHT-DRAG TO PAN"
                : "CLICK A TRAIN FOR SERVICE DETAILS  ·  SELECT A SIGNAL TO SET ITS ASPECT";
        DemolishButton.Background = _engine.DemolishMode
            ? new SolidColorBrush(Color.FromRgb(54, 35, 38))
            : new SolidColorBrush(Color.FromRgb(25, 35, 45));
        RefreshBuildListSelection();
        UpdateStyleInterface();
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

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPanel(ModeSelectionPanel);
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPanel(SettingsPanel);
    }

    private void BackToHomeButton_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPanel(HomePanel);
    }

    private void ShowMenuPanel(Grid panel)
    {
        foreach (var otherPanel in new[] { HomePanel, ModeSelectionPanel, SettingsPanel })
        {
            if (!ReferenceEquals(otherPanel, panel))
                AnimateElementOut(otherPanel, 0, -12);
        }
        var overlayWasHidden = MenuOverlay.Visibility != Visibility.Visible;
        panel.Visibility = Visibility.Visible;
        MenuOverlay.Visibility = Visibility.Visible;
        if (overlayWasHidden) AnimateElementIn(MenuOverlay, 0, 0);
        AnimateElementIn(panel, 0, 16);
        ModeNavigation.Visibility = Visibility.Collapsed;
    }

    private async void GameStyleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_modeLoadInProgress || sender is not Button { Tag: GameStyle style }) return;
        if (_inGame && !SaveGame()) return;

        var lifetimeToken = _windowLifetime.Token;
        _inGame = false;
        _gameStarting = true;
        _modeLoadInProgress = true;
        GameLoadingText.Text = $"Loading {style} railway...";
        GameLoadingOverlay.Visibility = Visibility.Visible;
        AnimateElementIn(GameLoadingOverlay, 0, 8);
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (lifetimeToken.IsCancellationRequested) return;
            var nextEngine = _engine.Style == style
                ? _engine
                : await Task.Run(
                    () => new GameEngine(GameSaveStore.Load(style), style),
                    lifetimeToken);
            if (lifetimeToken.IsCancellationRequested) return;
            if (!ReferenceEquals(_engine, nextEngine))
            {
                _engine.TrainEnteredZone -= Engine_TrainEnteredZone;
                _engine.TrainStationChanged -= Engine_TrainStationChanged;
                _engine = nextEngine;
                _engine.TrainEnteredZone += Engine_TrainEnteredZone;
                _engine.TrainStationChanged += Engine_TrainStationChanged;
                Railway.Engine = _engine;
            }

            if (style == GameStyle.Regular && _engine.Mode != GameMode.Signaller)
                _engine.SetMode(GameMode.Signaller);

            AnimateElementOut(MenuOverlay, 0, -12);
            ModeNavigation.Visibility = Visibility.Visible;
            AnimateElementIn(ModeNavigation, 0, -5);
            CloseDetails();
            ApplyMode();
            RefreshHud();
            RefreshRegularDesk();
            AnimateElementOut(GameLoadingOverlay, 0, -6);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (lifetimeToken.IsCancellationRequested) return;
            _lastTickSeconds = _stopwatch.Elapsed.TotalSeconds;
            _lastSaveSeconds = _lastTickSeconds;
            _gameStarting = false;
            _inGame = true;
            SaveGame();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            _gameStarting = false;
            AnimateElementOut(GameLoadingOverlay, 0, -6);
            ShowMenuPanel(ModeSelectionPanel);
            MessageBox.Show(
                $"Could not load {style} mode:\n{exception.Message}",
                "Signaller — Game mode error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _modeLoadInProgress = false;
        }
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_inGame && !SaveGame()) return;
        _inGame = false;
        CloseDetails();
        ShowMenuPanel(HomePanel);
    }

    private void EntrySoundCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        var previousValue = _soundEnabled;
        _soundEnabled = EntrySoundCheckBox.IsChecked == true;
        try
        {
            AppSettingsStore.Save(new(_soundEnabled));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _soundEnabled = previousValue;
            _loadingSettings = true;
            EntrySoundCheckBox.IsChecked = previousValue;
            _loadingSettings = false;
            MessageBox.Show(
                $"Your sound preference could not be saved:\n{exception.Message}",
                "Signaller — Settings error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void UpdateStyleInterface()
    {
        var regular = _engine.Style == GameStyle.Regular;
        OriginalModeNavigation.Visibility = regular ? Visibility.Collapsed : Visibility.Visible;
        RegularModeNavigation.Visibility = regular ? Visibility.Visible : Visibility.Collapsed;
        RegularRailway.Engine = _engine;
        RegularDeskPanel.Visibility = (_inGame || _gameStarting) && regular
            ? Visibility.Visible
            : Visibility.Collapsed;
        RegularRailway.Visibility = (_inGame || _gameStarting) && regular
            ? Visibility.Visible
            : Visibility.Collapsed;
        Railway.Visibility = regular ? Visibility.Collapsed : Visibility.Visible;
        CanvasHint.Visibility = regular ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshRegularDesk()
    {
        if (_engine.Style != GameStyle.Regular) return;
        RegularRailway.InvalidateVisual();

        RegularRoutesPanel.Children.Clear();
        foreach (var route in _engine.Routes)
        {
            var routeSignals = _engine.Signals
                .Where(signal => signal.RouteId == route.Id
                    || signal.RouteId == "common")
                .ToArray();
            var affected = routeSignals.Any(signal => _engine.DisplayedAspect(signal) == SignalAspect.Red);
            var trains = _engine.Trains.Where(train => train.RouteId == route.Id).ToArray();
            var card = new Border
            {
                Margin = new Thickness(4),
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromRgb(20, 30, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 59, 68)),
                BorderThickness = new Thickness(1)
            };
            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = route.Name,
                Foreground = new SolidColorBrush(Color.FromRgb(220, 230, 233)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{route.EntranceName}  →  {route.ExitName}",
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(130, 144, 157)),
                FontSize = 8
            });
            content.Children.Add(new TextBlock
            {
                Text = affected ? "RESTRICTED · CHECK SIGNALS" : "ROUTE CLEAR",
                Margin = new Thickness(0, 9, 0, 0),
                Foreground = new SolidColorBrush(affected
                    ? Color.FromRgb(245, 187, 97)
                    : Color.FromRgb(115, 224, 176)),
                FontSize = 8,
                FontWeight = FontWeights.SemiBold
            });
            content.Children.Add(new TextBlock
            {
                Text = $"{trains.Length} active service(s)  ·  {routeSignals.Length} signals",
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(160, 174, 181)),
                FontSize = 8
            });
            content.Children.Add(new TextBlock
            {
                Text = string.Join("  ·  ", _engine.StationsForRoute(route.Id).Select(station => station.Name)),
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(130, 144, 157)),
                FontSize = 7,
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = content;
            RegularRoutesPanel.Children.Add(card);
        }

        RegularTrainList.Children.Clear();
        foreach (var train in _engine.Trains.OrderBy(train => train.Headcode, StringComparer.Ordinal))
        {
            var button = new Button
            {
                Tag = train.Id,
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(9, 7, 9, 7),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Color.FromRgb(20, 30, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 59, 68))
            };
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = train.Headcode,
                Foreground = new SolidColorBrush(Color.FromRgb(115, 224, 176)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            var service = new TextBlock
            {
                Text = $"{train.ServiceName}\n{train.Status}",
                Foreground = new SolidColorBrush(Color.FromRgb(190, 202, 207)),
                FontSize = 8,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(service, 1);
            row.Children.Add(service);
            var nextStation = _engine.StoppedAtStation(train) ?? _engine.NextStation(train);
            var destination = new TextBlock
            {
                Text = nextStation?.Platform ?? "EXIT",
                Foreground = new SolidColorBrush(Color.FromRgb(130, 144, 157)),
                FontSize = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(destination, 2);
            row.Children.Add(destination);
            button.Content = row;
            button.Click += RegularTrainButton_Click;
            RegularTrainList.Children.Add(button);
        }

        RegularSignalsPanel.Children.Clear();
        foreach (var signal in _engine.Signals)
        {
            var aspectColor = signal.Aspect switch
            {
                SignalAspect.Green => Color.FromRgb(115, 224, 176),
                SignalAspect.Yellow => Color.FromRgb(245, 187, 97),
                _ => Color.FromRgb(242, 107, 114)
            };
            var button = new Button
            {
                Tag = signal.Id,
                Content = $"{signal.Id}     {signal.Aspect.ToString().ToUpperInvariant()}",
                Margin = new Thickness(0, 0, 0, 5),
                Padding = new Thickness(9, 7, 9, 7),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = new SolidColorBrush(aspectColor),
                Background = new SolidColorBrush(Color.FromRgb(20, 30, 37)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(45, 59, 68)),
                ToolTip = "Open signal controls"
            };
            button.Click += RegularSignalButton_Click;
            RegularSignalsPanel.Children.Add(button);
        }
    }

    private void RegularTrainButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string trainId }) return;
        var train = _engine.Trains.FirstOrDefault(candidate => candidate.Id == trainId);
        if (train is not null) OpenTrainDetails(train);
    }

    private void RegularSignalButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string signalId }) return;
        var signal = _engine.Signals.FirstOrDefault(candidate => candidate.Id == signalId);
        if (signal is not null) OpenSignalDetails(signal);
    }

    private void BuildItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BuildItemKind kind }) return;
        _engine.SelectBuildItem(kind);
        RotateButton.Visibility = kind is BuildItemKind.StraightTrack or BuildItemKind.CurvedTrack
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        if (sender is not RailwaySurface railway) return;
        var position = railway.MouseToWorld(e.GetPosition(railway));
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
            railway.InvalidateVisual();
            return;
        }

        var signal = _engine.Signals.FirstOrDefault(signal =>
        {
            var marker = _engine.SignalPosition(signal);
            return DistanceSquared(marker.X, marker.Y, point.X, point.Y) < 30 * 30;
        });
        if (signal is not null)
        {
            OpenSignalDetails(signal);
            return;
        }

        var train = _engine.Trains
            .Select(candidate => (Train: candidate, Position: _engine.PositionAt(candidate.RouteId, candidate.Distance)))
            .Where(item => DistanceSquared(item.Position.X, item.Position.Y, point.X, point.Y) < 30 * 30)
            .OrderBy(item => DistanceSquared(item.Position.X, item.Position.Y, point.X, point.Y))
            .Select(item => item.Train)
            .FirstOrDefault();
        if (train is not null)
        {
            OpenTrainDetails(train);
            return;
        }

        CloseDetails();
    }

    private void Railway_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not RailwaySurface railway) return;
        if (railway.IsPanning)
        {
            railway.UpdatePan(e.GetPosition(railway));
            e.Handled = true;
            return;
        }
        if (_engine.Mode != GameMode.Editor || railway.MouseToWorld(e.GetPosition(railway)) is not { } point)
            return;

        _engine.SetPreview(point.X, point.Y);
        railway.InvalidateVisual();
    }

    private void Railway_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _engine.ClearPreview();
        if (sender is RailwaySurface railway) railway.InvalidateVisual();
    }

    private void Railway_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not RailwaySurface railway
            || (e.ChangedButton != System.Windows.Input.MouseButton.Right
                && e.ChangedButton != System.Windows.Input.MouseButton.Middle))
            return;
        railway.BeginPan(e.GetPosition(railway));
        e.Handled = true;
    }

    private void Railway_PreviewMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not RailwaySurface railway || !railway.IsPanning) return;
        railway.EndPan();
        e.Handled = true;
    }

    private void Railway_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not RailwaySurface railway) return;
        railway.ZoomAt(e.GetPosition(railway), e.Delta);
        e.Handled = true;
    }

    private void RotateButton_Click(object sender, RoutedEventArgs e)
    {
        _engine.RotateSelectedItem();
        DisplayLatestNotification($"Track piece rotated to {_engine.SelectedRotation}°.");
        Railway.InvalidateVisual();
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (SignalControlPanel.Visibility == Visibility.Visible && _selectedSignal is not null)
        {
            var aspect = e.Key switch
            {
                System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1 => SignalAspect.Green,
                System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2 => SignalAspect.Yellow,
                System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3 => SignalAspect.Red,
                _ => (SignalAspect?)null
            };
            if (aspect is not null)
            {
                ApplySignalAspect(aspect.Value);
                e.Handled = true;
                return;
            }
        }

        if (_engine.Mode == GameMode.Editor && e.Key == System.Windows.Input.Key.R
            && _engine.SelectedBuildItem is BuildItemKind.StraightTrack or BuildItemKind.CurvedTrack)
        {
            _engine.RotateSelectedItem();
            Railway.InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            CloseDetails();
        }
    }

    private void SignalAspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }
            || !Enum.TryParse<SignalAspect>(value, out var aspect))
            return;
        ApplySignalAspect(aspect);
    }

    private void ApplySignalAspect(SignalAspect aspect)
    {
        if (_selectedSignal is null || !_engine.SetSignalAspect(_selectedSignal.Id, aspect)) return;
        SaveGame();
        RefreshSignalDetails();
        DisplayLatestNotification();
        Railway.InvalidateVisual();
        RegularRailway.InvalidateVisual();
    }

    private void OpenSignalDetails(Signal signal)
    {
        _selectedSignal = signal;
        _selectedTrain = null;
        AnimateElementOut(TrainInfoPanel, 16, 0);
        SignalControlPanel.Visibility = Visibility.Visible;
        AnimateElementIn(SignalControlPanel, 16, 0);
        RefreshSignalDetails();
    }

    private void RefreshSignalDetails()
    {
        if (_selectedSignal is null) return;
        SignalInfoId.Text = _selectedSignal.Id;
        SignalInfoAspect.Text = $"CURRENT ASPECT · {_engine.DisplayedAspect(_selectedSignal).ToString().ToUpperInvariant()}";
    }

    private void OpenTrainDetails(TrainService train)
    {
        _selectedTrain = train;
        _selectedSignal = null;
        AnimateElementOut(SignalControlPanel, 16, 0);
        TrainInfoPanel.Visibility = Visibility.Visible;
        AnimateElementIn(TrainInfoPanel, 16, 0);
        RefreshTrainDetails();
    }

    private void RefreshTrainDetails()
    {
        if (_selectedTrain is null) return;
        var train = _engine.Trains.FirstOrDefault(candidate => candidate.Id == _selectedTrain.Id);
        if (train is null)
        {
            CloseDetails();
            return;
        }

        var route = _engine.Routes.First(item => item.Id == train.RouteId);
        var nextStation = _engine.StoppedAtStation(train) ?? _engine.NextStation(train);
        TrainInfoHeadcode.Text = train.Headcode;
        TrainInfoService.Text = train.ServiceName;
        TrainInfoType.Text = train.TrainType;
        TrainInfoLatency.Text = _engine.LatencyText(train);
        TrainInfoSpeed.Text = $"{train.Speed * 3.6:0} km/h";
        TrainInfoNextStation.Text = nextStation is null
            ? route.ExitName
            : $"{nextStation.Name} · {nextStation.Platform}";
        TrainInfoDestination.Text = $"{route.Name} · {route.Destination}";
        TrainInfoStatus.Text = train.Status;
    }

    private void CloseDetailsButton_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void CloseDetails()
    {
        _selectedTrain = null;
        _selectedSignal = null;
        AnimateElementOut(TrainInfoPanel, 16, 0);
        AnimateElementOut(SignalControlPanel, 16, 0);
    }

    private void Engine_TrainEnteredZone(TrainService train)
    {
        if (_soundEnabled) SystemSounds.Asterisk.Play();
    }

    private void Engine_TrainStationChanged(TrainService train, StationInfo station, bool doorsOpening)
    {
        if (!_soundEnabled) return;
        if (doorsOpening) SystemSounds.Exclamation.Play();
        else SystemSounds.Beep.Play();
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
        UpdateButton.Content = "UPDATE";
        UpdateButton.IsEnabled = false;
        UpdateButton.ToolTip = "Checking for the latest Signaller release";

        try
        {
            var release = await _updater.CheckAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _availableUpdate = release;
            if (release is null)
            {
                UpdateButton.Content = "UPDATE";
                UpdateButton.ToolTip = "You have the latest version. Click to check again.";
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
            UpdateButton.Content = "UPDATE";
            UpdateButton.ToolTip = "Could not check for updates. Click to retry.";
            DisplayLatestNotification($"Could not check for updates: {exception.Message}");
        }
        finally
        {
            _updateRequestInProgress = false;
            if (!cancellationToken.IsCancellationRequested)
                UpdateButton.IsEnabled = true;
        }
    }

    private bool SaveGame()
    {
        try
        {
            GameSaveStore.Save(_engine.Save(), _engine.Style);
            _lastSaveSeconds = _stopwatch.Elapsed.TotalSeconds;
            _saveFailed = false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidDataException)
        {
            if (_saveFailed)
            {
                return false;
            }
            _saveFailed = true;
            ShowToast($"Could not save your game: {exception.Message}");
            return false;
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_inGame && !SaveGame()
            && MessageBox.Show(
                "Your game could not be saved. Close Signaller anyway and lose unsaved changes?",
                "Signaller — Save error",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        _windowLifetime.Cancel();
        _timer.Stop();
        _updateCheckTimer.Stop();
        _updaterCleanupTimer.Stop();
        _updater.Dispose();
        _engine.TrainEnteredZone -= Engine_TrainEnteredZone;
        _engine.TrainStationChanged -= Engine_TrainStationChanged;
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

        ShowToast(message);
    }

    private void ShowToast(string message)
    {
        StatusToast.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        AnimateElementIn(ToastBorder, 0, -8);
        if (_toastTimeout is not null) _toastTimeout.Stop();
        _toastTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimeout.Tick += (_, _) =>
        {
            _toastTimeout.Stop();
            AnimateElementOut(ToastBorder, 0, -5);
        };
        _toastTimeout.Start();
    }

    private DispatcherTimer? _toastTimeout;

    private void AnimateElementIn(FrameworkElement element, double offsetX, double offsetY)
    {
        NextMotionVersion(element);
        var transform = GetMotionTransform(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        transform.X = 0;
        transform.Y = 0;
        element.Visibility = Visibility.Visible;

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(offsetX, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void AnimateElementOut(FrameworkElement element, double offsetX, double offsetY)
    {
        if (element.Visibility != Visibility.Visible) return;
        var version = NextMotionVersion(element);
        var transform = GetMotionTransform(element);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        transform.BeginAnimation(TranslateTransform.XProperty, null);
        transform.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        transform.X = 0;
        transform.Y = 0;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            if (!_motionVersions.TryGetValue(element, out var currentVersion)
                || currentVersion != version)
                return;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            element.Opacity = 1;
            transform.X = 0;
            transform.Y = 0;
            element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, offsetX, fade.Duration.TimeSpan)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            });
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, offsetY, fade.Duration.TimeSpan)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
                FillBehavior = FillBehavior.Stop
            });
    }

    private void PulseElement(FrameworkElement element)
    {
        var transform = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        element.RenderTransform = transform;
        var pulse = new DoubleAnimation(1, 1.12, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    private int NextMotionVersion(FrameworkElement element)
    {
        var version = _motionVersions.GetValueOrDefault(element) + 1;
        _motionVersions[element] = version;
        return version;
    }

    private static TranslateTransform GetMotionTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform transform) return transform;
        transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

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
    private static readonly Pen EditorTrackBedPen = CreateFrozenPen(Color.FromRgb(39, 55, 62), 9);
    private static readonly Pen EditorTrackTiePen = CreateFrozenPen(Color.FromRgb(87, 105, 111), 1.2);
    private double _zoom = 1;
    private double _panX;
    private double _panY;
    private WpfPoint _panStart;
    private double _panStartX;
    private double _panStartY;

    public GameEngine? Engine { get; set; }
    public bool IsPanning { get; private set; }

    public WpfPoint? MouseToWorld(WpfPoint position)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return null;
        var transform = CreateWorldTransform();
        if (!transform.HasInverse) return null;
        transform.Invert();
        var world = transform.Transform(position);
        var x = world.X;
        var y = world.Y;
        if (x < 0 || x > GameEngine.WorldWidth || y < 0 || y > GameEngine.WorldHeight)
            return null;
        return new WpfPoint(x, y);
    }

    public void BeginPan(WpfPoint position)
    {
        IsPanning = true;
        _panStart = position;
        _panStartX = _panX;
        _panStartY = _panY;
        CaptureMouse();
    }

    public void UpdatePan(WpfPoint position)
    {
        if (!IsPanning) return;
        _panX = _panStartX + position.X - _panStart.X;
        _panY = _panStartY + position.Y - _panStart.Y;
        InvalidateVisual();
    }

    public void EndPan()
    {
        IsPanning = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    public void ZoomAt(WpfPoint position, int wheelDelta)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || wheelDelta == 0) return;
        var before = CreateWorldTransform();
        before.Invert();
        var worldAnchor = before.Transform(position);
        _zoom = Math.Clamp(_zoom * (wheelDelta > 0 ? 1.15 : 1 / 1.15), 0.65, 4);
        var after = CreateWorldTransform();
        var projectedAnchor = after.Transform(worldAnchor);
        _panX += position.X - projectedAnchor.X;
        _panY += position.Y - projectedAnchor.Y;
        InvalidateVisual();
    }

    private Matrix CreateWorldTransform()
    {
        var scale = Math.Min(ActualWidth / GameEngine.WorldWidth, ActualHeight / GameEngine.WorldHeight) * _zoom;
        var offsetX = (ActualWidth - GameEngine.WorldWidth * scale) / 2 + _panX;
        var offsetY = (ActualHeight - GameEngine.WorldHeight * scale) / 2 + _panY;
        return new Matrix(scale, 0, 0, scale, offsetX, offsetY);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(CanvasBrush, null, new Rect(RenderSize));

        if (Engine is null || ActualWidth <= 0 || ActualHeight <= 0) return;
        drawingContext.PushTransform(new MatrixTransform(CreateWorldTransform()));

        DrawAmbientGrid(drawingContext);
        if (Engine.Mode == GameMode.Editor) DrawGrid(drawingContext);
        foreach (var route in Engine.Routes)
            DrawTrack(drawingContext, route.Points, closed: false);
        DrawStations(drawingContext, Engine);
        DrawNetworkLabels(drawingContext, Engine);
        foreach (var item in Engine.PlacedItems)
        {
            if (item.Kind == BuildItemKind.Signal && Engine.IsOperationalSignalItem(item)) continue;
            DrawPlacedItem(drawingContext, item, Engine.Mode);
        }
        foreach (var signal in Engine.Signals) DrawSignal(drawingContext, Engine, signal);
        foreach (var train in Engine.Trains) DrawTrain(drawingContext, Engine, train);
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

    private static void DrawStations(DrawingContext drawingContext, GameEngine engine)
    {
        var stationPen = CreateFrozenPen(Color.FromRgb(80, 99, 108), 1);
        var markerPen = CreateFrozenPen(Color.FromRgb(115, 224, 176), 1.4);
        if (engine.Style == GameStyle.Original)
        {
            foreach (var route in engine.Routes)
            foreach (var station in engine.StationsForRoute(route.Id))
            {
                var position = engine.PositionAt(route.Id, station.Distance);
                var marker = new WpfPoint(position.X, position.Y);
                drawingContext.DrawLine(markerPen,
                    new WpfPoint(position.X - 11, position.Y), new WpfPoint(position.X + 11, position.Y));
                drawingContext.DrawRoundedRectangle(
                    new SolidColorBrush(Color.FromRgb(28, 41, 49)),
                    stationPen,
                    new Rect(position.X + 15, position.Y - 19, 122, 34), 6, 6);
                drawingContext.DrawText(CreateText(station.Name.ToUpperInvariant(), 8,
                        Color.FromRgb(209, 225, 220), FontWeights.SemiBold),
                    new WpfPoint(position.X + 23, position.Y - 14));
                drawingContext.DrawText(CreateText(station.Platform.ToUpperInvariant(), 7,
                        Color.FromRgb(129, 146, 143), FontWeights.Normal),
                    new WpfPoint(position.X + 23, position.Y + 1));
            }
            return;
        }

        var groups = engine.AllStations.GroupBy(item =>
            string.IsNullOrWhiteSpace(item.Station.GroupId)
                ? $"{item.RouteId}/{item.Station.Name}"
                : item.Station.GroupId);
        foreach (var group in groups)
        {
            var platforms = group.ToArray();
            var central = platforms[0].Station.GroupId == "central";
            foreach (var (platformRouteId, platformStation) in platforms)
            {
                var platformPosition = engine.PositionAt(platformRouteId, platformStation.Distance);
                var indicator = engine.PlatformIndicatorFor(platformRouteId, platformStation);
                var platformColor = indicator switch
                {
                    PlatformIndicator.DoorsOpen => Colors.White,
                    PlatformIndicator.ReadyToDepart when (int)(engine.GameSeconds * 2) % 2 == 0
                        => Color.FromRgb(245, 187, 97),
                    PlatformIndicator.ReadyToDepart => Color.FromRgb(91, 73, 46),
                    PlatformIndicator.Delayed => Color.FromRgb(242, 107, 114),
                    _ => Color.FromRgb(115, 224, 176)
                };
                DrawRegularPlatform(
                    drawingContext,
                    platformPosition,
                    platformStation.Platform,
                    platformColor);

                if (!central)
                    DrawStationName(drawingContext, platformStation.Name,
                        platformPosition.X, platformPosition.Y - 54);
            }

            if (!central) continue;
            var centralPositions = platforms
                .Select(item => engine.PositionAt(item.RouteId, item.Station.Distance))
                .ToArray();
            var labelPosition = centralPositions.OrderBy(position => position.Y).First();
            DrawStationName(drawingContext, platforms[0].Station.Name,
                labelPosition.X, labelPosition.Y - 74);
        }
    }

    private static void DrawRegularPlatform(
        DrawingContext drawingContext,
        TrackPosition position,
        string platform,
        Color indicatorColor)
    {
        const double length = 148;
        const double top = -34;
        const double height = 18;
        drawingContext.PushTransform(new RotateTransform(
            position.Angle * 180 / Math.PI, position.X, position.Y));
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(91, 105, 109)),
            new Pen(new SolidColorBrush(Color.FromRgb(153, 168, 166)), 1),
            new Rect(position.X - length / 2, position.Y + top, length, height),
            3,
            3);
        drawingContext.DrawLine(
            new Pen(new SolidColorBrush(Color.FromRgb(230, 199, 101)), 1.5),
            new WpfPoint(position.X - length / 2 + 5, position.Y + top + height - 2),
            new WpfPoint(position.X + length / 2 - 5, position.Y + top + height - 2));
        drawingContext.DrawEllipse(
            new SolidColorBrush(indicatorColor),
            null,
            new WpfPoint(position.X + length / 2 - 13, position.Y + top + height / 2),
            3.5,
            3.5);
        drawingContext.DrawText(
            CreateText(platform.ToUpperInvariant(), 7, Color.FromRgb(20, 31, 36), FontWeights.SemiBold),
            new WpfPoint(position.X - length / 2 + 13, position.Y + top + 4));
        drawingContext.Pop();
    }

    private static void DrawStationName(
        DrawingContext drawingContext,
        string name,
        double centerX,
        double topY)
    {
        var text = CreateText(name.ToUpperInvariant(), 7.5,
            Color.FromRgb(218, 228, 226), FontWeights.SemiBold);
        var width = Math.Min(190, text.Width + 14);
        var left = Math.Clamp(centerX - width / 2, 4, GameEngine.WorldWidth - width - 4);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(24, 36, 42)),
            new Pen(new SolidColorBrush(Color.FromRgb(72, 91, 96)), 0.8),
            new Rect(left, topY, width, 15),
            3,
            3);
        drawingContext.DrawText(text,
            new WpfPoint(left + (width - text.Width) / 2, topY + 2));
    }

    private static void DrawNetworkLabels(DrawingContext drawingContext, GameEngine engine)
    {
        foreach (var route in engine.Routes)
        {
            var entry = route.Points[0];
            DrawTextCentered(drawingContext, $"{route.EntranceName} · ENTRY",
                entry.X + 54, entry.Y + (entry.Y < 180 ? 31 : entry.Y > 920 ? -31 : 30), 8,
                Color.FromRgb(151, 174, 171));
            var exit = route.Points[^1];
            if (engine.Style == GameStyle.Regular)
            {
                DrawTextCentered(drawingContext, $"{route.Name} · {route.ExitName}",
                    exit.X - 92, exit.Y - 28, 8, Color.FromRgb(245, 187, 97));
            }
            else
            {
                DrawTextCentered(drawingContext, route.ExitName, exit.X - 32, exit.Y - 38, 8,
                    Color.FromRgb(245, 187, 97));
                DrawTextCentered(drawingContext, route.Name, exit.X - 70, exit.Y + 23, 7,
                    Color.FromRgb(101, 118, 129));
            }
        }
        if (engine.Style == GameStyle.Original)
        {
            DrawTextCentered(drawingContext, "AUTOMATIC JUNCTION", 820, 593, 8,
                Color.FromRgb(101, 118, 129));
            DrawTextCentered(drawingContext, "EAST JUNCTION", 1260, 385, 8,
                Color.FromRgb(101, 118, 129));
        }
    }

    private static void DrawSignal(DrawingContext drawingContext, GameEngine engine, Signal signal)
    {
        var point = engine.SignalPosition(signal);
        var aspect = engine.DisplayedAspect(signal);
        var color = aspect switch
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
        DrawTextCentered(drawingContext, aspect.ToString().ToUpperInvariant(),
            point.X, point.Y + 32, 7, color);
    }

    private static void DrawTrain(DrawingContext drawingContext, GameEngine engine, TrainService train)
    {
        var position = engine.PositionAt(train.RouteId, train.Distance);
        var trainColor = train.RouteId switch
        {
            "north" => Color.FromRgb(112, 184, 232),
            "south" => Color.FromRgb(175, 147, 224),
            _ => Color.FromRgb(232, 169, 78)
        };
        drawingContext.PushTransform(new RotateTransform(position.Angle * 180 / Math.PI, position.X, position.Y));
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(trainColor),
            null, new Rect(position.X - 20, position.Y - 8, 40, 16), 5, 5);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(38, 51, 59)),
            null, new Rect(position.X - 12, position.Y - 5, 20, 10), 2, 2);
        drawingContext.DrawRectangle(new SolidColorBrush(Color.FromRgb(255, 225, 163)),
            null, new Rect(position.X + 14, position.Y - 3, 3, 6));
        drawingContext.Pop();
        drawingContext.DrawEllipse(
            new SolidColorBrush(train.Stopped
                ? Color.FromRgb(242, 107, 114)
                : trainColor),
            null,
            new WpfPoint(position.X, position.Y - 19), 3, 3);
        DrawTextCentered(drawingContext, train.Headcode, position.X, position.Y + 12, 7,
            Color.FromRgb(215, 225, 228));
    }

    private static void DrawPlacedItem(DrawingContext drawingContext, PlacedItem item, GameMode mode)
    {
        drawingContext.PushTransform(new RotateTransform(item.Rotation, item.X, item.Y));
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
                drawingContext.DrawLine(
                    EditorTrackBedPen,
                    new WpfPoint(item.X - 20, item.Y),
                    new WpfPoint(item.X + 20, item.Y));
                for (var tie = -16; tie <= 16; tie += 8)
                    drawingContext.DrawLine(
                        EditorTrackTiePen,
                        new WpfPoint(item.X + tie, item.Y - 8),
                        new WpfPoint(item.X + tie, item.Y + 8));
                foreach (var railOffset in new[] { -5d, 5d })
                    drawingContext.DrawLine(pen, new WpfPoint(item.X - 20, item.Y + railOffset),
                        new WpfPoint(item.X + 20, item.Y + railOffset));
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
        drawingContext.Pop();
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
