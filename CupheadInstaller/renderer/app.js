'use strict';

window.onerror = (msg, src, line, col, err) => {
  const el = document.getElementById('global-error');
  if (el) {
    el.textContent = 'JS Error: ' + msg + ' (' + src + ':' + line + ')';
    el.style.display = 'block';
  }
  console.error('[app]', msg, err);
};

if (typeof installer === 'undefined')
  console.error('[app] installer is undefined - preload may have failed');

let currentView = 0;
let cupheadPath = null;
let installState = null;
let skipBepInExRepair = false;

function goTo(idx, direction = 1) {
  const views = document.querySelectorAll('.view');
  const active = document.querySelector('.view.active');

  if (active) {
    active.classList.remove('active');
    active.classList.add(direction >= 0 ? 'exit-left' : 'enter-left');
    setTimeout(() => active.classList.remove('exit-left', 'enter-left'), 350);
  }

  const next = views[idx];
  next.classList.add(direction >= 0 ? 'enter-right' : 'enter-left');
  void next.offsetWidth;
  next.classList.remove('enter-right', 'enter-left');
  next.classList.add('active');

  document.querySelectorAll('.rail-step').forEach((el, i) => {
    el.classList.toggle('active', i === idx);
    el.classList.toggle('done', i < idx);
  });

  const fill = document.querySelector('.rail-fill');
  if (fill)
    fill.style.setProperty('--progress', ((idx / 3) * 100) + '%');

  currentView = idx;
  if (idx === 1) onEnterDetect();
  if (idx === 2) onEnterInstall();
}

function set(id, visible) {
  const el = document.getElementById(id);
  if (el)
    el.classList.toggle('hidden', !visible);
}

function setText(id, text) {
  const el = document.getElementById(id);
  if (el)
    el.textContent = text;
}

function disable(id, value) {
  const el = document.getElementById(id);
  if (el)
    el.disabled = value;
}

function fill(id, pct) {
  const el = document.getElementById(id);
  if (el)
    el.style.width = pct + '%';
}

function setDetect(state) {
  set('detect-scanning', state === 'scanning');
  set('detect-notfound', state === 'notfound');
  set('detect-found', state === 'found');
  set('badge-row', state === 'found');
  set('install-plan', state === 'found');
}

function setToolNote(kind, text) {
  const el = document.getElementById('detect-tool-note');
  if (!el) return;

  if (!text) {
    el.textContent = '';
    el.className = 'tool-note hidden';
    return;
  }

  el.textContent = text;
  el.className = 'tool-note ' + (kind || '');
}

function reportGlobalError(text) {
  const el = document.getElementById('global-error');
  if (!el) return;
  el.textContent = text;
  el.style.display = text ? 'block' : 'none';
}

function updateUtilityButtons() {
  const hasPath = !!cupheadPath;
  disable('btn-open-folder', !hasPath);
  disable('btn-verify', !hasPath);
  disable('btn-done-open-folder', !hasPath);
}

function badge(id, ok, text) {
  const el = document.getElementById(id);
  if (!el) return;

  el.className = 'status-badge ' + (ok ? 'ok' : 'nok');
  const value = el.querySelector('.badge-val');
  if (value)
    value.textContent = text;
}

function updateInstallPlan(state) {
  if (!state) {
    setText('install-plan-note', '');
    return;
  }

  const sentences = [];
  if (state.hasBepInExCore && state.hasDoorstop)
    sentences.push('BepInEx is already healthy, so only the mod files will be refreshed.');
  else
    sentences.push('BepInEx will be repaired automatically from the installer bundle before the mod is copied.');

  sentences.push('CupHeads mod files are refreshed every time you press Install, even if the DLL is already there.');

  if (state.hasLegacyFiles)
    sentences.push('Legacy LiteNetLib leftovers from older builds will be removed automatically.');
  else
    sentences.push('A final verification pass runs before the installer finishes.');

  setText('install-plan-note', sentences.join(' '));
}

function updateInstallSummary(state) {
  if (!state) {
    setText('install-summary-body', '');
    return;
  }

  const parts = [];
  parts.push(skipBepInExRepair
    ? 'BepInEx will be kept as-is unless the verify pass finds damage.'
    : 'BepInEx needs repair, so the installer will use its bundled repair package and only fall back to GitHub if that bundle is missing.');
  parts.push('Bundled CupHeads files will be overwritten with the latest installer copy.');

  if (state.hasLegacyFiles)
    parts.push('Old network leftovers like LiteNetLib.dll will be cleaned out.');

  setText('install-summary-body', parts.join(' '));
}

