# Signaller

Signaller is a standalone Windows railway-signalling game built with native WPF and .NET 8. It draws its track network directly with WPF; it does not use a browser, Electron, HTML, or a web server.

## Run from source

Install the .NET 8 SDK and, on Windows, run these commands from the project directory:

```powershell
dotnet run --project Signaller.csproj
dotnet run --project tests\Signaller.GameLogic.Tests.csproj
```

The WPF application targets Windows x64. Game saves are stored under `%LOCALAPPDATA%\Signaller\game-state.json`.

## Build a standalone installer

Publish a self-contained, single-file Windows build and package it with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
dotnet publish Signaller.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist\publish
ISCC.exe /DAppVersion=1.2.0 /DPublishDir="..\dist\publish" /DOutputDir="..\dist" installer\Signaller.iss
```

The installer is written to `dist\Signaller-Setup-1.2.0.exe`. It installs per-user without administrator privileges and uses a stable application ID; installing a newer version upgrades the existing installation and keeps the saved game.

## Automatic updates and GitHub releases

The installed app checks GitHub when it starts and every five minutes while running. When an update is available, choose **UPDATE** to open the in-game updater. It downloads and verifies the installer, closes the game, installs in the background, and reopens the updated game. Your saved game is kept.

If you are upgrading from the original 1.0 Electron app, run the latest native installer once; future native releases can be installed from the app's update button.

To publish a release:

1. Update the `<Version>` in `Signaller.csproj` and commit the change.
2. Push a matching version tag, such as `v1.2.0`.

The [GitHub Actions workflow](./.github/workflows/release.yml) validates the tag, runs the game-logic regression tests, builds the self-contained Windows application, packages it, and attaches the installer to the GitHub release. Public release downloads let installed copies check and update without an embedded token.

## Play

- In **Signaller Mode**, click a signal to cycle red → yellow → green and click the points to change their direction. A train stops safely before a red signal or points set against its assigned route.
- Switch to **Editor Mode**, select a trackside item, and click a clear grid tile to place it. Demolishing a piece refunds 50% of its cost.
- The train runs the Greyfield Loop and earns payments for safe section and destination crossings. Use the pause button to stop the simulation.

Editor additions are visual prototypes and do not yet alter the operating route.
