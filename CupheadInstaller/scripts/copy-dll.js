'use strict';
// Copies the freshly-built CupheadOnline.dll into installer/assets/
// Run automatically before "npm run dist" via the "predist" hook.
//
// build.ps1 sets CUPHEADS_MOD_DLL to the exact DLL it just built. Without it
// (manual `npm run dist`), the most recently built candidate wins, so a stale
// Release build can never shadow a fresh Debug build or vice versa.

const fs   = require('fs');
const path = require('path');

const DEST = path.resolve(__dirname, '..', 'assets', 'CupheadOnline.dll');
const modRoot = path.resolve(__dirname, '..', '..', 'CupheadOnline', 'bin');
const candidates = [
  path.join(modRoot, 'Release', 'net35', 'CupheadOnline.dll'),
  path.join(modRoot, 'Debug', 'net35', 'CupheadOnline.dll'),
];

function pickSource() {
  const fromEnv = process.env.CUPHEADS_MOD_DLL;
  if (fromEnv) {
    if (fs.existsSync(fromEnv))
      return fromEnv;
    console.error('\n  ERROR: CUPHEADS_MOD_DLL points to a missing file: ' + fromEnv + '\n');
    process.exit(1);
  }

  const existing = candidates
    .filter((p) => fs.existsSync(p))
    .map((p) => ({ path: p, mtime: fs.statSync(p).mtimeMs }))
    .sort((a, b) => b.mtime - a.mtime);

  return existing.length ? existing[0].path : null;
}

const SRC = pickSource();
if (!SRC) {
  console.error('\n  ERROR: CupheadOnline.dll not found. Checked:');
  candidates.forEach((p) => console.error('    ' + p));
  console.error('  Build the mod first:');
  console.error('    dotnet build CupheadOnline/CupheadOnline.csproj -c Release\n');
  process.exit(1);
}

fs.mkdirSync(path.dirname(DEST), { recursive: true });
fs.copyFileSync(SRC, DEST);
console.log('  Copied ' + path.relative(path.resolve(__dirname, '..', '..'), SRC) + ' -> assets/CupheadOnline.dll');
