using Signaller.GameLogic;

var tests = new (string Name, Action Run)[]
{
    ("signal aspects cycle in the expected order", SignalAspectsCycle),
    ("train stops safely at a red signal and resumes on green", TrainStopsAtSignalAndResumes),
    ("train remains stopped at a red signal until it is cleared", RedSignalKeepsTrainStopped),
    ("automatic routing serves all three terminal branches", AutomaticRoutingServesThreeTerminals),
    ("regular mode has junctions, central platforms, side stations, and station signals", RegularModeBuildsExpandedNetwork),
    ("regular trains stop, open doors, wait safely, and resume at stations", RegularTrainsServeStations),
    ("trains enter the zone and trigger an arrival notification", TrainEntryIsReported),
    ("only safe signal and destination crossings pay", SafePassagesEarnMoney),
    ("loop distance and wrapped crossings are correct", TrackDistancesWrapCorrectly),
    ("editor charges builds and refunds half on demolition", EditorBuildsAndRefunds),
    ("editor refuses occupied and track-overlapping placements", EditorRejectsOccupiedPlacements),
    ("connected depot-to-station track activates an operating route", EditorTracksBecomeOperational),
    ("game save restores operation and editor state", GameSaveRestoresState),
    ("original and regular saves preserve their selected game style", GameStylesPersistIndependently),
    ("version one saves migrate without manual switch controls", LegacySavesMigrate),
    ("invalid saves are rejected and conflicting saved items are recovered", InvalidSavesAreRecovered),
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
    Check(engine.SetSignalAspect("S01", SignalAspect.Red), "S01 should exist.");
    Equal(SignalAspect.Red, engine.Signals[0].Aspect);
    engine.ToggleSignal("S01");
    Equal(SignalAspect.Yellow, engine.Signals[0].Aspect);
    engine.ToggleSignal("S01");
    Check(!engine.SetSignalAspect("missing", SignalAspect.Green), "An unknown signal should not be changed.");
    engine.SetSignalAspect("S01", SignalAspect.Green);
    Equal(SignalAspect.Green, engine.Signals[0].Aspect);
    Check(!engine.ToggleSignal("missing"), "An unknown signal should not be changed.");
}

static void TrainStopsAtSignalAndResumes()
{
    var engine = CreateSingleTrainEngine();
    engine.SetSignalAspect("S01", SignalAspect.Red);
    RunUntilStopped(engine, 20);
    Check(engine.TrainStopped, "A red signal must stop the train.");
    Check(engine.TrainDistance < engine.Signals[0].Distance,
        "The train must not pass a red signal.");
    Equal("Held at signal S01", engine.TrainStatus);

    engine.SetSignalAspect("S01", SignalAspect.Green);
    engine.Advance(0.1);
    Check(!engine.TrainStopped, "A green signal should release the train.");
}

static void RedSignalKeepsTrainStopped()
{
    var engine = CreateSingleTrainEngine();
    engine.SetSignalAspect("S01", SignalAspect.Red);
    RunUntilStopped(engine, 20);
    var stoppedAt = engine.TrainDistance;

    engine.Advance(2);
    Check(engine.TrainStopped, "A red signal must continue holding the train.");
    Near(stoppedAt, engine.TrainDistance);
    Equal(GameEngine.StartingBalance, engine.Balance);

    engine.SetSignalAspect("S01", SignalAspect.Green);
    engine.Advance(0.25);
    Check(!engine.TrainStopped, "A green signal should release the train.");
}

