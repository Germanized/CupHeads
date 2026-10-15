'use strict';

const { app, BrowserWindow, ipcMain, dialog, shell } = require('electron');
const path = require('path');
const fs = require('fs');
const https = require('https');
const http = require('http');
const os = require('os');
const crypto = require('crypto');
const { execSync } = require('child_process');

const CUPHEAD_STEAM_APP_ID = '268910';

let mainWindow;

const DEFAULT_INSTALL_MANIFEST = Object.freeze({
  pluginFolder: 'CupheadOnline',
  files: [
    { name: 'CupheadOnline.dll', required: true, target: 'CupheadOnline.dll' },
    { name: 'StartupSplash/CupHeadsIntro.mp4', required: false, target: 'Assets/CupHeadsIntro.mp4' },
  ],
  legacyCleanup: [
    'LiteNetLib.dll',
  ],
});

function createWindow() {
  mainWindow = new BrowserWindow({
    title: 'CupHeads Installer',
    width: 880,
    height: 620,
    frame: false,
    resizable: false,
    backgroundColor: '#080808',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });

  mainWindow.loadFile(path.join(__dirname, 'renderer', 'index.html'));

  mainWindow.webContents.on('before-input-event', (_, input) => {
    if (input.key === 'F12' && input.type === 'keyDown')
      mainWindow.webContents.toggleDevTools();
  });

  mainWindow.webContents.on('did-fail-load', (_, code, desc) => {
    console.error('[main] Page failed to load:', code, desc);
  });

  mainWindow.webContents.on('render-process-gone', (_, details) => {
    console.error('[main] Renderer crashed:', details.reason);
  });
}

app.whenReady().then(createWindow);
app.on('window-all-closed', () => app.quit());

ipcMain.on('window-close', () => app.quit());
ipcMain.on('window-minimize', () => mainWindow && mainWindow.minimize());
ipcMain.handle('get-app-version', () => app.getVersion());

function getSteamPath() {
  const keys = [
    'HKLM\\SOFTWARE\\WOW6432Node\\Valve\\Steam',
    'HKLM\\SOFTWARE\\Valve\\Steam',
    'HKCU\\SOFTWARE\\Valve\\Steam',
  ];

  for (const key of keys) {
    try {
      const out = execSync(`reg query "${key}" /v InstallPath`, {
        encoding: 'utf8',
        timeout: 3000,
      });
      const match = out.match(/InstallPath\s+REG_SZ\s+(.+)/);
      if (match) return match[1].trim();
    } catch {
      // continue
    }
  }

  const fallbacks = [
    'C:\\Program Files (x86)\\Steam',
    'C:\\Program Files\\Steam',
    path.join(os.homedir(), 'Steam'),
  ];

  for (const candidate of fallbacks) {
    if (fs.existsSync(path.join(candidate, 'steam.exe')))
      return candidate;
  }

  return null;
}

function parseVdfLibraryPaths(content) {
  const paths = [];
  const pattern = /"path"\s+"([^"]+)"/gi;
  let match;

  while ((match = pattern.exec(content)) !== null)
    paths.push(match[1].replace(/\\\\/g, '\\'));

  return paths;
}

function findCuphead() {
  const steamPath = getSteamPath();
  const candidates = steamPath ? [steamPath] : [];

  if (steamPath) {
    const vdfPath = path.join(steamPath, 'steamapps', 'libraryfolders.vdf');
    if (fs.existsSync(vdfPath)) {
      const extraPaths = parseVdfLibraryPaths(fs.readFileSync(vdfPath, 'utf8'));
      candidates.push(...extraPaths);
    }
  }

  for (const libraryPath of candidates) {
    const cupheadDir = path.join(libraryPath, 'steamapps', 'common', 'Cuphead');
    if (fs.existsSync(path.join(cupheadDir, 'Cuphead.exe')))
      return cupheadDir;
  }

  return null;
}

function getBundledAssetRoot() {
  return app.isPackaged
    ? path.join(process.resourcesPath, 'assets')
    : path.join(__dirname, 'assets');
}

