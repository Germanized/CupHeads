'use strict';

const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('installer', {
  getAppVersion:    ()    => ipcRenderer.invoke('get-app-version'),
  detectCuphead:    ()    => ipcRenderer.invoke('detect-cuphead'),
  browseFolder:     ()    => ipcRenderer.invoke('browse-folder'),
  checkInstallState: (d)  => ipcRenderer.invoke('check-install-state', d),
  openFolder:       (d)   => ipcRenderer.invoke('open-folder', d),
  launchSteam:      ()    => ipcRenderer.invoke('launch-steam'),
  launchCuphead:    ()    => ipcRenderer.invoke('launch-cuphead'),
  verifyInstall:    (d)   => ipcRenderer.invoke('verify-install', d),
  startInstall:     (o)   => ipcRenderer.send('install', o),
  onInstallProgress: (cb) => ipcRenderer.on('install-progress', (_, d) => cb(d)),
  windowClose:      ()    => ipcRenderer.send('window-close'),
  windowMinimize:   ()    => ipcRenderer.send('window-minimize'),
});