static void AutomaticRoutingServesThreeTerminals()
{
    var engine = new GameEngine();
    Equal(3, engine.Routes.Count);
    Equal(10, engine.Signals.Count);
    Equal(GameEngine.MaximumTrains, engine.TrainCount);
    Check(!GameEngine.BuildItems.Any(item => item.Kind == BuildItemKind.Switch),
        "Manual switch controls must not be offered.");
    Check(!engine.Trains.Any(train => train.StoppedAtStationDistance is not null),
        "Original mode should retain its existing through-running station behavior.");
    Check(engine.Trains.Select(train => train.RouteId).Distinct().Count() > 1,
        "The opening timetable should use automatic routes.");
    Check(engine.Routes.All(route => route.Points.Count >= 3 && route.ExitName.Contains("EXIT")),
        "Each branch should have a defined terminal.");

    for (var index = 0; index < 60 * 25; index++)
        engine.Advance(1d / 60);
    for (var first = 0; first < engine.Trains.Count; first++)
    for (var second = first + 1; second < engine.Trains.Count; second++)
    {
        var a = engine.Trains[first];
        var b = engine.Trains[second];
        if (a.RouteId != b.RouteId && (a.Distance >= 950 || b.Distance >= 950)) continue;
        var gap = Math.Abs(a.Distance - b.Distance);
        Check(gap >= GameEngine.SafetyDistance - 0.1,
            "The automatic interlocking must preserve safe train separation.");
    }
}

static void RegularModeBuildsExpandedNetwork()
{
    var engine = new GameEngine(style: GameStyle.Regular);
    Equal(5, engine.RouteCount);
    Equal(3, new GameEngine(style: GameStyle.Original).RouteCount);
    Equal(10, new GameEngine(style: GameStyle.Original).Signals.Count);
    Check(engine.Trains.All(train =>
            train.Distance < engine.StationsForRoute(train.RouteId).First(station => station.GroupId == "central").Distance),
        "Every opening service should approach and call at Greyfield Central.");
    Check(engine.Signals.Count >= 28, "Every regular-mode station should have approach and departure signals.");
    Equal(5, engine.AllStations.Count(item => item.Station.GroupId == "central"));
    Check(engine.AllStations.Select(item => item.Station.Name).Distinct().Count() >= 9,
        "The regular timetable should serve the central station and multiple side stations.");
    foreach (var (routeId, station) in engine.AllStations)
    {
        var stationSignals = engine.Signals
            .Where(signal => signal.RouteId == routeId || (station.GroupId == "central" && signal.RouteId == "common"))
            .ToArray();
        Check(stationSignals.Any(signal => Math.Abs(signal.Distance - station.Distance + 55) < 0.001),
            $"{station.Name} should have an approach signal.");
        Check(stationSignals.Any(signal => Math.Abs(signal.Distance - station.Distance - 55) < 0.001),
            $"{station.Name} should have a departure signal.");
    }
}

static void RegularTrainsServeStations()
{
    var engine = CreateSingleTrainEngine(GameStyle.Regular);
    var train = engine.Trains.Single();
    var central = engine.StationsForRoute(train.RouteId).First(station => station.GroupId == "central");
    var arrivals = 0;
    var departures = 0;
    engine.TrainStationChanged += (_, station, doorsOpening) =>
    {
        Equal(central.Name, station.Name);
        if (doorsOpening) arrivals++;
        else departures++;
    };

    RunUntilAtStation(engine, train, central, 30);
    Equal(1, arrivals);
    Equal(0, departures);
    Equal(PlatformIndicator.DoorsOpen, engine.PlatformIndicatorFor(train.RouteId, central));

    var restored = new GameEngine(engine.Save(), GameStyle.Regular);
    var restoredTrain = restored.Trains.Single(candidate => candidate.Id == train.Id);
    var restoredCentral = restored.StationsForRoute(restoredTrain.RouteId)
        .First(station => station.GroupId == "central");
    Equal(PlatformIndicator.DoorsOpen, restored.PlatformIndicatorFor(restoredTrain.RouteId, restoredCentral));

    AdvanceFor(engine, GameEngine.StationDoorOpenSeconds + 0.2);
    Equal(PlatformIndicator.ReadyToDepart, engine.PlatformIndicatorFor(train.RouteId, central));
    AdvanceUntil(engine, () => engine.StoppedAtStation(train) is null, 6);
    Equal(1, departures);
    Equal(PlatformIndicator.Clear, engine.PlatformIndicatorFor(train.RouteId, central));

    var heldEngine = CreateSingleTrainEngine(GameStyle.Regular);
    var heldTrain = heldEngine.Trains.Single();
    var heldStation = heldEngine.StationsForRoute(heldTrain.RouteId)
        .First(station => station.GroupId == "central");
    var departureSignal = heldEngine.Signals.Single(signal =>
        signal.Id == "ST01D" && signal.RouteId == "common");
    Check(heldEngine.SetSignalAspect(departureSignal.Id, SignalAspect.Red),
        "The station departure signal should be controllable.");
    RunUntilAtStation(heldEngine, heldTrain, heldStation, 30);
    AdvanceFor(heldEngine, GameEngine.StationDelayedSeconds + 1);
    Equal(PlatformIndicator.Delayed, heldEngine.PlatformIndicatorFor(heldTrain.RouteId, heldStation));
    Check(heldEngine.StoppedAtStation(heldTrain) is not null,
        "A red departure signal must hold the train at the platform.");
    heldEngine.SetSignalAspect(departureSignal.Id, SignalAspect.Green);
    AdvanceUntil(heldEngine, () => heldEngine.StoppedAtStation(heldTrain) is null, 2);
    Equal(PlatformIndicator.Clear, heldEngine.PlatformIndicatorFor(heldTrain.RouteId, heldStation));
}