function normalizeInstallManifest(raw) {
  const manifest = Object.assign({}, DEFAULT_INSTALL_MANIFEST, raw || {});

  const files = Array.isArray(manifest.files) ? manifest.files : DEFAULT_INSTALL_MANIFEST.files;
  manifest.files = files
    .filter((file) => file && typeof file.name === 'string' && file.name.trim().length > 0)
    .map((file) => ({
      name: file.name,
      required: file.required !== false,
      target: typeof file.target === 'string' && file.target.trim().length > 0 ? file.target : file.name,
    }));

  if (!manifest.files.length)
    manifest.files = DEFAULT_INSTALL_MANIFEST.files.slice();

  manifest.pluginFolder = typeof manifest.pluginFolder === 'string' && manifest.pluginFolder.trim().length > 0
    ? manifest.pluginFolder
    : DEFAULT_INSTALL_MANIFEST.pluginFolder;

  manifest.legacyCleanup = Array.isArray(manifest.legacyCleanup)
    ? manifest.legacyCleanup.filter((name) => typeof name === 'string' && name.trim().length > 0)
    : DEFAULT_INSTALL_MANIFEST.legacyCleanup.slice();

  return manifest;
}

function loadInstallManifest() {
  const manifestPath = path.join(getBundledAssetRoot(), 'install-manifest.json');

  if (!fs.existsSync(manifestPath))
    return normalizeInstallManifest(null);

  try {
    const raw = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    return normalizeInstallManifest(raw);
  } catch (err) {
    console.warn('[main] Failed to read install manifest:', err.message);
    return normalizeInstallManifest(null);
  }
}

function getPluginDir(cupheadDir, manifest) {
  const activeManifest = manifest || loadInstallManifest();
  return path.join(cupheadDir, 'BepInEx', 'plugins', activeManifest.pluginFolder);
}

function isCupheadRunning() {
  try {
    const output = execSync('tasklist /FI "IMAGENAME eq Cuphead.exe" /NH', {
      encoding: 'utf8',
      timeout: 3000,
      windowsHide: true,
    });
    return /\bCuphead\.exe\b/i.test(output);
  } catch {
    return false;
  }
}

function sha256File(filePath) {
  return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex');
}

/**
 * Verifies the installed DLL against the one bundled in this installer.
 * A byte-for-byte hash match is the only reliable "is this our build" check;
 * the old version-string scan matched any DLL that happened to contain the
 * same "x.y.z" text. The version strings are still reported for diagnostics.
 */
function inspectPluginDllVersion(dllPath) {
  const expectedVersion = app.getVersion();
  const result = {
    path: dllPath,
    expectedVersion,
    detectedVersions: [],
    matchesExpected: false,
  };

  try {
    if (!dllPath || !fs.existsSync(dllPath))
      return result;

    const text = fs.readFileSync(dllPath).toString('utf16le');
    const versions = text.match(/\b\d+\.\d+\.\d+\b/g) || [];
    result.detectedVersions = Array.from(new Set(versions));

    const bundledDll = resolveBundledFile('CupheadOnline.dll');
    if (fs.existsSync(bundledDll)) {
      result.matchesExpected = sha256File(dllPath) === sha256File(bundledDll);
      result.method = 'sha256';
    } else {
      result.matchesExpected = text.includes(expectedVersion);
      result.method = 'version-string';
    }
  } catch (err) {
    result.error = err.message;
  }

  return result;
}

