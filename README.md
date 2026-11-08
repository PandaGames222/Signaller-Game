# Signaller

A standalone Windows desktop railway signalling game built with Electron, Node.js, and an HTML5 Canvas. Run or install the game as a desktop app; it does not need a browser, account, or game server.

## Build the Windows installer

- Node.js 18 or newer
- npm (included with Node.js)

From a terminal in the project folder:

```sh
npm install
npm run dist:win
```

The Windows x64 installer is written to `dist/Signaller-Setup-1.0.0.exe`. Run it to install Signaller with Start Menu and desktop shortcuts. The installer uses a stable Windows application ID, so running a newer installer upgrades an existing Signaller installation instead of creating a second copy. Your app data is not deleted during an upgrade or uninstall.

To run the desktop app directly during development, use `npm start`.

## Automatic updates

The installed app displays its installed version and checks the configured GitHub releases for updates. When a newer version is published, it downloads in the background; select **Restart to update** to install it. The NSIS installer also upgrades the existing installation if you download and run it manually.

To publish a release:

1. Increment the version in `package.json` and commit/push the change.
2. Push a matching `v<version>` tag (for example, `v1.0.1`).

The [GitHub Actions workflow](./.github/workflows/release.yml) builds the Windows installer and uploads the installer, blockmap, and updater metadata. The repository `PandaGames222/Signaller-Game` is public so installed copies can access releases without an embedded private token. Each published release is available to download and powers the app's built-in updater.

## Play

- In **Signaller Mode**, click a signal to cycle red → yellow → green. Click the points to switch between straight and diverging. Trains stop at red signals and points that do not match their assigned route.
- Switch to **Editor Mode**, select a build item, and click a grid tile to place it. Placed items can be removed in demolish mode for a 50% refund.
- The train runs the Greyfield Loop and earns section payments while the simulation is running. Use the pause button to stop the simulation.

Editor additions are visual prototypes and do not yet alter the operating route.
