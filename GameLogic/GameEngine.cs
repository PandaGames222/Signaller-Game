namespace Signaller.GameLogic;

public enum GameMode
{
    Signaller,
    Editor
}

public enum SignalAspect
{
    Red,
    Yellow,
    Green
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

public sealed class Signal
{
    public required string Id { get; init; }
    public required double Distance { get; init; }
    public SignalAspect Aspect { get; set; }
    public double Side { get; init; }
}

public sealed class Points
{
    public required string Id { get; init; }
    public required double Distance { get; init; }
    public PointDirection Direction { get; set; }
    public required PointDirection AssignedDirection { get; init; }
}

public sealed record PlacedItem(
    BuildItemKind Kind,
    double X,
    double Y,
    int Cost);

public sealed record TrainSaveData(double Distance, bool Stopped, string Status);

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
    TrainSaveData Train);

public sealed record SignalSaveData(string Id, SignalAspect Aspect);
public sealed record PointsSaveData(string Id, PointDirection Direction);

public sealed class GameEngine
{
    public const double WorldWidth = 1000;
    public const double WorldHeight = 600;
    public const double GridSize = 40;
    public const int SaveVersion = 1;
    public const int StartingBalance = 5000;
    public const double SafetyDistance = 15;
    public const double SimulationClockRate = 3;
    public const double TrainSpeed = 47;
    public const double SectionPayment = 25;
    public const double DestinationPayment = 100;

    public static readonly TrackPoint[] MainLoop =
    [
        new(220, 120),
        new(700, 120),
        new(830, 190),
        new(830, 370),
        new(730, 460),
        new(270, 460),
        new(170, 370),
        new(170, 210)
    ];

    public static readonly BuildItemDefinition[] BuildItems =
    [
        new(BuildItemKind.StraightTrack, "Straight track", "━", 100),
        new(BuildItemKind.CurvedTrack, "Curved track", "⌒", 100),
        new(BuildItemKind.Switch, "Switch / points", "⑂", 500),
        new(BuildItemKind.Signal, "Signal", "●", 300),
        new(BuildItemKind.Station, "Station", "▤", 2500),
        new(BuildItemKind.Depot, "Spawner / depot", "▰", 1000)
    ];

    private const double StationDistance = 1319;
    private const double TrackOffset = 20;
    private readonly double[] _segmentLengths;
    private readonly double[] _segmentStartDistances;
    private readonly Queue<(double Time, double Amount)> _recentEarnings = new();
    private readonly List<string> _notifications = [];
    private double _previewX = double.NaN;
    private double _previewY = double.NaN;

    public GameEngine(GameSaveData? save = null)
    {
        (_segmentLengths, _segmentStartDistances, TrackLength) = MeasureLoop();
        Signals =
        [
            new() { Id = "S1", Distance = 300, Aspect = SignalAspect.Green, Side = -1 },
            new() { Id = "S2", Distance = 710, Aspect = SignalAspect.Green, Side = 1 }
        ];
        Switches =
        [
            new()
            {
                Id = "P1",
                Distance = 480,
                Direction = PointDirection.Straight,
                AssignedDirection = PointDirection.Straight
            }
        ];

        if (save is not null) Restore(save);
    }

    public double TrackLength { get; }
    public double TrainDistance { get; private set; } = 80;
    public bool TrainStopped { get; private set; }
    public string TrainStatus { get; private set; } = "In service";
    public int Balance { get; private set; } = StartingBalance;
    public double GameSeconds { get; private set; }
    public double RealSeconds { get; private set; }
    public double IncomePerMinute { get; private set; }
    public bool IsPaused { get; private set; }
    public GameMode Mode { get; private set; } = GameMode.Signaller;
    public BuildItemKind SelectedBuildItem { get; private set; } = BuildItemKind.StraightTrack;
    public bool DemolishMode { get; private set; }
    public IReadOnlyList<Signal> Signals { get; }
    public IReadOnlyList<Points> Switches { get; }
    public List<PlacedItem> PlacedItems { get; } = [];
    public IReadOnlyList<string> Notifications => _notifications;
    public double PreviewX => _previewX;
    public double PreviewY => _previewY;

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