function getInstallState(dir) {
  const manifest = loadInstallManifest();

  if (!dir || !fs.existsSync(dir)) {
    return {
      valid: false,
      hasBepInEx: false,
      hasBepInExCore: false,
      hasDoorstop: false,
      hasPlugin: false,
      ready: false,
      pluginDir: '',
      installedPluginFiles: [],
      missingPluginFiles: manifest.files.map((file) => file.target),
      hasLegacyFiles: false,
      legacyFilesPresent: [],
      pluginVersion: null,
      pluginVersionOk: false,
      manifest,
    };
  }

  const pluginDir = getPluginDir(dir, manifest);
  const installedPluginFiles = [];
  const missingPluginFiles = [];

  for (const file of manifest.files) {
    const targetPath = path.join(pluginDir, file.target);
    if (fs.existsSync(targetPath))
      installedPluginFiles.push(file.target);
    else if (file.required)
      missingPluginFiles.push(file.target);
  }

  const legacyFilesPresent = manifest.legacyCleanup.filter((name) =>
    fs.existsSync(path.join(pluginDir, name)));
  const pluginDllPath = path.join(pluginDir, 'CupheadOnline.dll');
  const pluginVersion = inspectPluginDllVersion(pluginDllPath);

  const state = {
    valid: fs.existsSync(path.join(dir, 'Cuphead.exe')),
    hasBepInEx: fs.existsSync(path.join(dir, 'BepInEx')),
    hasBepInExCore: fs.existsSync(path.join(dir, 'BepInEx', 'core', 'BepInEx.dll')),
    hasDoorstop: fs.existsSync(path.join(dir, 'winhttp.dll')),
    hasPlugin: missingPluginFiles.length === 0,
    pluginDir,
    installedPluginFiles,
    missingPluginFiles,
    hasLegacyFiles: legacyFilesPresent.length > 0,
    legacyFilesPresent,
    pluginVersion,
    pluginVersionOk: pluginVersion.matchesExpected,
    manifest,
  };

  state.ready = state.valid && state.hasBepInExCore && state.hasDoorstop && state.hasPlugin;
  return state;
}

function verifyInstall(dir) {
  const checks = getInstallState(dir);

  if (!checks.valid) {
    return {
      ok: false,
      message: 'Cuphead.exe was not found in that folder.',
      checks,
    };
  }

  const missing = [];
  if (!checks.hasBepInExCore) missing.push('BepInEx core');
  if (!checks.hasDoorstop) missing.push('winhttp.dll bootstrap');
  if (checks.missingPluginFiles.length > 0)
    missing.push(...checks.missingPluginFiles);

  if (missing.length > 0) {
    return {
      ok: false,
      message: 'Missing: ' + missing.join(', ') + '.',
      checks,
    };
  }

  if (!checks.pluginVersionOk) {
    const detected = checks.pluginVersion && checks.pluginVersion.detectedVersions.length
      ? checks.pluginVersion.detectedVersions.join(', ')
      : 'unknown';
    return {
      ok: false,
      message: 'The installed CupheadOnline.dll is not the build bundled with this installer (v'
        + app.getVersion()
        + '; found version strings: '
        + detected
        + '). Close Cuphead, press Install again, and both players should use the same installer.',
      checks,
    };
  }

  if (checks.hasLegacyFiles) {
    return {
      ok: true,
      message: 'Install is usable, but legacy files were found: '
        + checks.legacyFilesPresent.join(', ')
        + '. Run Install to refresh the mod and clean them up.',
      checks,
    };
  }

  return {
    ok: true,
    message: 'Install looks good. BepInEx and CupHeads ' + app.getVersion() + ' are in place.',
    checks,
  };
}

function resolveBundledFile(fileName) {
  return path.join(getBundledAssetRoot(), fileName);
}

function describeModFileCopyFailure(err, file) {
  const target = file && file.destinationPath ? file.destinationPath : 'the Cuphead plugin folder';
  const code = err && err.code ? String(err.code) : 'UNKNOWN';
  return [
    'Windows blocked the installer while refreshing ' + (file ? file.target : 'the mod DLL') + '.',
    '',
    'Close Cuphead and Steam if either one is using the DLL, then run CupHeads.exe as Administrator and press Install again.',
    '',
    'Target: ' + target,
    'Error: ' + code + (err && err.message ? ' - ' + err.message : ''),
  ].join('\n');
}

