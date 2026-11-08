const { app, BrowserWindow, ipcMain } = require("electron");
const { autoUpdater } = require("electron-updater");
const path = require("node:path");

let mainWindow;

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1440,
    height: 900,
    minWidth: 1024,
    minHeight: 680,
    backgroundColor: "#0d1218",
    title: "Signaller",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true
    }
  });

  mainWindow.loadFile(path.join(__dirname, "index.html"));
  mainWindow.on("closed", () => {
    mainWindow = null;
  });
}

function sendUpdateStatus(status) {
  if (mainWindow && !mainWindow.isDestroyed()) {
    mainWindow.webContents.send("app:update-status", status);
  }
}

function configureUpdates() {
  ipcMain.on("app:get-version", (event) => {
    event.returnValue = app.getVersion();
  });

  autoUpdater.autoDownload = true;
  autoUpdater.autoInstallOnAppQuit = true;
  autoUpdater.allowPrerelease = false;

  autoUpdater.on("checking-for-update", () => {
    sendUpdateStatus({ status: "checking" });
  });
  autoUpdater.on("update-available", (info) => {
    sendUpdateStatus({ status: "downloading", version: info.version });
  });
  autoUpdater.on("update-not-available", () => {
    sendUpdateStatus({ status: "current" });
  });
  autoUpdater.on("download-progress", (progress) => {
    sendUpdateStatus({ status: "downloading", percent: progress.percent });
  });
  autoUpdater.on("update-downloaded", (info) => {
    sendUpdateStatus({ status: "downloaded", version: info.version });
  });
  autoUpdater.on("error", (error) => {
    console.error("Signaller update error:", error);
    sendUpdateStatus({ status: "error", message: error.message });
  });

  ipcMain.handle("app:check-for-updates", async () => {
    if (!app.isPackaged) return { status: "development" };
    if (process.platform !== "win32") return { status: "unsupported" };

    try {
      await autoUpdater.checkForUpdates();
      return { status: "checking" };
    } catch (error) {
      console.error("Could not check for Signaller updates:", error);
      return { status: "error", message: error.message };
    }
  });

  ipcMain.handle("app:install-update", () => {
    if (!app.isPackaged || process.platform !== "win32") return false;
    autoUpdater.quitAndInstall();
    return true;
  });

  if (app.isPackaged && process.platform === "win32") {
    setTimeout(() => {
      autoUpdater.checkForUpdates().catch((error) => {
        console.error("Could not check for Signaller updates:", error);
        sendUpdateStatus({ status: "error", message: error.message });
      });
    }, 4000);
  }
}

app.whenReady().then(() => {
  createWindow();
  configureUpdates();

  app.on("activate", () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on("window-all-closed", () => {
  mainWindow = null;
  if (process.platform !== "darwin") app.quit();
});
