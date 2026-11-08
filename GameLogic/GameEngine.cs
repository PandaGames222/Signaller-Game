namespace Signaller.GameLogic;

public enum GameMode
{
    Signaller,
    Editor
}

public enum GameStyle
{
    Original,
    Regular
}

public enum SignalAspect
{
    Red,
    Yellow,
    Green
}

public enum PlatformIndicator
{
    Clear,
    DoorsOpen,
    ReadyToDepart,
    Delayed
}

public enum PointDirection
{
    Straight,
    Diverging
}

public enum BuildItemKind
{
    StraightTrack,
    CurvedTrack,
    Switch,
    Signal,
    Station,
    Depot
}

public readonly record struct TrackPoint(double X, double Y);
public readonly record struct TrackPosition(double X, double Y, double Angle);
public readonly record struct BuildItemDefinition(
    BuildItemKind Kind,
    string Name,
    string Symbol,
    int Cost);

public sealed record GameRoute(
    string Id,
    string Name,
    string Destination,
    string ExitName,
    IReadOnlyList<TrackPoint> Points,
    string EntranceName = "WEST DEPOT");

public sealed record StationInfo(
    string Name,
    string Platform,
    double Distance,
    string GroupId = "");

public sealed class Signal
{
    public required string Id { get; init; }
    public required double Distance { get; init; }
    public SignalAspect Aspect { get; set; }
    public double Side { get; init; }
    public required string RouteId { get; init; }
}

public sealed record PlacedItem(
    BuildItemKind Kind,
    double X,
    double Y,
    int Cost,
    int Rotation = 0);

public sealed class TrainService
{
    public required string Id { get; init; }
    public required string Headcode { get; init; }
    public required string ServiceName { get; init; }
    public required string TrainType { get; init; }
    public required string RouteId { get; init; }
    public required double Speed { get; init; }
    public double Distance { get; set; }
    public double LatencySeconds { get; set; }
    public bool Stopped { get; set; }
    public string Status { get; set; } = "In service";
    public double StationDwellSeconds { get; set; }
    public double? StoppedAtStationDistance { get; set; }
}

public sealed record TrainSaveData(
    double Distance,
    bool Stopped,
    string Status,
    string Id = "TR01",
    string Headcode = "2K14",
    string ServiceName = "Greyfield Local",
    string TrainType = "Class 170 Turbostar",
    string RouteId = "main",
    double Speed = 47,
    double LatencySeconds = 0,
    double StationDwellSeconds = 0,
    double? StoppedAtStationDistance = null);

public sealed record GameSaveData(
    int Version,
    GameMode Mode,
    int Balance,
    double GameSeconds,
    double RealSeconds,
    double EarnedInWindow,
    IReadOnlyList<PlacedItem> PlacedItems,
    IReadOnlyList<SignalSaveData> Signals,
    IReadOnlyList<PointsSaveData> Points,
    TrainSaveData Train,
    IReadOnlyList<TrainSaveData>? Trains = null,
    GameStyle Style = GameStyle.Original);

public sealed record SignalSaveData(string Id, SignalAspect Aspect);
public sealed record PointsSaveData(string Id, PointDirection Direction);

public sealed class GameEngine
{
    public const double WorldWidth = 1800;
    public const double WorldHeight = 1100;
    public const double GridSize = 40;
    public const int SaveVersion = 4;
    public const int StartingBalance = 5000;
    public const double SafetyDistance = 36;
    public const double SimulationClockRate = 3;
    public const double TrainSpeed = 47;
    public const double SectionPayment = 25;
    public const double DestinationPayment = 100;
    public const double StationDoorOpenSeconds = 10;
    public const double StationMinimumDwellSeconds = 15;
    public const double StationDelayedSeconds = 30;
    public const int MaximumTrains = 5;

    public static readonly TrackPoint[] MainRoutePoints =
    [
        new(40, 550),
        new(200, 550),
        new(350, 420),
        new(620, 420),
        new(820, 550),
        new(1080, 550),
        new(1260, 420),
        new(1510, 420),
        new(1760, 550)
    ];

    public static readonly BuildItemDefinition[] BuildItems =
    [
        new(BuildItemKind.StraightTrack, "Straight track", "━", 100),
        new(BuildItemKind.CurvedTrack, "Curved track", "⌒", 100),
        new(BuildItemKind.Signal, "Signal", "●", 300),
        new(BuildItemKind.Station, "Station", "▤", 2500),
        new(BuildItemKind.Depot, "Spawner / depot", "▰", 1000)
    ];

    private static readonly TrackPoint[] NorthRoutePoints =
    [
        .. MainRoutePoints.Take(5),
        new(820, 340),
        new(820, 70)
    ];

    private static readonly TrackPoint[] SouthRoutePoints =
    [
        .. MainRoutePoints.Take(5),
        new(820, 780),
        new(820, 1030)
    ];

    private static readonly TrackPoint[] AirportRoutePoints =
    [
        .. MainRoutePoints.Take(7),
        new(1410, 260),
        new(1600, 180)
    ];

    private static readonly TrackPoint[] HarborRoutePoints =
    [
        .. MainRoutePoints.Take(7),
        new(1400, 570),
        new(1570, 730),
        new(1650, 900)
    ];

    private static readonly RouteMetrics MainMetrics = MeasureRoute(MainRoutePoints);
    private static readonly RouteMetrics NorthMetrics = MeasureRoute(NorthRoutePoints);
    private static readonly RouteMetrics SouthMetrics = MeasureRoute(SouthRoutePoints);
    private static readonly RouteMetrics AirportMetrics = MeasureRoute(AirportRoutePoints);
    private static readonly RouteMetrics HarborMetrics = MeasureRoute(HarborRoutePoints);
    private static readonly string[] RouteIds = ["main", "north", "south", "airport", "harbor"];
    private static readonly string[] ServiceNames =
    [
        "Greyfield Local",
        "North Quay Express",
        "Canal Park Shuttle",
        "Portside Connector",
        "Riverside Regional"
    ];
    private static readonly string[] Headcodes = ["2K14", "1N26", "5C08", "2P42", "1R17"];
    private static readonly string[] TrainTypes =
    [
        "Class 170 Turbostar",
        "Class 185 Desiro",
        "Class 195 Civity",
        "Class 350 Desiro",
        "Class 158 Express Sprinter"
    ];
    private static readonly double[] ServiceSpeeds = [44, 49, 42, 47, 45];

    private readonly Dictionary<string, RouteMetrics> _metrics;
    private readonly Dictionary<string, List<StationInfo>> _stations;
    private readonly List<GameRoute> _routes = [];
    private readonly List<GameRoute> _availableRoutes = [];
    private readonly List<Signal> _signals = [];
    private readonly HashSet<(double X, double Y)> _operationalSignalLocations = [];
    private readonly List<TrainService> _trains = [];
    private readonly Queue<(double Time, double Amount)> _recentEarnings = new();
    private readonly List<string> _notifications = [];
    private double _previewX = double.NaN;
    private double _previewY = double.NaN;
    private double _spawnCountdown = 10;
    private int _nextTrainNumber = 6;