function copyBundledModFile(file) {
  const tempPath = file.destinationPath + '.tmp-' + process.pid;

  try {
    fs.mkdirSync(path.dirname(file.destinationPath), { recursive: true });
    try { if (fs.existsSync(tempPath)) fs.unlinkSync(tempPath); } catch { /* best-effort */ }
    fs.copyFileSync(file.sourcePath, tempPath);
    fs.renameSync(tempPath, file.destinationPath);
  } catch (err) {
    try { if (fs.existsSync(tempPath)) fs.unlinkSync(tempPath); } catch { /* best-effort */ }
    throw new Error(describeModFileCopyFailure(err, file));
  }
}

ipcMain.handle('detect-cuphead', () => {
  try {
    return { path: findCuphead(), error: null };
  } catch (err) {
    return { path: null, error: err.message };
  }
});

ipcMain.handle('browse-folder', async () => {
  const result = await dialog.showOpenDialog(mainWindow, {
    properties: ['openDirectory'],
    title: 'Select Cuphead Installation Folder',
  });

  return result.canceled ? null : result.filePaths[0];
});

ipcMain.handle('check-install-state', (_, dir) => getInstallState(dir));

ipcMain.handle('open-folder', async (_, dir) => {
  if (!dir || !fs.existsSync(dir)) {
    return { ok: false, message: 'Pick a valid Cuphead folder first.' };
  }

  const error = await shell.openPath(dir);
  return error
    ? { ok: false, message: error }
    : { ok: true, message: 'Opened the Cuphead folder.' };
});

ipcMain.handle('launch-steam', async () => {
  try {
    await shell.openExternal('steam://open/main');
    return { ok: true, message: 'Sent a launch request to Steam.' };
  } catch (err) {
    return { ok: false, message: err.message };
  }
});

ipcMain.handle('launch-cuphead', async () => {
  if (isCupheadRunning())
    return { ok: true, message: 'Cuphead is already running.' };

  try {
    await shell.openExternal('steam://rungameid/' + CUPHEAD_STEAM_APP_ID);
    return { ok: true, message: 'Asked Steam to launch Cuphead with CupHeads.' };
  } catch (err) {
    return { ok: false, message: err.message };
  }
});

ipcMain.handle('verify-install', (_, dir) => verifyInstall(dir));

function getCupheadArch(cupheadDir) {
  try {
    const exePath = path.join(cupheadDir, 'Cuphead.exe');
    const buf = Buffer.alloc(0x200);
    const fd = fs.openSync(exePath, 'r');
    fs.readSync(fd, buf, 0, 0x200, 0);
    fs.closeSync(fd);

    if (buf.readUInt16LE(0) !== 0x5A4D) return 'x64';

    const peOffset = buf.readUInt32LE(0x3C);
    if (peOffset + 6 > buf.length) return 'x64';
    if (buf.readUInt32LE(peOffset) !== 0x4550) return 'x64';

    const machine = buf.readUInt16LE(peOffset + 4);
    return machine === 0x014C ? 'x86' : 'x64';
  } catch {
    return 'x64';
  }
}