async function applyPath(dir) {
  setDetect('found');
  setText('detect-path', dir);

  const state = await installer.checkInstallState(dir);
  installState = state;

  if (!state.valid) {
    cupheadPath = null;
    installState = null;
    skipBepInExRepair = false;
    setDetect('notfound');
    updateUtilityButtons();
    disable('btn-d-next', true);
    updateInstallPlan(null);
    updateInstallSummary(null);
    return;
  }

  cupheadPath = dir;
  skipBepInExRepair = !!state.hasBepInExCore && !!state.hasDoorstop;

  badge(
    'badge-bep',
    skipBepInExRepair,
    skipBepInExRepair
      ? 'Healthy'
      : (state.hasBepInEx ? 'Needs repair' : 'Will install')
  );

  badge(
    'badge-plug',
    !!state.hasPlugin,
    state.hasPlugin
      ? 'Installed - will refresh'
      : (state.installedPluginFiles && state.installedPluginFiles.length > 0
        ? 'Needs repair'
        : 'Will install')
  );

  updateInstallPlan(state);
  updateInstallSummary(state);

  if (state.hasLegacyFiles) {
    setToolNote(
      'warn',
      'Legacy LiteNetLib.dll was found from an older build. Install will remove it automatically.'
    );
  } else if (state.hasPlugin) {
    setToolNote(
      'ok',
      'Install works like a repair/update pass: the bundled mod files are refreshed every time.'
    );
  } else {
    setToolNote(
      '',
      'Install will copy the latest bundled files, repair missing prerequisites, and verify everything when it finishes.'
    );
  }

  updateUtilityButtons();
  disable('btn-d-next', false);
}

async function onEnterDetect() {
  cupheadPath = null;
  installState = null;
  skipBepInExRepair = false;

  setDetect('scanning');
  setToolNote('', '');
  updateUtilityButtons();
  disable('btn-d-next', true);
  updateInstallPlan(null);
  updateInstallSummary(null);

  try {
    const result = await installer.detectCuphead();
    if (result && result.path)
      await applyPath(result.path);
    else
      setDetect('notfound');
  } catch (err) {
    console.error('[detect]', err);
    setDetect('notfound');
  }
}

function resetItem(id) {
  const el = document.getElementById(id);
  if (!el) return;

  el.className = 'install-item';
  const status = document.getElementById(id + '-status');
  if (status)
    status.textContent = 'Waiting';

  const ring = document.getElementById(id + '-ring');
  if (ring) {
    ring.classList.remove('done');
    ring.innerHTML =
      '<svg viewBox="0 0 32 32" fill="none">' +
      '<circle cx="16" cy="16" r="14" stroke="currentColor" stroke-width="1.5"/>' +
      '</svg>';
  }
}

function setItem(id, state, statusText) {
  const el = document.getElementById(id);
  if (!el) return;

  el.className = 'install-item ' + state;
  const status = document.getElementById(id + '-status');
  if (status && statusText)
    status.textContent = statusText;
}

function setGlyphCheck(ringId) {
  const el = document.getElementById(ringId);
  if (!el) return;

  el.classList.add('done');
  el.innerHTML =
    '<svg viewBox="0 0 32 32" fill="none">' +
    '<circle cx="16" cy="16" r="14" fill="var(--green-soft)" stroke="currentColor" stroke-width="1.5"/>' +
    '<path d="M10 16l4 4 8-8" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>' +
    '</svg>';
}

function onEnterInstall() {
  resetItem('ii-bep');
  resetItem('ii-plug');
  resetItem('ii-clean');
  resetItem('ii-verify');
  set('error-box', false);
  set('install-error-actions', false);
  set('ii-bep-bar', false);
  fill('ii-bep-fill', 0);
  reportGlobalError('');
  updateInstallSummary(installState);

  installer.startInstall({
    cupheadDir: cupheadPath,
    skipBepInEx: skipBepInExRepair,
  });
}