    public GameEngine(GameSaveData? save = null, GameStyle style = GameStyle.Original)
    {
        Style = save is not null && Enum.IsDefined(save.Style) ? save.Style : style;
        _routes.AddRange(Style == GameStyle.Regular
            ?
            [
                new("main", "GREYFIELD MAIN", "Portside", "EAST EXIT", MainRoutePoints),
                new("north", "NORTH QUAY", "North Quay", "NORTH EXIT", NorthRoutePoints),
                new("south", "CANAL PARK", "Canal Park", "SOUTH EXIT", SouthRoutePoints),
                new("airport", "AIRPORT LINE", "Greyfield Airport", "AIRPORT EXIT", AirportRoutePoints),
                new("harbor", "HARBOR LINE", "East Harbor", "HARBOR EXIT", HarborRoutePoints)
            ]
            :
            [
                new("main", "GREYFIELD MAIN", "Portside", "EAST EXIT", MainRoutePoints),
                new("north", "NORTH QUAY", "North Quay", "NORTH EXIT", NorthRoutePoints),
                new("south", "CANAL PARK", "Canal Park", "SOUTH EXIT", SouthRoutePoints)
            ]);
        _availableRoutes.AddRange(_routes);
        _metrics = new Dictionary<string, RouteMetrics>(StringComparer.Ordinal)
        {
            ["main"] = MainMetrics,
            ["north"] = NorthMetrics,
            ["south"] = SouthMetrics
        };
        if (Style == GameStyle.Regular)
        {
            _metrics.Add("airport", AirportMetrics);
            _metrics.Add("harbor", HarborMetrics);
        }
        TrackLength = MainMetrics.Total;
        var centralDistance = MainMetrics.Starts[2] + 90;
        _stations = Style == GameStyle.Regular
            ? new Dictionary<string, List<StationInfo>>(StringComparer.Ordinal)
            {
                ["main"] =
                [
                    new("Greyfield Central", "Platform 1", centralDistance, "central"),
                    new("Riverside", "Platform 6", MainMetrics.Starts[6] + 70),
                    new("East Market", "Platform 7", MainMetrics.Starts[7] + 105)
                ],
                ["north"] =
                [
                    new("Greyfield Central", "Platform 2", centralDistance, "central"),
                    new("North Junction", "Platform 8", NorthMetrics.Starts[4] + 100),
                    new("North Quay", "Platform 9", NorthMetrics.Starts[5] + 130)
                ],
                ["south"] =
                [
                    new("Greyfield Central", "Platform 3", centralDistance, "central"),
                    new("Market Cross", "Platform 10", SouthMetrics.Starts[4] + 100),
                    new("Canal Park", "Platform 11", SouthMetrics.Starts[5] + 100)
                ],
                ["airport"] =
                [
                    new("Greyfield Central", "Platform 4", centralDistance, "central"),
                    new("Airport Parkway", "Platform 12", AirportMetrics.Starts[7] + 110)
                ],
                ["harbor"] =
                [
                    new("Greyfield Central", "Platform 5", centralDistance, "central"),
                    new("Harbor Point", "Platform 13", HarborMetrics.Starts[7] + 105)
                ]
            }
            : new Dictionary<string, List<StationInfo>>(StringComparer.Ordinal)
        {
            ["main"] =
            [
                new("Greyfield Central", "Platform 1", MainMetrics.Starts[2] + 90),
                new("Riverside", "Platform 2", MainMetrics.Starts[6] + 80)
            ],
            ["north"] = [new("North Quay", "Platform 3", NorthMetrics.Starts[5] + 120)],
            ["south"] = [new("Canal Park", "Platform 4", SouthMetrics.Starts[5] + 100)]
        };

        _signals.AddRange(
        [
            new() { Id = "S01", Distance = 105, Aspect = SignalAspect.Green, Side = -1, RouteId = "common" },
            new() { Id = "S02", Distance = 340, Aspect = SignalAspect.Green, Side = 1, RouteId = "common" },
            new() { Id = "S03", Distance = 650, Aspect = SignalAspect.Green, Side = -1, RouteId = "common" },
            new() { Id = "S04", Distance = 1020, Aspect = SignalAspect.Green, Side = -1, RouteId = "main" },
            new() { Id = "S05", Distance = 1380, Aspect = SignalAspect.Green, Side = 1, RouteId = "main" },
            new() { Id = "S06", Distance = 1770, Aspect = SignalAspect.Green, Side = -1, RouteId = "main" },
            new() { Id = "S07", Distance = NorthMetrics.Starts[5] + 50, Aspect = SignalAspect.Green, Side = -1, RouteId = "north" },
            new() { Id = "S08", Distance = NorthMetrics.Starts[5] + 200, Aspect = SignalAspect.Green, Side = 1, RouteId = "north" },
            new() { Id = "S09", Distance = SouthMetrics.Starts[5] + 45, Aspect = SignalAspect.Green, Side = 1, RouteId = "south" },
            new() { Id = "S10", Distance = SouthMetrics.Starts[5] + 180, Aspect = SignalAspect.Green, Side = -1, RouteId = "south" }
        ]);
        if (Style == GameStyle.Regular) AddStationSignals();

        if (save is null)
            CreateInitialFleet();
        else
            Restore(save);
    }

    public double TrackLength { get; }
    public GameStyle Style { get; }
    public double TrainDistance => Trains.FirstOrDefault()?.Distance ?? 0;
    public bool TrainStopped => Trains.FirstOrDefault()?.Stopped ?? false;
    public string TrainStatus => Trains.FirstOrDefault()?.Status ?? "Waiting at the entrance";
    public int Balance { get; private set; } = StartingBalance;
    public double GameSeconds { get; private set; }
    public double RealSeconds { get; private set; }
    public double IncomePerMinute { get; private set; }
    public bool IsPaused { get; private set; }
    public GameMode Mode { get; private set; } = GameMode.Signaller;
    public BuildItemKind SelectedBuildItem { get; private set; } = BuildItemKind.StraightTrack;
    public bool DemolishMode { get; private set; }
    public IReadOnlyList<GameRoute> Routes => _routes;
    public IReadOnlyList<TrainService> Trains => _trains;
    public IReadOnlyList<Signal> Signals => _signals;
    public List<PlacedItem> PlacedItems { get; } = [];
    public IReadOnlyList<string> Notifications => _notifications;
    public double PreviewX => _previewX;
    public double PreviewY => _previewY;
    public int TrainCount => Trains.Count;
    public int RouteCount => _availableRoutes.Count;
    public int SelectedRotation { get; private set; }
    public event Action<TrainService>? TrainEnteredZone;
    public event Action<TrainService, StationInfo, bool>? TrainStationChanged;

    public string FormattedBalance => $"${Balance:N0}";