static void TrainEntryIsReported()
{
    var engine = new GameEngine();
    var entered = 0;
    engine.TrainEnteredZone += _ => entered++;

    for (var index = 0; index < 60 * 60 * 60 && entered == 0; index++)
        engine.Advance(1d / 60);

    Equal(1, entered);
}

static void SafePassagesEarnMoney()
{
    var redSignal = new GameEngine();
    var isolatedTrain = redSignal.Save().Train with
    {
        Distance = 0,
        RouteId = "main",
        Id = "TR99",
        Stopped = false,
        Status = "In service"
    };
    var isolatedSave = redSignal.Save() with
    {
        Train = isolatedTrain,
        Trains = [isolatedTrain]
    };
    redSignal = new GameEngine(isolatedSave);
    redSignal.SetSignalAspect("S01", SignalAspect.Red);
    RunUntilStopped(redSignal, 20);
    Equal(GameEngine.StartingBalance, redSignal.Balance);
    redSignal.SetSignalAspect("S01", SignalAspect.Green);
    RunUntilBalance(redSignal, GameEngine.StartingBalance + 25, 5);
    Equal(GameEngine.StartingBalance + 25, redSignal.Balance);

    var running = new GameEngine();
    running.Advance(0.1);
    Check(running.Balance >= GameEngine.StartingBalance,
        "The opening timetable should begin earning safely.");
    RunUntilBalance(running, GameEngine.StartingBalance + 25, 20);
    Check(running.Balance >= GameEngine.StartingBalance + 25,
        "Safe signal crossings should generate income.");
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
    Check(!engine.PlaceSelectedItem(200, 550),
        "An editor piece must not overlap the existing railway.");
    Check(engine.PlaceSelectedItem(40, 40), "A clear tile should accept the first build.");
    Check(!engine.PlaceSelectedItem(40, 40),
        "A second piece must not overwrite the existing piece.");
    Equal(GameEngine.StartingBalance - 100, engine.Balance);
    engine.SetMode(GameMode.Signaller);
    Check(!engine.PlaceSelectedItem(80, 80), "Buildings must not be placeable in signalling mode.");

    engine.SetMode(GameMode.Editor);
    engine.SelectBuildItem(BuildItemKind.Depot);
    Check(engine.PlaceSelectedItem(1740, 1020), "Editor placement should work in the expanded map's lower-right area.");
    Equal(1760d, engine.PlacedItems[^1].X);
    Equal(1040d, engine.PlacedItems[^1].Y);
}