function download(url, dest, onProgress) {
  return new Promise((resolve, reject) => {
    const file = fs.createWriteStream(dest);
    let redirectCount = 0;
    let activeReq = null;
    let settled = false;
    let idleTimer = null;

    function cleanupTemp() {
      try { file.destroy(); } catch { /* best-effort */ }
      try {
        if (fs.existsSync(dest))
          fs.unlinkSync(dest);
      } catch {
        /* best-effort */
      }
    }

    function fail(err) {
      if (settled) return;
      settled = true;
      if (idleTimer)
        clearTimeout(idleTimer);
      try { if (activeReq) activeReq.destroy(); } catch { /* best-effort */ }
      cleanupTemp();
      reject(err);
    }

    function armTimeout() {
      if (idleTimer)
        clearTimeout(idleTimer);
      idleTimer = setTimeout(() => {
        fail(new Error('BepInEx download timed out. Please try again in a minute.'));
      }, 30000);
    }

    function doGet(requestUrl) {
      armTimeout();
      const transport = requestUrl.startsWith('https') ? https : http;
      const req = transport.get(requestUrl, {
        headers: {
          'User-Agent': 'CupHeads-Installer',
          Accept: 'application/octet-stream,*/*',
        },
      }, (res) => {
        armTimeout();
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume(); // release the socket of the redirect response
          if (++redirectCount > 10) {
            fail(new Error('Too many redirects while downloading BepInEx.'));
            return;
          }
          doGet(new URL(res.headers.location, requestUrl).toString());
          return;
        }

        if (res.statusCode !== 200) {
          res.resume();
          fail(new Error('Download failed (HTTP ' + res.statusCode + ')'));
          return;
        }

        const total = parseInt(res.headers['content-length'] || '0', 10);
        let received = 0;

        res.on('data', (chunk) => {
          armTimeout();
          received += chunk.length;
          if (total > 0 && onProgress)
            onProgress(Math.round((received / total) * 100));
        });

        res.pipe(file);
        res.on('error', fail);
      });

      activeReq = req;
      req.setTimeout(30000, () => fail(new Error('BepInEx download timed out. Please try again in a minute.')));
      req.on('error', fail);
    }

    file.on('finish', () => {
      if (settled) return;
      settled = true;
      if (idleTimer)
        clearTimeout(idleTimer);
      file.close(resolve);
    });
    file.on('error', fail);

    doGet(url);
  });
}

const BEPINEX_VERSION = '5.4.23.5';
const BEPINEX = {
  x64: `https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x64_${BEPINEX_VERSION}.zip`,
  x86: `https://github.com/BepInEx/BepInEx/releases/download/v${BEPINEX_VERSION}/BepInEx_win_x86_${BEPINEX_VERSION}.zip`,
};

function getBundledBepInExArchive(arch) {
  const assetRoot = getBundledAssetRoot();
  const patterns = arch === 'x64'
    ? [/^BepInEx_win_x64_.*\.zip$/i, /^BepInEx_x64_.*\.zip$/i]
    : [/^BepInEx_win_x86_.*\.zip$/i, /^BepInEx_x86_.*\.zip$/i];

  for (const pattern of patterns) {
    const match = fs.readdirSync(assetRoot).find((name) => pattern.test(name));
    if (match)
      return path.join(assetRoot, match);
  }

  return null;
}

// The mod is built against the BepInEx 5 API, so the fallback download is
// pinned to the same 5.x release the installer bundles. "Latest" on GitHub
// could resolve to an incompatible major version.
function getBepInExDownloadUrl(arch) {
  return BEPINEX[arch] || BEPINEX.x64;
}

let installInProgress = false;