    public string FormattedGameClock
    {
        get
        {
            var elapsedMinutes = (long)(GameSeconds * SimulationClockRate / 60);
            var timeOfDay = 8 * 60L + elapsedMinutes;
            return $"{timeOfDay / 60:D2}:{timeOfDay % 60:D2}";
        }
    }

    public static double NormalizeDistance(double distance, double trackLength)
    {
        if (!double.IsFinite(distance) || !double.IsFinite(trackLength) || trackLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(trackLength));

        var result = distance % trackLength;
        return result < 0 ? result + trackLength : result;
    }

    public static double DistanceToNext(double from, double to, double trackLength)
    {
        return NormalizeDistance(to - from, trackLength);
    }

    public static bool CrossedMarker(double from, double marker, double advance, double trackLength)
    {
        if (advance <= 0) return false;
        var distance = DistanceToNext(from, marker, trackLength);
        return distance > 0 && distance <= advance;
    }

    public TrackPosition PositionAt(double distance) => PositionAt("main", distance);

    public TrackPosition PositionAt(string routeId, double distance)
    {
        var route = GetRoute(routeId);
        var metrics = _metrics[route.Id];
        distance = Math.Clamp(distance, 0, metrics.Total);
        for (var index = 0; index < metrics.Lengths.Length; index++)
        {
            var end = metrics.Starts[index] + metrics.Lengths[index];
            if (distance <= end || index == metrics.Lengths.Length - 1)
            {
                var start = route.Points[index];
                var next = route.Points[index + 1];
                var angle = Math.Atan2(next.Y - start.Y, next.X - start.X);
                var progress = (distance - metrics.Starts[index]) / metrics.Lengths[index];
                return new(
                    start.X + (next.X - start.X) * progress,
                    start.Y + (next.Y - start.Y) * progress,
                    angle);
            }
        }

        throw new InvalidOperationException("The track position could not be calculated.");
    }

    public TrackPosition SignalPosition(Signal signal)
    {
        var routeId = signal.RouteId == "common" ? "main" : signal.RouteId;
        return OffsetFromTrack(PositionAt(routeId, signal.Distance), signal.Side * 20);
    }

    public StationInfo? NextStation(TrainService train)
    {
        return _stations[train.RouteId].FirstOrDefault(station => station.Distance > train.Distance)
            ?? new StationInfo(GetRoute(train.RouteId).Destination, GetRoute(train.RouteId).ExitName, RouteMetricsFor(train.RouteId).Total);
    }

    public IReadOnlyList<StationInfo> StationsForRoute(string routeId) => _stations[routeId];
    public IEnumerable<(string RouteId, StationInfo Station)> AllStations =>
        _stations.SelectMany(route => route.Value.Select(station => (route.Key, station)));

    public StationInfo? StoppedAtStation(TrainService train)
    {
        return train.StoppedAtStationDistance is { } distance
            ? _stations[train.RouteId].FirstOrDefault(station => station.Distance == distance)
            : null;
    }

    public PlatformIndicator PlatformIndicatorFor(string routeId, StationInfo station)
    {
        var train = _trains.FirstOrDefault(candidate =>
            candidate.RouteId == routeId && candidate.StoppedAtStationDistance == station.Distance);
        if (train is null) return PlatformIndicator.Clear;
        if (train.StationDwellSeconds >= StationDelayedSeconds) return PlatformIndicator.Delayed;
        return train.StationDwellSeconds < StationDoorOpenSeconds
            ? PlatformIndicator.DoorsOpen
            : PlatformIndicator.ReadyToDepart;
    }

    public bool IsOperationalSignalItem(PlacedItem item) =>
        _operationalSignalLocations.Contains((item.X, item.Y));

    public string LatencyText(TrainService train)
    {
        var sign = train.LatencySeconds < 0 ? "EARLY " : train.LatencySeconds > 0 ? "LATE " : "ON TIME";
        var seconds = (int)Math.Abs(train.LatencySeconds);
        return train.LatencySeconds == 0 ? sign : $"{sign}{seconds / 60:D2}:{seconds % 60:D2}";
    }

    public void SetMode(GameMode mode)
    {
        Mode = mode;
        DemolishMode = false;
    }

    public void TogglePause()
    {
        IsPaused = !IsPaused;
        _notifications.Add(IsPaused ? "Simulation paused." : "Simulation resumed.");
    }

    public bool SetSignalAspect(string id, SignalAspect aspect)
    {
        var signal = Signals.FirstOrDefault(signal => signal.Id == id);
        if (signal is null || !Enum.IsDefined(aspect)) return false;
        signal.Aspect = aspect;
        _notifications.Add($"{signal.Id} set to {aspect.ToString().ToUpperInvariant()}.");
        return true;
    }

    public bool ToggleSignal(string id)
    {
        var signal = Signals.FirstOrDefault(signal => signal.Id == id);
        if (signal is null) return false;

        signal.Aspect = signal.Aspect switch
        {
            SignalAspect.Red => SignalAspect.Yellow,
            SignalAspect.Yellow => SignalAspect.Green,
            _ => SignalAspect.Red
        };
        _notifications.Add($"{signal.Id} set to {signal.Aspect.ToString().ToUpperInvariant()}.");
        return true;
    }

    public void SelectBuildItem(BuildItemKind item)
    {
        if (!BuildItems.Any(definition => definition.Kind == item))
            throw new ArgumentOutOfRangeException(nameof(item));

        SelectedBuildItem = item;
        DemolishMode = false;
    }

    public void RotateSelectedItem() => SelectedRotation = (SelectedRotation + 90) % 360;

    public void ToggleDemolishMode() => DemolishMode = !DemolishMode;

    public void SetPreview(double x, double y)
    {
        _previewX = SnapToGrid(x, WorldWidth);
        _previewY = SnapToGrid(y, WorldHeight);
    }

    public void ClearPreview()
    {
        _previewX = double.NaN;
        _previewY = double.NaN;
    }

    public bool PlaceSelectedItem(double x, double y)
    {
        if (Mode != GameMode.Editor || DemolishMode) return false;

        var definition = BuildItems.Single(item => item.Kind == SelectedBuildItem);
        if (Balance < definition.Cost)
        {
            _notifications.Add($"Not enough funds — {definition.Name} costs ${definition.Cost:N0}.");
            return false;
        }

        var snappedX = SnapToGrid(x, WorldWidth);
        var snappedY = SnapToGrid(y, WorldHeight);
        if (PlacedItems.Any(item => Distance(item.X - snappedX, item.Y - snappedY) < GridSize * 0.75))
        {
            _notifications.Add("That tile is already occupied.");
            return false;
        }

        if (IsWithinTrackClearance(snappedX, snappedY))
        {
            _notifications.Add("Build on a clear grid tile, away from the existing railway.");
            return false;
        }

        Balance -= definition.Cost;
        PlacedItems.Add(new(SelectedBuildItem, snappedX, snappedY, definition.Cost, SelectedRotation));
        _notifications.Add($"{definition.Name} placed for ${definition.Cost:N0}.");
        RebuildEditorRoutes(notify: true);
        return true;
    }

