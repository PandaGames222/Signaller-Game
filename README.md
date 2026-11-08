# Signaller

A standalone desktop railway signalling game built with Electron, Node.js, and an HTML5 Canvas. The supplied network runs locally; it does not require an account or a game server.

## Requirements

- Node.js 18 or newer
- npm (included with Node.js)

## Run the game

1. Open this project folder in Visual Studio Code.
2. Open the integrated terminal in the project folder.
3. Install the project dependencies:

   ```sh
   npm install
   ```

4. Start the desktop app:

   ```sh
   npm start
   ```

## Play

- In **Signaller Mode**, click a signal to cycle red → yellow → green. Click the points to switch between straight and diverging. Trains stop at red signals and points that do not match their assigned route.
- Switch to **Editor Mode**, select a build item, and click a grid tile to place it. Placed items can be removed in demolish mode for a 50% refund.
- The train runs the Greyfield Loop and earns section payments while the simulation is running. Use the pause button to stop the simulation.

Editor additions are visual prototypes and do not yet alter the operating route.
