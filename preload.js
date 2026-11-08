const { contextBridge, ipcRenderer } = require("electron");

contextBridge.exposeInMainWorld("signallerApp", {
  getVersion: () => ipcRenderer.sendSync("app:get-version"),
  checkForUpdates: () => ipcRenderer.invoke("app:check-for-updates"),
  installUpdate: () => ipcRenderer.invoke("app:install-update"),
  onUpdateStatus: (callback) => {
    const listener = (_event, status) => callback(status);
    ipcRenderer.on("app:update-status", listener);
    return () => ipcRenderer.removeListener("app:update-status", listener);
  }
});