    public bool DemolishNearest(double x, double y)
    {
        if (Mode != GameMode.Editor || !DemolishMode) return false;

        var snappedX = SnapToGrid(x, WorldWidth);
        var snappedY = SnapToGrid(y, WorldHeight);
        var index = -1;
        var nearest = 25d;
        for (var itemIndex = 0; itemIndex < PlacedItems.Count; itemIndex++)
        {
            var item = PlacedItems[itemIndex];
            var distance = Distance(item.X - snappedX, item.Y - snappedY);
            if (distance < nearest)
            {
                nearest = distance;
                index = itemIndex;
            }
        }

        if (index < 0)
        {
            _notifications.Add("Select one of your placed pieces to demolish it.");
            return false;
        }

        var removed = PlacedItems[index];
        PlacedItems.RemoveAt(index);
        var refund = removed.Cost / 2;
        Balance = (int)Math.Min(int.MaxValue, (long)Balance + refund);
        _notifications.Add($"Piece demolished — ${refund:N0} refunded.");
        RebuildEditorRoutes(notify: true);
        return true;
    }

    public void Advance(double deltaSeconds)
    {
        if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0 || IsPaused || Mode == GameMode.Editor)
            return;

        var remaining = Math.Min(deltaSeconds, 0.25);
        while (remaining > 0)
        {
            var step = Math.Min(remaining, 1d / 60);
            AdvanceStep(step);
            remaining -= step;
        }

