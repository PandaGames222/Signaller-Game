using Signaller.GameLogic;

var tests = new (string Name, Action Run)[]
{
    ("signal aspects cycle in the expected order", SignalAspectsCycle),
    ("train stops safely at a red signal and resumes on green", TrainStopsAtSignalAndResumes),
    ("train remains stopped at a red signal until it is cleared", RedSignalKeepsTrainStopped),
    ("misaligned points stop the assigned route", MisalignedPointsStopTrain),
    ("only safe signal and destination crossings pay", SafePassagesEarnMoney),
    ("loop distance and wrapped crossings are correct", TrackDistancesWrapCorrectly),
    ("editor charges builds and refunds half on demolition", EditorBuildsAndRefunds),
    ("editor refuses occupied and track-overlapping placements", EditorRejectsOccupiedPlacements),
    ("game save restores operation and editor state", GameSaveRestoresState),
    ("invalid and overlapping saved items are rejected", InvalidSavesAreRejected),
    ("large balances cannot overflow during train payments", LargeBalancesDoNotOverflow),
    ("update requests validate the release, checksum, and install path", UpdateRequestsAreValidated)
};

var failures = new List<string>();
foreach (var (name, run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{name}: {exception.Message}");
        Console.WriteLine($"FAIL {name}: {exception.Message}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} of {tests.Length} game-engine tests failed.");
    return 1;
}

Console.WriteLine($"All {tests.Length} game-engine tests passed.");
return 0;

static void SignalAspectsCycle()
{
    var engine = new GameEngine();
    Check(engine.ToggleSignal("S1"), "S1 should exist.");
    Equal(SignalAspect.Red, engine.Signals[0].Aspect);
    engine.ToggleSignal("S1");
    Equal(SignalAspect.Yellow, engine.Signals[0].Aspect);
    engine.ToggleSignal("S1");
    Equal(SignalAspect.Green, engine.Signals[0].Aspect);
    Check(!engine.ToggleSignal("missing"), "An unknown signal should not be changed.");
}

static void TrainStopsAtSignalAndResumes()
{
    var engine = new GameEngine();
    engine.ToggleSignal("S1");
    RunUntilStopped(engine, 20);
    Check(engine.TrainStopped, "A red signal must stop the train.");
    Check(engine.TrainDistance < engine.Signals[0].Distance,
        "The train must not pass a red signal.");
    Equal("Held at signal S1", engine.TrainStatus);
    Equal(GameEngine.StartingBalance, engine.Balance);

    engine.ToggleSignal("S1");
    engine.ToggleSignal("S1");
    engine.Advance(0.1);
    Check(!engine.TrainStopped, "A green signal should release the train.");
    engine.Advance(0.6);
    Equal(GameEngine.StartingBalance + (int)GameEngine.SectionPayment, engine.Balance);
}

static void RedSignalKeepsTrainStopped()
{
    var engine = new GameEngine();
    engine.ToggleSignal("S1");
    RunUntilStopped(engine, 20);
    var stoppedAt = engine.TrainDistance;

    engine.Advance(2);
    Check(engine.TrainStopped, "A red signal must continue holding the train.");
    Near(stoppedAt, engine.TrainDistance);
    Equal(GameEngine.StartingBalance, engine.Balance);

    engine.ToggleSignal("S1");
    engine.ToggleSignal("S1");
    engine.Advance(0.25);
    Check(!engine.TrainStopped, "A green signal should release the train.");
}

static void MisalignedPointsStopTrain()
{
    var engine = new GameEngine();
    engine.ToggleSwitch("P1");
    RunUntilStopped(engine, 20);
    Check(engine.TrainStopped, "Points set for a different route must stop the train.");
    Check(engine.TrainDistance < engine.Switches[0].Distance,
        "The train must stop before reaching the misaligned points.");

    engine.ToggleSwitch("P1");
    engine.Advance(0.1);
    Check(!engine.TrainStopped, "Restoring the assigned direction must resume the train.");
}

static void SafePassagesEarnMoney()
{
    var redSignal = new GameEngine();
    redSignal.ToggleSignal("S1");
    RunUntilStopped(redSignal, 20);
    Equal(GameEngine.StartingBalance, redSignal.Balance);

    var running = new GameEngine();
    running.Advance(0.1);
    Equal(GameEngine.StartingBalance, running.Balance);
    RunUntilBalance(running, GameEngine.StartingBalance + 25, 20);
    Equal(GameEngine.StartingBalance + 25, running.Balance);
    RunUntilBalance(running, GameEngine.StartingBalance + 150, 25);
    Equal(GameEngine.StartingBalance + 150, running.Balance);
    Check(running.IncomePerMinute > 0, "Recent earnings should appear in the income HUD.");
}

static void TrackDistancesWrapCorrectly()
{
    var engine = new GameEngine();
    Near(0, GameEngine.NormalizeDistance(engine.TrackLength, engine.TrackLength));
    Near(engine.TrackLength - 5, GameEngine.NormalizeDistance(-5, engine.TrackLength));
    Near(10, GameEngine.DistanceToNext(engine.TrackLength - 10, 0, engine.TrackLength));
    Check(GameEngine.CrossedMarker(engine.TrackLength - 10, 5, 20, engine.TrackLength),
        "A marker after a loop should count as crossed.");
    Check(!GameEngine.CrossedMarker(10, 15, 0, engine.TrackLength),
        "A marker cannot be crossed without movement.");
}

static void EditorBuildsAndRefunds()
{
    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    engine.SelectBuildItem(BuildItemKind.StraightTrack);
    Check(engine.PlaceSelectedItem(40, 40), "A clear tile should accept a track.");
    Equal(GameEngine.StartingBalance - 100, engine.Balance);
    engine.ToggleDemolishMode();
    Check(engine.DemolishMode, "Toggling demolish mode should succeed.");
    Check(engine.DemolishNearest(40, 40), "The placed item should be demolishable.");
    Equal(GameEngine.StartingBalance - 50, engine.Balance);

    engine.SelectBuildItem(BuildItemKind.Station);
    Check(engine.PlaceSelectedItem(80, 80), "An affordable station should be built.");
    Equal(GameEngine.StartingBalance - 2550, engine.Balance);

    var lowBalance = new GameEngine();
    lowBalance.SetMode(GameMode.Editor);
    lowBalance.SelectBuildItem(BuildItemKind.Station);
    Check(lowBalance.PlaceSelectedItem(40, 40), "The first station should be affordable.");
    Check(lowBalance.PlaceSelectedItem(80, 40), "A second station should use the remaining balance.");
    Check(!lowBalance.PlaceSelectedItem(120, 40), "An unaffordable station must not be built.");
    Equal(0, lowBalance.Balance);
}

static void EditorRejectsOccupiedPlacements()
{
    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    Check(!engine.PlaceSelectedItem(300, 120),
        "An editor piece must not overlap the existing loop.");
    Check(engine.PlaceSelectedItem(40, 40), "A clear tile should accept the first build.");
    Check(!engine.PlaceSelectedItem(40, 40),
        "A second piece must not overwrite the existing piece.");
    Equal(GameEngine.StartingBalance - 100, engine.Balance);
    engine.SetMode(GameMode.Signaller);
    Check(!engine.PlaceSelectedItem(80, 80), "Buildings must not be placeable in signalling mode.");
}

static void GameSaveRestoresState()
{
    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    engine.PlaceSelectedItem(40, 40);
    engine.ToggleDemolishMode();
    var saved = engine.Save();
    var restored = new GameEngine(saved);

    Equal(GameEngine.StartingBalance - 100, restored.Balance);
    Equal(1, restored.PlacedItems.Count);
    Equal(40d, restored.PlacedItems[0].X);
    Equal(GameMode.Editor, restored.Mode);
}

static void InvalidSavesAreRejected()
{
    var invalid = new GameSaveData(
        Version: 99,
        Mode: GameMode.Signaller,
        Balance: 5000,
        GameSeconds: 0,
        RealSeconds: 0,
        EarnedInWindow: 0,
        PlacedItems: [],
        Signals: [],
        Points: [],
        Train: new(80, false, "In service"));

    Throws<InvalidDataException>(() => new GameEngine(invalid));

    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    Check(engine.PlaceSelectedItem(40, 40), "A valid save item should be placeable.");
    var validSave = engine.Save();
    var overlappingSave = validSave with
    {
        PlacedItems = [.. validSave.PlacedItems, validSave.PlacedItems[0]]
    };
    Throws<InvalidDataException>(() => new GameEngine(overlappingSave));
}

static void LargeBalancesDoNotOverflow()
{
    var engine = new GameEngine();
    var save = engine.Save() with { Balance = int.MaxValue };
    var restored = new GameEngine(save);

    for (var index = 0; index < 20 * 60; index++)
        restored.Advance(1d / 60);
    Equal(int.MaxValue, restored.Balance);
}

static void UpdateRequestsAreValidated()
{
    var valid = new[]
    {
        "--apply-update",
        "1.2.0",
        "https://github.com/PandaGames222/Signaller-Game/releases/download/v1.2.0/Signaller-Setup-1.2.0.exe",
        new string('A', 64),
        "42",
        @"C:\Games\Signaller.exe"
    };
    Check(AppUpdateRequest.TryParse(valid, out var request), "A valid update request should be accepted.");
    Equal(new Version(1, 2, 0), request!.Version);
    Equal(@"C:\Games\Signaller.exe", request.GameExecutablePath);
    Equal(valid.Length, request.ToArguments().Length);

    foreach (var invalid in new[]
    {
        valid.Select((value, index) => index == 2
            ? value.Replace("github.com", "example.com", StringComparison.Ordinal)
            : value).ToArray(),
        valid.Select((value, index) => index == 2
            ? value.Replace("1.2.0", "1.3.0", StringComparison.Ordinal)
            : value).ToArray(),
        valid.Select((value, index) => index == 3 ? "not-a-checksum" : value).ToArray(),
        valid.Select((value, index) => index == 5 ? @"C:\Games\other.exe" : value).ToArray(),
        valid.Take(5).ToArray()
    })
        Check(!AppUpdateRequest.TryParse(invalid, out _),
            "Invalid update URLs, checksums, target paths, or arguments must be rejected.");
}

static void RunUntilStopped(GameEngine engine, double seconds)
{
    for (var i = 0; i < seconds * 60 && !engine.TrainStopped; i++)
        engine.Advance(1d / 60);
}

static void RunUntilBalance(GameEngine engine, int expected, double seconds)
{
    for (var i = 0; i < seconds * 60 && engine.Balance < expected; i++)
        engine.Advance(1d / 60);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Near(double expected, double actual, double epsilon = 0.001)
{
    if (Math.Abs(expected - actual) > epsilon)
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