function handleInstallStep(step, status, progress, arch, message) {
  if (step === 'bepinex') {
    if (status === 'downloading') {
      setItem('ii-bep', 'active', message || (arch ? `Downloading ${arch}... ${progress}%` : `Downloading... ${progress}%`));
      set('ii-bep-bar', true);
      fill('ii-bep-fill', progress || 0);
    } else if (status === 'extracting') {
      setItem('ii-bep', 'active', message || 'Extracting...');
      set('ii-bep-bar', true);
      fill('ii-bep-fill', 100);
    } else if (status === 'done') {
      setItem('ii-bep', 'done', message || 'Installed');
      set('ii-bep-bar', false);
      setGlyphCheck('ii-bep-ring');
    } else if (status === 'skipped') {
      setItem('ii-bep', 'skipped', message || 'Already healthy');
      set('ii-bep-bar', false);
      setGlyphCheck('ii-bep-ring');
    }
  }

  if (step === 'plugin') {
    if (status === 'installing') {
      setItem('ii-plug', 'active', message || 'Refreshing bundled files...');
    } else if (status === 'done') {
      setItem('ii-plug', 'done', message || 'Bundled files refreshed');
      setGlyphCheck('ii-plug-ring');
    }
  }

  if (step === 'cleanup') {
    if (status === 'installing') {
      setItem('ii-clean', 'active', message || 'Cleaning legacy files...');
    } else if (status === 'done') {
      setItem('ii-clean', 'done', message || 'Legacy cleanup complete');
      setGlyphCheck('ii-clean-ring');
    } else if (status === 'skipped') {
      setItem('ii-clean', 'skipped', message || 'No cleanup needed');
      setGlyphCheck('ii-clean-ring');
    }
  }

  if (step === 'verify') {
    if (status === 'installing') {
      setItem('ii-verify', 'active', message || 'Verifying install...');
    } else if (status === 'done') {
      setItem('ii-verify', 'done', message || 'Verified');
      setGlyphCheck('ii-verify-ring');
    }
  }
}

document.getElementById('btn-close').addEventListener('click', () => installer.windowClose());
document.getElementById('btn-minimize').addEventListener('click', () => installer.windowMinimize());
document.getElementById('btn-start').addEventListener('click', () => goTo(1));

document.getElementById('btn-browse').addEventListener('click', async () => {
  const dir = await installer.browseFolder();
  if (!dir) return;
  setDetect('scanning');
  await applyPath(dir);
  if (!cupheadPath)
    setDetect('notfound');
});

document.getElementById('btn-change').addEventListener('click', async () => {
  const dir = await installer.browseFolder();
  if (!dir) return;
  await applyPath(dir);
});

document.getElementById('btn-open-folder').addEventListener('click', async () => {
  const result = await installer.openFolder(cupheadPath);
  setToolNote(result.ok ? 'ok' : 'error', result.message);
});

document.getElementById('btn-launch-steam').addEventListener('click', async () => {
  const result = await installer.launchSteam();
  setToolNote(result.ok ? 'ok' : 'warn', result.message);
});

document.getElementById('btn-verify').addEventListener('click', async () => {
  const result = await installer.verifyInstall(cupheadPath);
  if (cupheadPath)
    await applyPath(cupheadPath);

  const noteKind = result.ok
    ? ((result.checks && result.checks.hasLegacyFiles) ? 'warn' : 'ok')
    : 'warn';
  setToolNote(noteKind, result.message);
});

document.getElementById('btn-d-back').addEventListener('click', () => goTo(0, -1));
document.getElementById('btn-d-next').addEventListener('click', () => goTo(2));

installer.onInstallProgress((data) => {
  const { type, step, status, progress, arch, message } = data;

  if (type === 'step')
    handleInstallStep(step, status, progress, arch, message);

  if (type === 'done') {
    setText(
      'done-summary',
      data.summary || 'CupHeads is refreshed and ready to launch through Steam.'
    );
    setTimeout(() => goTo(3), 700);
  }

  if (type === 'error') {
    set('error-box', true);
    set('install-error-actions', true);
    setText('error-msg', data.message);
    setItem('ii-verify', 'error', 'Install stopped');
  }
});

// A failed install used to leave the window stuck on this page; offer a way
// back to the folder step and an in-place retry (e.g. after closing Cuphead).
document.getElementById('btn-i-back').addEventListener('click', () => goTo(1, -1));
document.getElementById('btn-i-retry').addEventListener('click', async () => {
  if (cupheadPath) {
    const state = await installer.checkInstallState(cupheadPath);
    installState = state;
    skipBepInExRepair = !!state.hasBepInExCore && !!state.hasDoorstop;
  }
  onEnterInstall();
});

document.getElementById('btn-done').addEventListener('click', () => installer.windowClose());
document.getElementById('btn-done-open-folder').addEventListener('click', async () => {
  const result = await installer.openFolder(cupheadPath);
  reportGlobalError(result.ok ? '' : result.message);
});
document.getElementById('btn-done-launch-steam').addEventListener('click', async () => {
  const launch = installer.launchCuphead || installer.launchSteam;
  const result = await launch();
  reportGlobalError(result.ok ? '' : result.message);
});

if (installer.getAppVersion) {
  installer.getAppVersion().then((version) => {
    setText('app-version', 'v' + version);
    setText('welcome-version', 'Installer v' + version);
  }).catch((err) => {
    console.warn('[app] Failed to fetch app version:', err);
  });
}