        RecalculateIncome();
    }

    public GameSaveData Save()
    {
        var trains = Trains.Select(ToSaveData).ToArray();
        var primaryTrain = trains.FirstOrDefault() ?? new TrainSaveData(0, false, "Waiting at the entrance");
        return new(
            SaveVersion,
            Mode,
            Balance,
            GameSeconds,
            RealSeconds,
            _recentEarnings.Sum(earning => earning.Amount),
            PlacedItems.ToArray(),
            Signals.Select(signal => new SignalSaveData(signal.Id, signal.Aspect)).ToArray(),
            [],
            primaryTrain,
            trains,
            Style);
    }

    public IReadOnlyList<string> DrainNotifications()
    {
        var notifications = _notifications.ToArray();
        _notifications.Clear();
        return notifications;
    }

    private void AddStationSignals()
    {
        var stationGroups = AllStations.GroupBy(item =>
            string.IsNullOrWhiteSpace(item.Station.GroupId)
                ? $"{item.RouteId}/{item.Station.Name}"
                : item.Station.GroupId);
        var signalNumber = 1;
        foreach (var group in stationGroups)
        {
            var (routeId, station) = group.First();
            var signalRouteId = group.Key == "central" ? "common" : routeId;
            var beforeDistance = station.Distance - 55;
            var afterDistance = station.Distance + 55;
            if (beforeDistance <= 0 || afterDistance >= RouteMetricsFor(routeId).Total) continue;

            _signals.Add(new Signal
            {
                Id = $"ST{signalNumber:D2}A",
                RouteId = signalRouteId,
                Distance = beforeDistance,
                Side = signalNumber % 2 == 0 ? 1 : -1,
                Aspect = SignalAspect.Green
            });
            _signals.Add(new Signal
            {
                Id = $"ST{signalNumber:D2}D",
                RouteId = signalRouteId,
                Distance = afterDistance,
                Side = signalNumber % 2 == 0 ? -1 : 1,
                Aspect = SignalAspect.Green
            });
            signalNumber++;
        }
    }

    private void CreateInitialFleet()
    {
        var trains = new List<TrainService>(MaximumTrains);
        for (var index = 0; index < MaximumTrains; index++)
        {
            var profileIndex = index % ServiceNames.Length;
            trains.Add(CreateTrain(
                profileIndex,
                index * (Style == GameStyle.Regular ? 85 : 155),
                $"TR{index + 1:D2}",
                stopped: false,
                "In service",
                latencySeconds: index % 2 == 0 ? 0 : 20));
        }

        _trains.AddRange(trains);
    }

    private void RebuildEditorRoutes(bool notify)
    {
        var previousAvailableRouteIds = _availableRoutes
            .Select(route => route.Id)
            .ToHashSet(StringComparer.Ordinal);
        var existingSignalAspects = _signals
            .Where(signal => signal.RouteId.StartsWith("editor-", StringComparison.Ordinal))
            .ToDictionary(signal => signal.Id, signal => signal.Aspect, StringComparer.Ordinal);
        _signals.RemoveAll(signal => signal.RouteId.StartsWith("editor-", StringComparison.Ordinal));
        _operationalSignalLocations.Clear();
        var oldPositions = _trains
            .Where(train => train.RouteId.StartsWith("editor-", StringComparison.Ordinal))
            .ToDictionary(train => train.Id, train => PositionAt(train.RouteId, train.Distance));
        var editorRoutes = BuildEditorRoutes();
        var editorRouteIds = editorRoutes.Select(route => route.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var route in _routes.Where(route => route.Id.StartsWith("editor-", StringComparison.Ordinal)).ToArray())
        {
            _routes.Remove(route);
            _metrics.Remove(route.Id);
            _stations.Remove(route.Id);
        }

        foreach (var route in editorRoutes)
        {
            _routes.Add(route);
            var metrics = MeasureRoute(route.Points);
            _metrics.Add(route.Id, metrics);
            _stations.Add(route.Id, [new("User-built station", "Platform 1", metrics.Total)]);
            if (notify && !previousAvailableRouteIds.Contains(route.Id))
                _notifications.Add($"Operational service opened: {route.EntranceName} to {route.Destination}.");
        }

        foreach (var item in PlacedItems
            .Where(item => item.Kind == BuildItemKind.Signal)
            .OrderBy(item => item.Y)
            .ThenBy(item => item.X))
        {
            var nearestRoute = editorRoutes
                .Select(route => (Route: route, Projection: ProjectToRoute(route.Id, item.X, item.Y)))
                .Where(candidate => candidate.Projection.DistanceSquared <= 50 * 50)
                .OrderBy(candidate => candidate.Projection.DistanceSquared)
                .FirstOrDefault();
            if (nearestRoute.Route is null) continue;

            var id = $"E{(int)item.X:D4}-{(int)item.Y:D4}";
            _signals.Add(new Signal
            {
                Id = id,
                RouteId = nearestRoute.Route.Id,
                Distance = nearestRoute.Projection.Distance,
                Side = nearestRoute.Projection.Side,
                Aspect = existingSignalAspects.GetValueOrDefault(id, SignalAspect.Green)
            });
            _operationalSignalLocations.Add((item.X, item.Y));
        }

        _availableRoutes.Clear();
        _availableRoutes.AddRange(_routes);
        if (notify && previousAvailableRouteIds.Any(id =>
            id.StartsWith("editor-", StringComparison.Ordinal) && !_availableRoutes.Any(route => route.Id == id)))
            _notifications.Add("An editor route was disconnected. Trains on it were withdrawn safely.");

        foreach (var train in _trains
            .Where(train => train.RouteId.StartsWith("editor-", StringComparison.Ordinal))
            .ToArray())
        {
            if (!editorRouteIds.Contains(train.RouteId) || !oldPositions.TryGetValue(train.Id, out var position))
            {
                _trains.Remove(train);
                _notifications.Add($"{train.Headcode} was withdrawn because its editor route is no longer connected.");
                continue;
            }

            train.Distance = NearestDistanceOnRoute(train.RouteId, position);
        }
    }

    private List<GameRoute> BuildEditorRoutes()
    {
        var trackTiles = PlacedItems
            .Where(item => item.Kind is BuildItemKind.StraightTrack or BuildItemKind.CurvedTrack)
            .ToDictionary(item => new GridCell((int)item.X, (int)item.Y));
        var routes = new List<GameRoute>();
        var depots = PlacedItems.Where(item => item.Kind == BuildItemKind.Depot);
        var stations = PlacedItems.Where(item => item.Kind == BuildItemKind.Station);

        foreach (var depot in depots)
        foreach (var station in stations)
        {
            var path = FindEditorTrackPath(depot, station, trackTiles);
            if (path is null) continue;

            var depotX = ((int)depot.X).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var depotY = ((int)depot.Y).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var stationX = ((int)station.X).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var stationY = ((int)station.Y).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var points = new List<TrackPoint>(path.Count + 2)
            {
                new(depot.X, depot.Y)
            };
            points.AddRange(path.Select(cell => new TrackPoint(cell.X, cell.Y)));
            points.Add(new(station.X, station.Y));
            routes.Add(new(
                $"editor-{depotX}-{depotY}-{stationX}-{stationY}",
                "EDITOR SERVICE",
                $"Station {stationX},{stationY}",
                "USER-BUILT EXIT",
                points,
                $"Depot {depotX},{depotY}"));
        }

        return routes;
    }

    private static List<GridCell>? FindEditorTrackPath(
        PlacedItem depot,
        PlacedItem station,
        IReadOnlyDictionary<GridCell, PlacedItem> trackTiles)
    {
        var startCells = GetTerminalTrackCells(depot, trackTiles);
        var destinationCells = GetTerminalTrackCells(station, trackTiles).ToHashSet();
        if (startCells.Count == 0 || destinationCells.Count == 0) return null;

        var parents = new Dictionary<GridCell, GridCell?>();
        var queue = new Queue<GridCell>();
        foreach (var cell in startCells)
        {
            if (!parents.TryAdd(cell, null)) continue;
            queue.Enqueue(cell);
        }

        GridCell? destination = null;
        while (queue.TryDequeue(out var current))
        {
            if (destinationCells.Contains(current))
            {
                destination = current;
                break;
            }

            var tile = trackTiles[current];
            foreach (var (direction, neighbor) in GetTrackNeighbors(current, tile, trackTiles))
            {
                if (!parents.TryAdd(neighbor, current)) continue;
                queue.Enqueue(neighbor);
            }
        }

        if (destination is null) return null;
        var path = new List<GridCell>();
        var cellInPath = destination.Value;
        while (true)
        {
            path.Add(cellInPath);
            if (parents[cellInPath] is not { } parent) break;
            cellInPath = parent;
        }
        path.Reverse();
        return path;
    }

    private static List<GridCell> GetTerminalTrackCells(
        PlacedItem terminal,
        IReadOnlyDictionary<GridCell, PlacedItem> trackTiles)
    {
        var cells = new List<GridCell>();
        var terminalCell = new GridCell((int)terminal.X, (int)terminal.Y);
        foreach (var direction in TrackDirections.All)
        {
            var neighbor = terminalCell.Offset(direction);
            if (!trackTiles.TryGetValue(neighbor, out var tile)
                || !TrackPorts(tile).Contains(TrackDirections.Opposite(direction)))
                continue;
            cells.Add(neighbor);
        }
        return cells;
    }

    private static IEnumerable<(TrackDirection Direction, GridCell Cell)> GetTrackNeighbors(
        GridCell cell,
        PlacedItem tile,
        IReadOnlyDictionary<GridCell, PlacedItem> trackTiles)
    {
        foreach (var direction in TrackPorts(tile))
        {
            var neighbor = cell.Offset(direction);
            if (trackTiles.TryGetValue(neighbor, out var adjacent)
                && TrackPorts(adjacent).Contains(TrackDirections.Opposite(direction)))
                yield return (direction, neighbor);
        }
    }

    private static TrackDirection[] TrackPorts(PlacedItem tile)
    {
        if (tile.Kind == BuildItemKind.StraightTrack)
            return tile.Rotation is 0 or 180
                ? [TrackDirection.East, TrackDirection.West]
                : [TrackDirection.North, TrackDirection.South];

        return tile.Rotation switch
        {
            90 => [TrackDirection.East, TrackDirection.South],
            180 => [TrackDirection.South, TrackDirection.West],
            270 => [TrackDirection.West, TrackDirection.North],
            _ => [TrackDirection.North, TrackDirection.East]
        };
    }

    private double NearestDistanceOnRoute(string routeId, TrackPosition position)
    {
        return ProjectToRoute(routeId, position.X, position.Y).Distance;
    }

    private RouteProjection ProjectToRoute(string routeId, double x, double y)
    {
        var route = GetRoute(routeId);
        var metrics = _metrics[routeId];
        var nearestDistance = 0d;
        var nearestDistanceSquared = double.PositiveInfinity;
        var nearestPoint = new TrackPosition(0, 0, 0);
        var nearestSide = -1d;
        for (var index = 0; index < route.Points.Count - 1; index++)
        {
            var start = route.Points[index];
            var end = route.Points[index + 1];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var progress = Math.Clamp(
                ((x - start.X) * dx + (y - start.Y) * dy)
                / (dx * dx + dy * dy),
                0,
                1);
            var projectedX = start.X + progress * dx;
            var projectedY = start.Y + progress * dy;
            var distanceSquared = (x - projectedX) * (x - projectedX) + (y - projectedY) * (y - projectedY);
            if (distanceSquared >= nearestDistanceSquared) continue;
            nearestDistanceSquared = distanceSquared;
            nearestDistance = metrics.Starts[index] + metrics.Lengths[index] * progress;
            var angle = Math.Atan2(dy, dx);
            nearestPoint = new(projectedX, projectedY, angle);
            nearestSide = dx * (y - projectedY) - dy * (x - projectedX) >= 0 ? 1 : -1;
        }
        return new(nearestDistance, nearestDistanceSquared, nearestPoint, nearestSide);
    }

    private void AdvanceStep(double deltaSeconds)
    {
        RealSeconds += deltaSeconds;
        GameSeconds += deltaSeconds;
        var crossedTrains = new List<TrainService>();
        var finishedTrains = new List<TrainService>();
        var earnings = 0d;

        foreach (var train in Trains.OrderByDescending(train => train.Distance).ToArray())
        {
            var route = GetRoute(train.RouteId);
            var routeLength = RouteMetricsFor(train.RouteId).Total;
            if (Style == GameStyle.Regular && train.StoppedAtStationDistance is not null)
            {
                var station = StoppedAtStation(train);
                if (station is null)
                {
                    train.StoppedAtStationDistance = null;
                    train.StationDwellSeconds = 0;
                }
                else
                {
                    train.StationDwellSeconds += deltaSeconds;
                    train.Stopped = true;
                    if (train.StationDwellSeconds < StationMinimumDwellSeconds)
                    {
                        train.Status = train.StationDwellSeconds < StationDoorOpenSeconds
                            ? $"At {station.Name} · doors open"
                            : $"Preparing to depart {station.Name}";
                        continue;
                    }

                    var departureObstacle = FindNextObstacle(train);
                    var departureGap = FindFollowingTrainGap(train);
                    var signalHoldsPlatform = departureObstacle is { Distance: <= SafetyDistance + 55 };
                    var trafficHoldsPlatform = departureGap is { } platformTrafficGap
                        && platformTrafficGap <= SafetyDistance * 2;
                    if (signalHoldsPlatform || trafficHoldsPlatform)
                    {
                        train.LatencySeconds += deltaSeconds;
                        train.Status = signalHoldsPlatform
                            ? $"Ready at {station.Name} · held at signal {departureObstacle!.Value.Id}"
                            : $"Ready at {station.Name} · waiting for traffic";
                        continue;
                    }

                    train.StoppedAtStationDistance = null;
                    train.StationDwellSeconds = 0;
                    train.Stopped = false;
                    train.Status = $"Departing {station.Name}";
                    TrainStationChanged?.Invoke(train, station, false);
                }
            }

            if (train.Distance >= routeLength)
            {
                finishedTrains.Add(train);
                _notifications.Add($"{train.Headcode} departed the zone at {route.ExitName}.");
                continue;
            }

            var caution = NextSignalAspect(train) == SignalAspect.Yellow ? 0.55 : 1;
            var advance = train.Speed * caution * deltaSeconds;
            var obstacle = FindNextObstacle(train);
            var followingGap = FindFollowingTrainGap(train);
            var obstacleDistance = obstacle?.Distance ?? double.PositiveInfinity;
            var followingIsClosest = followingGap is { } gap && gap < obstacleDistance;
            if (followingIsClosest && followingGap is { } safeGap)
                obstacleDistance = safeGap;

            var nextStation = Style == GameStyle.Regular
                ? _stations[train.RouteId].FirstOrDefault(station => station.Distance > train.Distance)
                : null;
            var distanceToStation = nextStation is null
                ? double.PositiveInfinity
                : nextStation.Distance - train.Distance;
            var followingGapAllowsStation = followingGap is null
                || followingGap.Value >= distanceToStation + SafetyDistance;
            if (nextStation is not null && distanceToStation <= advance
                && distanceToStation < obstacleDistance && followingGapAllowsStation)
            {
                var stationArrivalPreviousDistance = train.Distance;
                train.Distance = nextStation.Distance;
                train.Stopped = true;
                train.StoppedAtStationDistance = nextStation.Distance;
                train.StationDwellSeconds = 0;
                train.Status = $"At {nextStation.Name} · doors open";
                foreach (var signal in Signals.Where(signal => IsSignalOnTrainRoute(signal, train)))
                {
                    if (signal.Aspect != SignalAspect.Red
                        && CrossedOpenMarker(stationArrivalPreviousDistance, signal.Distance, distanceToStation))
                    {
                        earnings += SectionPayment;
                        _notifications.Add($"{train.Headcode} safely passed {signal.Id} — ${SectionPayment:N0}.");
                    }
                }
                earnings += DestinationPayment;
                _notifications.Add($"{train.Headcode} arrived at {nextStation.Name}, {nextStation.Platform}.");
                TrainStationChanged?.Invoke(train, nextStation, true);
                continue;
            }

            train.Stopped = obstacleDistance <= SafetyDistance + advance;
            if (train.Stopped)
            {
                var distanceToStop = Math.Max(0, obstacleDistance - SafetyDistance);
                train.Distance = Math.Min(routeLength, train.Distance + distanceToStop);
                train.LatencySeconds += deltaSeconds;
                train.Status = !followingIsClosest && obstacle is not null
                    ? $"Held at signal {obstacle.Value.Id}"
                    : followingIsClosest
                        ? "Regulating behind traffic"
                        : "Approaching a red signal";
                continue;
            }

            var previousDistance = train.Distance;
            train.Distance = Math.Min(routeLength, train.Distance + advance);
            train.Status = train.LatencySeconds > 60 ? "Delayed in service" : "In service";

            foreach (var signal in Signals.Where(signal => IsSignalOnTrainRoute(signal, train)))
            {
                if (signal.Aspect != SignalAspect.Red
                    && CrossedOpenMarker(previousDistance, signal.Distance, advance))
                {
                    earnings += SectionPayment;
                    _notifications.Add($"{train.Headcode} safely passed {signal.Id} — ${SectionPayment:N0}.");
                }
            }

            foreach (var station in _stations[train.RouteId])
            {
                if (CrossedOpenMarker(previousDistance, station.Distance, advance))
                {
                    earnings += DestinationPayment;
                    _notifications.Add($"{train.Headcode} passed {station.Name}, {station.Platform}.");
                }
            }

            if (train.Distance >= routeLength)
                finishedTrains.Add(train);
        }

        foreach (var train in finishedTrains.Distinct())
            _trains.Remove(train);

        if (earnings > 0)
        {
            Balance = (int)Math.Min(int.MaxValue, (long)Balance + (int)earnings);
            _recentEarnings.Enqueue((RealSeconds, earnings));
        }

        while (_recentEarnings.TryPeek(out var oldEarning) && oldEarning.Time < RealSeconds - 60)
            _recentEarnings.Dequeue();

        _spawnCountdown -= deltaSeconds;
        if (_spawnCountdown <= 0 && Trains.Count < MaximumTrains)
        {
            var profileIndex = (_nextTrainNumber - 1) % ServiceNames.Length;
            var route = _availableRoutes[(_nextTrainNumber - 1) % _availableRoutes.Count];
            var train = CreateTrain(
                profileIndex,
                0,
                $"TR{_nextTrainNumber++:D2}",
                false,
                "Entering the control area",
                0,
                route.Id);
            _trains.Add(train);
            crossedTrains.Add(train);
            _notifications.Add($"{train.Headcode} entered the control area from {route.EntranceName}.");
            _spawnCountdown = 8;
        }

        foreach (var train in crossedTrains)
            TrainEnteredZone?.Invoke(train);
    }

    private double? FindFollowingTrainGap(TrainService train)
    {
        var ahead = Trains
            .Where(other => other.Id != train.Id && IsOnSameTrackSection(train, other))
            .Select(other => other.Distance - train.Distance)
            .Where(distance => distance > 0)
            .DefaultIfEmpty(double.PositiveInfinity)
            .Min();
        return double.IsPositiveInfinity(ahead) ? null : ahead;
    }

    private bool IsOnSameTrackSection(TrainService first, TrainService second)
    {
        if (first.RouteId == second.RouteId) return true;
        var sharedLength = SharedPrefixLength(first.RouteId, second.RouteId);
        return sharedLength > 0
            && first.Distance < sharedLength + SafetyDistance
            && second.Distance < sharedLength + SafetyDistance;
    }

    private bool IsSignalOnTrainRoute(Signal signal, TrainService train)
    {
        if (signal.RouteId == train.RouteId) return true;
        var signalRoute = signal.RouteId == "common" ? "main" : signal.RouteId;
        var sharedLength = SharedPrefixLength(train.RouteId, signalRoute);
        return sharedLength > 0
            && signal.Distance < sharedLength
            && train.Distance < sharedLength;
    }

    private double SharedPrefixLength(string firstRouteId, string secondRouteId)
    {
        var first = GetRoute(firstRouteId).Points;
        var second = GetRoute(secondRouteId).Points;
        var commonPointCount = 0;
        while (commonPointCount < first.Count && commonPointCount < second.Count
            && first[commonPointCount] == second[commonPointCount])
            commonPointCount++;
        if (commonPointCount < 2) return 0;
        return MeasureRoute(first.Take(commonPointCount).ToArray()).Total;
    }

    private SignalAspect NextSignalAspect(TrainService train)
    {
        return Signals
            .Where(signal => IsSignalOnTrainRoute(signal, train))
            .Select(signal => new
            {
                signal.Aspect,
                Distance = signal.Distance - train.Distance
            })
            .Where(item => item.Distance > 0)
            .OrderBy(item => item.Distance)
            .Select(item => item.Aspect)
            .FirstOrDefault(SignalAspect.Green);
    }

    private Obstacle? FindNextObstacle(TrainService train)
    {
        return Signals
            .Where(signal => signal.Aspect == SignalAspect.Red && IsSignalOnTrainRoute(signal, train))
            .Select(signal => new Obstacle(signal.Distance - train.Distance, signal.Id))
            .Where(obstacle => obstacle.Distance > 0)
            .OrderBy(obstacle => obstacle.Distance)
            .Select(obstacle => (Obstacle?)obstacle)
            .FirstOrDefault();
    }

    private void Restore(GameSaveData save)
    {
        if (save.Version is < 1 or > SaveVersion)
            throw new InvalidDataException($"Unsupported save version {save.Version}.");
        if (save.Train is null || save.Signals is null || save.Points is null || save.PlacedItems is null
            || string.IsNullOrWhiteSpace(save.Train.Status)
            || save.Balance < 0 || !double.IsFinite(save.GameSeconds) || save.GameSeconds < 0
            || !double.IsFinite(save.RealSeconds) || save.RealSeconds < 0
            || save.EarnedInWindow < 0 || !double.IsFinite(save.EarnedInWindow)
            || (save.Trains is not null && save.Trains.Count > 100))
            throw new InvalidDataException("The saved game contains invalid values.");

        if (!Enum.IsDefined(save.Mode))
            throw new InvalidDataException("The saved game contains an invalid game mode.");
        if (save.Version >= 3 && !Enum.IsDefined(save.Style))
            throw new InvalidDataException("The saved game contains an invalid game style.");
        Balance = save.Balance;
        Mode = save.Mode;
        GameSeconds = save.GameSeconds;
        RealSeconds = save.RealSeconds;
        if (save.PlacedItems.Count > 10_000)
            throw new InvalidDataException("The saved game contains too many trackside items.");
        var railwayOverlaps = 0;
        var pieceOverlaps = 0;
        var omittedLocations = new List<string>(3);
        foreach (var item in save.PlacedItems)
        {
            if (item is null)
                throw new InvalidDataException("The saved game contains an invalid trackside item.");
            var definition = BuildItems
                .Where(buildItem => buildItem.Kind == item.Kind)
                .Select(buildItem => (BuildItemDefinition?)buildItem)
                .FirstOrDefault();
            var isLegacySwitch = save.Version == 1
                && item.Kind == BuildItemKind.Switch
                && item.Cost == 500;
            if ((!isLegacySwitch && (definition is null || definition.Value.Cost != item.Cost))
                || !double.IsFinite(item.X) || !double.IsFinite(item.Y)
                || item.X < 0 || item.X > WorldWidth || item.Y < 0 || item.Y > WorldHeight
                || item.Rotation is not (0 or 90 or 180 or 270))
                throw new InvalidDataException("The saved game contains an invalid trackside item.");
            var overlapsRailway = save.Version >= SaveVersion && IsWithinTrackClearance(item.X, item.Y);
            var overlapsPiece = PlacedItems.Any(existing =>
                Distance(existing.X - item.X, existing.Y - item.Y) < GridSize * 0.75);
            if (overlapsRailway || overlapsPiece)
            {
                if (overlapsRailway) railwayOverlaps++;
                else pieceOverlaps++;
                if (omittedLocations.Count < 3)
                    omittedLocations.Add($"({item.X:0}, {item.Y:0})");
                continue;
            }
            PlacedItems.Add(item);
        }

        var omittedCount = railwayOverlaps + pieceOverlaps;
        if (omittedCount > 0)
        {
            var reasons = new List<string>(2);
            if (railwayOverlaps > 0) reasons.Add($"{railwayOverlaps} overlapping the railway");
            if (pieceOverlaps > 0) reasons.Add($"{pieceOverlaps} overlapping other built pieces");
            var locations = string.Join(", ", omittedLocations);
            var locationSummary = omittedCount > omittedLocations.Count ? $"{locations}, ..." : locations;
            _notifications.Add(
                $"{omittedCount} saved trackside item(s) ({string.Join("; ", reasons)}) were omitted at {locationSummary}. The rest of your save was restored.");
        }

        RebuildEditorRoutes(notify: false);
        foreach (var signalState in save.Signals)
        {
            if (signalState is null)
                throw new InvalidDataException("The saved game contains an invalid signal.");
            var id = save.Version == 1
                ? signalState.Id switch { "S1" => "S02", "S2" => "S05", _ => signalState.Id }
                : signalState.Id;
            var signal = Signals.FirstOrDefault(item => item.Id == id);
            if (signal is null || !Enum.IsDefined(signalState.Aspect))
                throw new InvalidDataException($"The saved game contains an invalid signal '{signalState.Id}'.");
            signal.Aspect = signalState.Aspect;
        }

        var trainStates = save.Trains is { Count: > 0 } ? save.Trains : [save.Train];
        var trains = new List<TrainService>(trainStates.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var trainState in trainStates)
        {
            var distance = trainState is not null && save.Version == 1 && double.IsFinite(trainState.Distance)
                ? Math.Clamp(trainState.Distance, 0, MainMetrics.Total)
                : trainState?.Distance ?? double.NaN;
            if (trainState is null || string.IsNullOrWhiteSpace(trainState.Status)
                || string.IsNullOrWhiteSpace(trainState.Id) || !ids.Add(trainState.Id)
                || string.IsNullOrWhiteSpace(trainState.Headcode)
                || string.IsNullOrWhiteSpace(trainState.ServiceName)
                || string.IsNullOrWhiteSpace(trainState.TrainType)
                || !_metrics.TryGetValue(trainState.RouteId, out var metrics)
                || !double.IsFinite(distance) || distance < 0 || distance > metrics.Total
                || !double.IsFinite(trainState.Speed) || trainState.Speed is < 10 or > 120
                || !double.IsFinite(trainState.LatencySeconds)
                || !double.IsFinite(trainState.StationDwellSeconds) || trainState.StationDwellSeconds < 0
                || (trainState.StoppedAtStationDistance is { } stationDistance
                    && (!trainState.Stopped
                        || !double.IsFinite(stationDistance)
                        || Math.Abs(distance - stationDistance) > 0.01
                        || !_stations[trainState.RouteId].Any(station => station.Distance == stationDistance))))
                throw new InvalidDataException("The saved game contains an invalid train.");
            trains.Add(new TrainService
            {
                Id = trainState.Id,
                Headcode = trainState.Headcode,
                ServiceName = trainState.ServiceName,
                TrainType = trainState.TrainType,
                RouteId = trainState.RouteId,
                Speed = trainState.Speed,
                Distance = distance,
                LatencySeconds = trainState.LatencySeconds,
                Stopped = trainState.Stopped,
                Status = trainState.Status,
                StationDwellSeconds = trainState.StationDwellSeconds,
                StoppedAtStationDistance = trainState.StoppedAtStationDistance
            });
        }

        _trains.AddRange(trains);
        _nextTrainNumber = trains
            .Select(train => int.TryParse(train.Id.AsSpan(2), out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        if (save.EarnedInWindow > 0) _recentEarnings.Enqueue((RealSeconds, save.EarnedInWindow));
    }

    private TrainService CreateTrain(
        int profileIndex,
        double distance,
        string id,
        bool stopped,
        string status,
        double latencySeconds,
        string? routeId = null)
    {
        return new TrainService
        {
            Id = id,
            Headcode = Headcodes[profileIndex],
            ServiceName = ServiceNames[profileIndex],
            TrainType = TrainTypes[profileIndex],
            RouteId = routeId ?? RouteIds[profileIndex % (Style == GameStyle.Regular ? RouteIds.Length : 3)],
            Speed = ServiceSpeeds[profileIndex],
            Distance = distance,
            LatencySeconds = latencySeconds,
            Stopped = stopped,
            Status = status
        };
    }

    private static TrainSaveData ToSaveData(TrainService train)
    {
        return new(
            train.Distance,
            train.Stopped,
            train.Status,
            train.Id,
            train.Headcode,
            train.ServiceName,
            train.TrainType,
            train.RouteId,
            train.Speed,
            train.LatencySeconds,
            train.StationDwellSeconds,
            train.StoppedAtStationDistance);
    }

    private void RecalculateIncome()
    {
        IncomePerMinute = _recentEarnings.Sum(earning => earning.Amount);
    }

    private static bool CrossedOpenMarker(double from, double marker, double advance)
    {
        return advance > 0 && marker > from && marker <= from + advance;
    }

    private GameRoute GetRoute(string routeId)
    {
        return Routes.FirstOrDefault(route => route.Id == routeId)
            ?? throw new ArgumentOutOfRangeException(nameof(routeId), "Unknown railway route.");
    }

    private RouteMetrics RouteMetricsFor(string routeId) => _metrics[routeId];

    private double SnapToGrid(double coordinate, double dimension)
    {
        return Math.Clamp(Math.Round(coordinate / GridSize, MidpointRounding.AwayFromZero) * GridSize,
            GridSize / 2, dimension - GridSize / 2);
    }

    private bool IsWithinTrackClearance(double x, double y)
    {
        foreach (var route in Routes.Where(route =>
            !route.Id.StartsWith("editor-", StringComparison.Ordinal)))
        {
            for (var index = 0; index < route.Points.Count - 1; index++)
            {
                var start = route.Points[index];
                var end = route.Points[index + 1];
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var lengthSquared = dx * dx + dy * dy;
                var progress = Math.Clamp(((x - start.X) * dx + (y - start.Y) * dy) / lengthSquared, 0, 1);
                var nearestX = start.X + progress * dx;
                var nearestY = start.Y + progress * dy;
                if (Distance(x - nearestX, y - nearestY) < 30) return true;
            }
        }

        return false;
    }

    private static TrackPosition OffsetFromTrack(TrackPosition position, double offset)
    {
        return new(
            position.X - Math.Sin(position.Angle) * offset,
            position.Y + Math.Cos(position.Angle) * offset,
            position.Angle);
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);

    private enum TrackDirection
    {
        North,
        East,
        South,
        West
    }

    private static class TrackDirections
    {
        public static readonly TrackDirection[] All =
        [
            TrackDirection.North,
            TrackDirection.East,
            TrackDirection.South,
            TrackDirection.West
        ];

        public static TrackDirection Opposite(TrackDirection direction)
        {
            return direction switch
            {
                TrackDirection.North => TrackDirection.South,
                TrackDirection.East => TrackDirection.West,
                TrackDirection.South => TrackDirection.North,
                _ => TrackDirection.East
            };
        }
    }

    private readonly record struct GridCell(int X, int Y)
    {
        public GridCell Offset(TrackDirection direction)
        {
            return direction switch
            {
                TrackDirection.North => this with { Y = Y - (int)GridSize },
                TrackDirection.East => this with { X = X + (int)GridSize },
                TrackDirection.South => this with { Y = Y + (int)GridSize },
                _ => this with { X = X - (int)GridSize }
            };
        }
    }

    private static RouteMetrics MeasureRoute(IReadOnlyList<TrackPoint> points)
    {
        var lengths = new double[points.Count - 1];
        var starts = new double[points.Count - 1];
        var total = 0d;
        for (var index = 0; index < points.Count - 1; index++)
        {
            starts[index] = total;
            var dx = points[index + 1].X - points[index].X;
            var dy = points[index + 1].Y - points[index].Y;
            lengths[index] = Math.Sqrt(dx * dx + dy * dy);
            total += lengths[index];
        }

        return new(lengths, starts, total);
    }

    private readonly record struct RouteMetrics(double[] Lengths, double[] Starts, double Total);
    private readonly record struct RouteProjection(
        double Distance,
        double DistanceSquared,
        TrackPosition Position,
        double Side);
    private readonly record struct Obstacle(double Distance, string Id);
}