static void EditorTracksBecomeOperational()
{
    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    engine.SelectBuildItem(BuildItemKind.Depot);
    Check(engine.PlaceSelectedItem(40, 40), "The custom route should start at its depot.");
    engine.SelectBuildItem(BuildItemKind.Station);
    Check(engine.PlaceSelectedItem(400, 40), "The custom route should have a terminal station.");
    engine.SelectBuildItem(BuildItemKind.StraightTrack);
    foreach (var x in Enumerable.Range(2, 8).Select(index => index * 40d))
        Check(engine.PlaceSelectedItem(x, 40), $"Track at {x} should connect to its neighbor.");
    engine.SelectBuildItem(BuildItemKind.Signal);
    Check(engine.PlaceSelectedItem(240, 80), "A signal beside the track should be built.");

    var customRoute = engine.Routes.Single(route => route.Id.StartsWith("editor-", StringComparison.Ordinal));
    Equal(4, engine.RouteCount);
    Equal("Depot 40,40", customRoute.EntranceName);
    var editorSignal = engine.Signals.Single(signal => signal.Id == "E0240-0080");
    Equal(customRoute.Id, editorSignal.RouteId);
    var savedTrain = engine.Save().Train with
    {
        Id = "TR99",
        RouteId = customRoute.Id,
        Distance = 0,
        Status = "In service"
    };
    var restored = new GameEngine(engine.Save() with { Train = savedTrain, Trains = [savedTrain] });
    Check(restored.Trains[0].RouteId == customRoute.Id,
        $"The custom train route should survive save restoration (saved {customRoute.Id}, restored {restored.Trains[0].RouteId}).");
    Equal("User-built station", restored.NextStation(restored.Trains[0])!.Name);
    var restoredSignal = restored.Signals.Single(signal => signal.Id == "E0240-0080");
    Check(restored.SetSignalAspect(restoredSignal.Id, SignalAspect.Red),
        "A built signal should be controllable.");
    restored.SetMode(GameMode.Signaller);
    for (var index = 0; index < 60 * 15; index++)
        restored.Advance(1d / 60);
    var stoppedTrain = restored.Trains.Single(train => train.Id == savedTrain.Id);
    Check(stoppedTrain.Stopped,
        $"The train must obey an editor-built red signal (distance {stoppedTrain.Distance:0.0}, signal {restoredSignal.Distance:0.0}, status {stoppedTrain.Status}).");

    for (var index = 0; index < 60 * 15; index++)
        restored.Advance(1d / 60);
    Check(restored.Trains.Any(train => train.RouteId == customRoute.Id),
        "The timetable should automatically dispatch trains over the constructed route.");

    restored.SetMode(GameMode.Editor);
    restored.ToggleDemolishMode();
    Check(restored.DemolishNearest(80, 40), "A route tile should be removable in demolish mode.");
    Equal(3, restored.RouteCount);
    Check(!restored.Trains.Any(train => train.RouteId == customRoute.Id),
        "Trains should be withdrawn safely if their constructed route is disconnected.");
}

static void GameSaveRestoresState()
{
    var engine = new GameEngine();
    engine.SetMode(GameMode.Editor);
    engine.PlaceSelectedItem(40, 40);
    engine.RotateSelectedItem();
    engine.SelectBuildItem(BuildItemKind.StraightTrack);
    engine.PlaceSelectedItem(80, 40);
    engine.ToggleDemolishMode();
    var saved = engine.Save();
    var restored = new GameEngine(saved);

    Equal(GameEngine.StartingBalance - 200, restored.Balance);
    Equal(GameEngine.MaximumTrains, restored.TrainCount);
    Equal(2, restored.PlacedItems.Count);
    Equal(40d, restored.PlacedItems[0].X);
    Equal(90, restored.PlacedItems[1].Rotation);
    Equal(GameMode.Editor, restored.Mode);
}