ipcMain.on('install', async (event, { cupheadDir, skipBepInEx }) => {
  const send = (type, data) => {
    try {
      if (!event.sender.isDestroyed())
        event.sender.send('install-progress', Object.assign({ type }, data || {}));
    } catch {
      // window closed
    }
  };

  if (installInProgress) {
    send('error', { message: 'An install is already running. Wait for it to finish.' });
    return;
  }

  installInProgress = true;
  try {
    const manifest = loadInstallManifest();
    const existingState = getInstallState(cupheadDir);

    if (!existingState.valid)
      throw new Error('Pick a valid Cuphead folder before installing.');

    if (isCupheadRunning()) {
      throw new Error(
        'Cuphead is currently running. Close the game completely, then press Install again so Windows can replace the old mod DLL.'
      );
    }

    if (!skipBepInEx) {
      const arch = getCupheadArch(cupheadDir);
      const bundledArchive = getBundledBepInExArchive(arch);
      let zipPath = bundledArchive;

      if (bundledArchive) {
        send('step', {
          step: 'bepinex',
          status: 'downloading',
          progress: 100,
          arch,
          message: 'Using bundled BepInEx repair package from the installer...',
        });
      } else {
        const zipUrl = getBepInExDownloadUrl(arch);
        zipPath = path.join(os.tmpdir(), path.basename(new URL(zipUrl).pathname));

        send('step', {
          step: 'bepinex',
          status: 'downloading',
          progress: 0,
          arch,
          message: 'Downloading BepInEx repair package from GitHub...',
        });

        await download(zipUrl, zipPath, (pct) => {
          send('step', {
            step: 'bepinex',
            status: 'downloading',
            progress: pct,
            arch,
            message: `Downloading BepInEx repair package from GitHub... ${pct}%`,
          });
        });
      }

      send('step', {
        step: 'bepinex',
        status: 'extracting',
        progress: 100,
        message: 'Extracting BepInEx into Cuphead... This can take a minute on some drives.',
      });

      const AdmZip = require('adm-zip');
      new AdmZip(zipPath).extractAllTo(cupheadDir, true);
      if (!bundledArchive) {
        try { fs.unlinkSync(zipPath); } catch { /* best-effort */ }
      }

      send('step', {
        step: 'bepinex',
        status: 'done',
        progress: 100,
        message: bundledArchive
          ? 'BepInEx is ready from the bundled repair package.'
          : 'BepInEx is ready.',
      });
    } else {
      send('step', {
        step: 'bepinex',
        status: 'skipped',
        progress: 100,
        message: 'BepInEx already looks healthy.',
      });
    }

    const pluginDir = getPluginDir(cupheadDir, manifest);
    fs.mkdirSync(pluginDir, { recursive: true });

    const bundledFiles = [];
    for (const file of manifest.files) {
      const sourcePath = resolveBundledFile(file.name);
      if (!fs.existsSync(sourcePath)) {
        if (file.required) {
          throw new Error(
            file.name + ' was not found in the installer package.\n'
            + 'Rebuild the installer after staging the latest bundled files.'
          );
        }

        continue;
      }

      bundledFiles.push({
        name: file.name,
        target: file.target,
        sourcePath,
        destinationPath: path.join(pluginDir, file.target),
      });
    }

    send('step', {
      step: 'plugin',
      status: 'installing',
      progress: 0,
      message: 'Refreshing bundled mod files...',
    });

    for (let i = 0; i < bundledFiles.length; i += 1) {
      const file = bundledFiles[i];
      copyBundledModFile(file);

      const progress = Math.round(((i + 1) / bundledFiles.length) * 100);
      send('step', {
        step: 'plugin',
        status: 'installing',
        progress,
        message: 'Refreshing bundled mod files...',
      });
    }

    send('step', {
      step: 'plugin',
      status: 'done',
      progress: 100,
      message: 'Bundled mod files refreshed.',
      files: bundledFiles.map((file) => file.target),
    });

    send('step', {
      step: 'cleanup',
      status: 'installing',
      progress: 0,
      message: 'Cleaning legacy networking leftovers...',
    });

    const removedFiles = [];
    for (const fileName of manifest.legacyCleanup) {
      const targetPath = path.join(pluginDir, fileName);
      if (fs.existsSync(targetPath)) {
        fs.unlinkSync(targetPath);
        removedFiles.push(fileName);
      }
    }

    send('step', {
      step: 'cleanup',
      status: removedFiles.length > 0 ? 'done' : 'skipped',
      progress: 100,
      message: removedFiles.length > 0
        ? 'Removed legacy files: ' + removedFiles.join(', ') + '.'
        : 'No legacy files needed cleanup.',
      removedFiles,
    });

    send('step', {
      step: 'verify',
      status: 'installing',
      progress: 0,
      message: 'Verifying final install...',
    });

    const verification = verifyInstall(cupheadDir);
    if (!verification.ok)
      throw new Error(verification.message);

    send('step', {
      step: 'verify',
      status: 'done',
      progress: 100,
      message: verification.message,
    });

    send('done', {
      summary: verification.message,
      removedFiles,
      installedFiles: bundledFiles.map((file) => file.target),
    });
  } catch (err) {
    send('error', { message: err.message });
  } finally {
    installInProgress = false;
  }
});
