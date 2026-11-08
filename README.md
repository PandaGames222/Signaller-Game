# Signaller

Signaller is a standalone Windows railway-signalling game built with native WPF and .NET 8. It draws its track network directly with WPF; it does not use a browser, Electron, HTML, or a web server.

## Run from source

Install the .NET 8 SDK and, on Windows, run these commands from the project directory:

```powershell
dotnet run --project Signaller.csproj
dotnet run --project tests\Signaller.GameLogic.Tests.csproj
```

The WPF application targets Windows x64. Original and Regular mode saves are stored separately under `%LOCALAPPDATA%\Signaller`.

## Build a standalone installer

Publish a self-contained, single-file Windows build and package it with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
dotnet publish Signaller.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist\publish
ISCC.exe /DAppVersion=1.6.0 /DPublishDir="..\dist\publish" /DOutputDir="..\dist" installer\Signaller.iss
```

The installer is written to `dist\Signaller-Setup-1.6.0.exe`. It installs per-user without administrator privileges and uses a stable application ID; installing a newer version upgrades the existing installation and keeps the saved game.

## Automatic updates and GitHub releases

The installed app checks GitHub when it starts and every five minutes while running. When an update is available, choose **UPDATE** to open the in-game updater. It downloads and verifies the installer, closes the game, installs in the background, and reopens the updated game. Your saved game is kept.

If you are upgrading from the original 1.0 Electron app, run the latest native installer once; future native releases can be installed from the app's update button.

To publish a release:

1. Update the `<Version>` in `Signaller.csproj` and the installer version in the build command above, then commit the change.
2. Push a matching version tag, such as `v1.6.0`.

The [GitHub Actions workflow](./.github/workflows/release.yml) validates the tag, runs the game-logic regression tests, builds the self-contained Windows application, packages it, and attaches the installer to the GitHub release. Public release downloads let installed copies check and update without an embedded token.

## Play

- The start screen provides separate **Play** and **Settings** choices. Loading screens appear while the app or a game mode loads, and the simulation starts only after its map is displayed. Play opens the **Original** map-and-editor mode or the **Regular** live control desk; each mode has its own save. The Regular desk includes a live five-route network, a central station with five platforms, eight side stations, junctions, and station approach/departure signals.
- Settings can turn train-entry and station arrival/departure sounds on or off. Update checks run automatically at startup and periodically; the top-bar action is labeled **UPDATE** and changes to show the available version.
- Original mode retains the three-route Greyfield map with ten signals. Regular mode has five automatically routed branches, including the airport and harbor junctions.
- Five trains run on the timetable and stop at Regular-mode stations. Platforms show white while doors are open, slowly blinking yellow when ready to depart, and red when a train is held at the platform for 30 seconds or longer. Click a train to inspect its headcode, service, train type, delay, speed, next station/platform, destination, and status.
- Click a signal to open its control panel. Choose a lamp or press **1** for green, **2** for yellow, or **3** for red. Trains obey red signals and maintain safe spacing automatically; points are routed by the timetable.
- Switch to **Editor Mode** to build grid-based track. Straight and curved tiles connect through matching edges; connect a **Depot** to a **Station** to add an operating route that trains automatically use. Press **R** to rotate track pieces; demolishing a piece refunds 50% of its cost.
- Trains earn payments for safe signal passages and station calls. Use the pause button to stop the simulation.
- Save recovery keeps the rest of a game playable if saved editor pieces overlap each other or the built-in railway; a notification reports omitted pieces. Older saves are not rejected solely because the built-in railway clearance changed.
- Saves are written to a temporary file and flushed to disk before replacing the previous save, reducing the risk of losing progress if the app closes or the system shuts down during a save.
