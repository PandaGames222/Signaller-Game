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
ISCC.exe /DAppVersion=1.4.0 /DPublishDir="..\dist\publish" /DOutputDir="..\dist" installer\Signaller.iss
```

The installer is written to `dist\Signaller-Setup-1.4.0.exe`. It installs per-user without administrator privileges and uses a stable application ID; installing a newer version upgrades the existing installation and keeps the saved game.

## Automatic updates and GitHub releases

The installed app checks GitHub when it starts and every five minutes while running. When an update is available, choose **UPDATE** to open the in-game updater. It downloads and verifies the installer, closes the game, installs in the background, and reopens the updated game. Your saved game is kept.

If you are upgrading from the original 1.0 Electron app, run the latest native installer once; future native releases can be installed from the app's update button.

To publish a release:

1. Update the `<Version>` in `Signaller.csproj` and commit the change.
2. Push a matching version tag, such as `v1.4.0`.

The [GitHub Actions workflow](./.github/workflows/release.yml) validates the tag, runs the game-logic regression tests, builds the self-contained Windows application, packages it, and attaches the installer to the GitHub release. Public release downloads let installed copies check and update without an embedded token.

## Play

- The start screen provides separate **Play** and **Settings** choices. Play opens the **Original** map-and-editor mode or the **Regular** live control desk; saves are kept independently for each mode.
- Settings can turn the train-entry chime on or off. Update checks run automatically at startup and periodically; the top-bar action is labeled **UPDATE** and changes to show the available version.
- The single Greyfield Control Area map has a West Depot entrance, automatic junction routing, and east, north, and south exits. Ten signals protect the three routes.
- Five trains run on the timetable. Click a train to inspect its headcode, service, train type, delay, speed, next station/platform, destination, and status. An arrival chime plays when a train enters the control area.
- Click a signal to open its control panel. Choose a lamp or press **1** for green, **2** for yellow, or **3** for red. Trains obey red signals and maintain safe spacing automatically; points are routed by the timetable.
- Switch to **Editor Mode** to build grid-based track. Straight and curved tiles connect through matching edges; connect a **Depot** to a **Station** to add an operating route that trains automatically use. Press **R** to rotate track pieces; demolishing a piece refunds 50% of its cost.
- Trains earn payments for safe signal passages and station calls. Use the pause button to stop the simulation.