    public TrackPosition PositionAt(double distance)
    {
        distance = NormalizeDistance(distance, TrackLength);
        for (var index = 0; index < _segmentLengths.Length; index++)
        {
            var end = _segmentStartDistances[index] + _segmentLengths[index];
            if (distance <= end || index == _segmentLengths.Length - 1)
            {
                var start = MainLoop[index];
                var next = MainLoop[(index + 1) % MainLoop.Length];
                var angle = Math.Atan2(next.Y - start.Y, next.X - start.X);
                var progress = (distance - _segmentStartDistances[index]) / _segmentLengths[index];
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
        var position = PositionAt(signal.Distance);
        return OffsetFromTrack(position, signal.Side * TrackOffset);
    }

    public TrackPosition SwitchPosition(Points points)
    {
        return PositionAt(points.Distance);
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

    public bool ToggleSwitch(string id)
    {
        var points = Switches.FirstOrDefault(points => points.Id == id);
        if (points is null) return false;

        points.Direction = points.Direction == PointDirection.Straight
            ? PointDirection.Diverging
            : PointDirection.Straight;
        _notifications.Add($"{points.Id} set {points.Direction.ToString().ToUpperInvariant()}.");
        return true;
    }

    public void SelectBuildItem(BuildItemKind item)
    {
        if (!BuildItems.Any(definition => definition.Kind == item))
            throw new ArgumentOutOfRangeException(nameof(item));

        SelectedBuildItem = item;
        DemolishMode = false;
    }

    public void ToggleDemolishMode()
    {
        DemolishMode = !DemolishMode;
    }

    public void SetPreview(double x, double y)
    {
        _previewX = SnapToGrid(x);
        _previewY = SnapToGrid(y);
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

        var snappedX = SnapToGrid(x);
        var snappedY = SnapToGrid(y);
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
        PlacedItems.Add(new(SelectedBuildItem, snappedX, snappedY, definition.Cost));
        _notifications.Add($"{definition.Name} placed for ${definition.Cost:N0}.");
        return true;
    }

    public bool DemolishNearest(double x, double y)
    {
        if (Mode != GameMode.Editor || !DemolishMode) return false;

        var snappedX = SnapToGrid(x);
        var snappedY = SnapToGrid(y);
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
        return new(
            SaveVersion,
            Mode,
            Balance,
            GameSeconds,
            RealSeconds,
            _recentEarnings.Sum(earning => earning.Amount),
            PlacedItems.ToArray(),
            Signals.Select(signal => new SignalSaveData(signal.Id, signal.Aspect)).ToArray(),
            Switches.Select(points => new PointsSaveData(points.Id, points.Direction)).ToArray(),
            new(TrainDistance, TrainStopped, TrainStatus));
    }

    public IReadOnlyList<string> DrainNotifications()
    {
        var notifications = _notifications.ToArray();
        _notifications.Clear();
        return notifications;
    }

    private void AdvanceStep(double deltaSeconds)
    {
        RealSeconds += deltaSeconds;
        GameSeconds += deltaSeconds;

        var obstacle = FindNextObstacle();
        var caution = NextSignalAspect() == SignalAspect.Yellow ? 0.55 : 1;
        var advance = TrainSpeed * caution * deltaSeconds;
        TrainStopped = false;

        if (obstacle is not null && obstacle.Value.Distance <= SafetyDistance + advance)
        {
            var distanceToStop = Math.Max(0, obstacle.Value.Distance - SafetyDistance);
            TrainDistance = NormalizeDistance(TrainDistance + distanceToStop, TrackLength);
            TrainStopped = true;
            TrainStatus = obstacle.Value.Type == ObstacleType.Signal
                ? $"Held at signal {obstacle.Value.Id}"
                : $"Points {obstacle.Value.Id} misaligned";
        }
        else
        {
            var previousDistance = TrainDistance;
            TrainDistance = NormalizeDistance(TrainDistance + advance, TrackLength);
            if (TrainStopped) TrainStatus = "In service";

            var sectionPayment = 0d;
            foreach (var signal in Signals)
            {
                if (signal.Aspect != SignalAspect.Red
                    && CrossedMarker(previousDistance, signal.Distance, advance, TrackLength))
                {
                    sectionPayment += SectionPayment;
                    _notifications.Add($"Safe passage at {signal.Id} — ${SectionPayment:N0} section payment.");
                }
            }

            if (CrossedMarker(previousDistance, StationDistance, advance, TrackLength))
            {
                sectionPayment += DestinationPayment;
                _notifications.Add($"Train arrived at Greyfield Station — ${DestinationPayment:N0} destination bonus.");
            }

            if (sectionPayment > 0)
            {
                Balance = (int)Math.Min(int.MaxValue, (long)Balance + (int)sectionPayment);
                _recentEarnings.Enqueue((RealSeconds, sectionPayment));
            }
        }

        TrainStatus = TrainStopped ? TrainStatus : "In service";
        while (_recentEarnings.TryPeek(out var earnings) && earnings.Time < RealSeconds - 60)
            _recentEarnings.Dequeue();
    }

    private void Restore(GameSaveData save)
    {
        if (save.Version != SaveVersion)
            throw new InvalidDataException($"Unsupported save version {save.Version}.");
        if (save.Train is null || save.Signals is null || save.Points is null || save.PlacedItems is null
            || string.IsNullOrWhiteSpace(save.Train.Status)
            || save.Balance < 0 || !double.IsFinite(save.GameSeconds) || save.GameSeconds < 0
            || !double.IsFinite(save.RealSeconds) || save.RealSeconds < 0
            || save.EarnedInWindow < 0 || !double.IsFinite(save.EarnedInWindow)
            || !double.IsFinite(save.Train.Distance))
            throw new InvalidDataException("The saved game contains invalid values.");

        Balance = save.Balance;
        if (!Enum.IsDefined(save.Mode))
            throw new InvalidDataException("The saved game contains an invalid game mode.");
        Mode = save.Mode;
        GameSeconds = save.GameSeconds;
        RealSeconds = save.RealSeconds;
        TrainDistance = NormalizeDistance(save.Train.Distance, TrackLength);
        TrainStopped = save.Train.Stopped;
        TrainStatus = save.Train.Status;
        foreach (var signalState in save.Signals)
        {
            if (signalState is null)
                throw new InvalidDataException("The saved game contains an invalid signal.");
            var signal = Signals.FirstOrDefault(item => item.Id == signalState.Id);
            if (signal is null || !Enum.IsDefined(signalState.Aspect))
                throw new InvalidDataException("The saved game contains an invalid signal.");
            signal.Aspect = signalState.Aspect;
        }

        foreach (var switchState in save.Points)
        {
            if (switchState is null)
                throw new InvalidDataException("The saved game contains invalid points.");
            var points = Switches.FirstOrDefault(item => item.Id == switchState.Id);
            if (points is null || !Enum.IsDefined(switchState.Direction))
                throw new InvalidDataException("The saved game contains invalid points.");
            points.Direction = switchState.Direction;
        }

        if (save.PlacedItems.Count > 10_000)
            throw new InvalidDataException("The saved game contains too many trackside items.");
        foreach (var item in save.PlacedItems)
        {
            if (item is null)
                throw new InvalidDataException("The saved game contains an invalid trackside item.");
            var definition = BuildItems
                .Where(buildItem => buildItem.Kind == item.Kind)
                .Select(buildItem => (BuildItemDefinition?)buildItem)
                .FirstOrDefault();
            if (definition is null || definition.Value.Cost != item.Cost
                || !double.IsFinite(item.X) || !double.IsFinite(item.Y)
                || item.X < 0 || item.X > WorldWidth || item.Y < 0 || item.Y > WorldHeight)
                throw new InvalidDataException("The saved game contains an invalid trackside item.");
            if (IsWithinTrackClearance(item.X, item.Y)
                || PlacedItems.Any(existing => Distance(existing.X - item.X, existing.Y - item.Y) < GridSize * 0.75))
                throw new InvalidDataException("The saved game contains overlapping trackside items.");
            PlacedItems.Add(item);
        }

        if (!double.IsFinite(save.EarnedInWindow)) return;
        if (save.EarnedInWindow > 0) _recentEarnings.Enqueue((RealSeconds, save.EarnedInWindow));
    }

    private void RecalculateIncome()
    {
        IncomePerMinute = _recentEarnings.Sum(earnings => earnings.Amount);
    }

    private SignalAspect NextSignalAspect()
    {
        return Signals
            .Select(signal => new
            {
                signal.Aspect,
                Distance = DistanceToNext(TrainDistance, signal.Distance, TrackLength)
            })
            .Where(item => item.Distance > 0)
            .OrderBy(item => item.Distance)
            .Select(item => item.Aspect)
            .FirstOrDefault(SignalAspect.Green);
    }

    private Obstacle? FindNextObstacle()
    {
        return Signals
            .Where(signal => signal.Aspect == SignalAspect.Red)
            .Select(signal => new Obstacle(
                DistanceToNext(TrainDistance, signal.Distance, TrackLength),
                ObstacleType.Signal,
                signal.Id))
            .Concat(Switches
                .Where(points => points.Direction != points.AssignedDirection)
                .Select(points => new Obstacle(
                    DistanceToNext(TrainDistance, points.Distance, TrackLength),
                    ObstacleType.Switch,
                    points.Id)))
            .Where(obstacle => obstacle.Distance > 0)
            .OrderBy(obstacle => obstacle.Distance)
            .Select(obstacle => (Obstacle?)obstacle)
            .FirstOrDefault();
    }

    private double SnapToGrid(double coordinate)
    {
        return Math.Clamp(Math.Round(coordinate / GridSize, MidpointRounding.AwayFromZero) * GridSize, 20, 980);
    }

    private bool IsWithinTrackClearance(double x, double y)
    {
        for (var index = 0; index < MainLoop.Length; index++)
        {
            var start = MainLoop[index];
            var end = MainLoop[(index + 1) % MainLoop.Length];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = dx * dx + dy * dy;
            var progress = Math.Clamp(((x - start.X) * dx + (y - start.Y) * dy) / lengthSquared, 0, 1);
            var nearestX = start.X + progress * dx;
            var nearestY = start.Y + progress * dy;
            if (Distance(x - nearestX, y - nearestY) < 30) return true;
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

    private static double Distance(double x, double y)
    {
        return Math.Sqrt(x * x + y * y);
    }

    private static (double[] Lengths, double[] Starts, double Total) MeasureLoop()
    {
        var lengths = new double[MainLoop.Length];
        var starts = new double[MainLoop.Length];
        var total = 0d;
        for (var index = 0; index < MainLoop.Length; index++)
        {
            starts[index] = total;
            var start = MainLoop[index];
            var end = MainLoop[(index + 1) % MainLoop.Length];
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            lengths[index] = Math.Sqrt(dx * dx + dy * dy);
            total += lengths[index];
        }

        return (lengths, starts, total);
    }

    private enum ObstacleType
    {
        Signal,
        Switch
    }

    private readonly record struct Obstacle(double Distance, ObstacleType Type, string Id);
}