static void GameStylesPersistIndependently()
{
    var original = new GameEngine(style: GameStyle.Original);
    var regular = new GameEngine(style: GameStyle.Regular);
    var originalSave = original.Save();
    var regularSave = regular.Save();

    Equal(GameStyle.Original, originalSave.Style);
    Equal(GameStyle.Regular, regularSave.Style);
    Equal(GameStyle.Original, new GameEngine(originalSave).Style);
    Equal(GameStyle.Regular, new GameEngine(regularSave, GameStyle.Regular).Style);

    var previousRegularTrains = regularSave.Trains!.Take(3).ToArray();
    var previousRegularSave = regularSave with
    {
        Version = 3,
        Train = previousRegularTrains[0],
        Trains = previousRegularTrains,
        Signals = regularSave.Signals.Take(10).ToArray()
    };
    var migratedRegular = new GameEngine(previousRegularSave, GameStyle.Regular);
    Equal(GameStyle.Regular, migratedRegular.Style);
    Equal(5, migratedRegular.RouteCount);
}

static void LegacySavesMigrate()
{
    var engine = new GameEngine();
    var legacyTrain = new TrainSaveData(300, false, "In service");
    var legacySave = engine.Save() with
    {
        Version = 1,
        Signals = [new("S1", SignalAspect.Red), new("S2", SignalAspect.Yellow)],
        Points = [new("P1", PointDirection.Diverging)],
        Train = legacyTrain,
        Trains = null,
        PlacedItems = []
    };
    var restored = new GameEngine(legacySave);

    Equal(1, restored.TrainCount);
    Equal("2K14", restored.Trains[0].Headcode);
    Equal(SignalAspect.Red, restored.Signals.Single(signal => signal.Id == "S02").Aspect);
    Equal(SignalAspect.Yellow, restored.Signals.Single(signal => signal.Id == "S05").Aspect);
}

static void InvalidSavesAreRecovered()
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
    var restored = new GameEngine(overlappingSave);
    Equal(1, restored.PlacedItems.Count);
    Equal(validSave.Balance, restored.Balance);
    Check(restored.Notifications.Any(notification => notification.Contains("other built pieces", StringComparison.Ordinal)),
        "The player should be told when a duplicate saved piece is omitted.");

    var trackOverlappingItem = new PlacedItem(BuildItemKind.StraightTrack, 200, 550, 100, 0);
    var currentSaveWithTrackOverlap = validSave with { PlacedItems = [trackOverlappingItem] };
    var restoredCurrentSave = new GameEngine(currentSaveWithTrackOverlap);
    Equal(0, restoredCurrentSave.PlacedItems.Count);
    Check(restoredCurrentSave.Notifications.Any(notification => notification.Contains("the railway", StringComparison.Ordinal)),
        "The player should be told when a saved piece overlaps the built-in railway.");

    var legacySaveWithTrackOverlap = currentSaveWithTrackOverlap with { Version = GameEngine.SaveVersion - 1 };
    var restoredLegacySave = new GameEngine(legacySaveWithTrackOverlap);
    Equal(1, restoredLegacySave.PlacedItems.Count);
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

static GameEngine CreateSingleTrainEngine(GameStyle style = GameStyle.Original)
{
    var source = new GameEngine(style: style);
    var train = source.Save().Trains!.First(train => train.RouteId == "main") with
    {
        Distance = 0,
        Id = "TR99",
        RouteId = "main",
        Stopped = false,
        Status = "In service"
    };
    return new GameEngine(source.Save() with { Train = train, Trains = [train] }, style);
}

static void RunUntilAtStation(GameEngine engine, TrainService train, StationInfo station, double seconds)
{
    AdvanceUntil(engine, () => engine.StoppedAtStation(train)?.Name == station.Name, seconds);
}

static void AdvanceUntil(GameEngine engine, Func<bool> condition, double seconds)
{
    for (var index = 0; index < seconds * 60 && !condition(); index++)
        engine.Advance(1d / 60);
    Check(condition(), $"The expected train state was not reached within {seconds:0} seconds.");
}

static void AdvanceFor(GameEngine engine, double seconds)
{
    for (var index = 0; index < seconds * 60; index++)
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
