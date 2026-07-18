<#
.SYNOPSIS
    CupHeads hook audit — verifies every Harmony patch target and reflected
    member name in the mod source against the decompiled game code.

.DESCRIPTION
    Scans CupheadOnline/**/*.cs for:
      [HarmonyPatch(typeof(Class), "Method")] and nameof(...) variants
    and checks each against Assembly-CSharp/<Class>.vb.

    A game update or a typo that breaks a hook fails this script, instead of
    failing silently at runtime. Run standalone or from build.ps1.

.USAGE
    powershell -ExecutionPolicy Bypass -File tools\audit-hooks.ps1
    Exit code 0 = all hooks resolve; 1 = at least one missing target.
#>

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$sourceDir  = Join-Path $repoRoot "CupheadOnline"
$gameDir    = Join-Path $repoRoot "Assembly-CSharp"

if (-not (Test-Path $sourceDir)) { Write-Error "Mod source not found: $sourceDir" }
if (-not (Test-Path $gameDir))   { Write-Error "Decompiled game source not found: $gameDir" }

# Classes the mod defines itself or that live outside Assembly-CSharp (Rewired,
# Unity). Their patches resolve via TargetMethod() or other assemblies.
$skipClasses = @("Rand") | Where-Object { $false }  # placeholder; Rand IS in Assembly-CSharp

$patchRegex = '\[HarmonyPatch\(typeof\((?<class>[A-Za-z0-9_.]+)\)\s*,\s*(?:nameof\((?:[A-Za-z0-9_]+\.)?(?<nameof>[A-Za-z0-9_]+)\)|"(?<name>[A-Za-z_0-9]+)")'

$targets = @{}
Get-ChildItem $sourceDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $file = $_.FullName
        $content = Get-Content $file -Raw
        foreach ($m in [regex]::Matches($content, $patchRegex)) {
            $cls = $m.Groups["class"].Value
            $method = if ($m.Groups["nameof"].Success) { $m.Groups["nameof"].Value } else { $m.Groups["name"].Value }
            $key = "$cls.$method"
            if (-not $targets.ContainsKey($key)) {
                $targets[$key] = [pscustomobject]@{ Class = $cls; Method = $method; File = $file }
            }
        }
    }

$failures = @()
$checked = 0

foreach ($entry in $targets.Values | Sort-Object { $_.Class + "." + $_.Method }) {
    $baseClass = ($entry.Class -split '\.')[-1]

    # Nested classes ("Outer.Inner") live inside Outer.vb
    $vbFile = Join-Path $gameDir "$baseClass.vb"
    if (-not (Test-Path $vbFile) -and $entry.Class.Contains(".")) {
        $outer = ($entry.Class -split '\.')[0]
        $vbFile = Join-Path $gameDir "$outer.vb"
    }

    if (-not (Test-Path $vbFile)) {
        Write-Output ("SKIP    {0}.{1} (no decompiled file - non-game type?)" -f $entry.Class, $entry.Method)
        continue
    }

    $checked++
    $vb = Get-Content $vbFile -Raw
    $method = $entry.Method

    $found = $false
    if ($method.StartsWith("get_") -or $method.StartsWith("set_")) {
        $propName = $method.Substring(4)
        $found = $vb -match "Property\s+$propName\b"
    }
    else {
        $found = ($vb -match "(Sub|Function)\s+$method\b") -or ($vb -match "Property\s+$method\b")
    }

    if ($found) {
        Write-Output ("OK      {0}.{1}" -f $entry.Class, $entry.Method)
    }
    else {
        $failures += ("MISSING {0}.{1}  (declared in {2})" -f $entry.Class, $entry.Method, $entry.File)
    }
}

# ── Reflected member names: GetField/GetMethod/GetProperty/Traverse on game types ──
# These are advisory (the owning type is often dynamic), so report but don't fail.
$reflectRegex = '(GetField|GetMethod|GetProperty)\("(?<member>[A-Za-z_0-9<>]+)"'
$reflectNames = @{}
Get-ChildItem $sourceDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        foreach ($m in [regex]::Matches((Get-Content $_.FullName -Raw), $reflectRegex)) {
            $reflectNames[$m.Groups["member"].Value] = $true
        }
    }

$gameBlob = ""
$advisory = @()
if ($reflectNames.Count -gt 0) {
    Write-Output ""
    Write-Output "Reflected member names (advisory scan across all game code):"
    $gameFiles = Get-ChildItem $gameDir -Filter *.vb
    $names = @($reflectNames.Keys)
    $foundNames = @{}
    foreach ($f in $gameFiles) {
        $blob = Get-Content $f.FullName -Raw
        foreach ($n in $names) {
            if (-not $foundNames.ContainsKey($n)) {
                $probe = $n -replace '<|>', ''  # backing-field names like <stoneTime>k__BackingField
                if ($blob -match [regex]::Escape($probe)) { $foundNames[$n] = $true }
            }
        }
    }
    foreach ($n in $names | Sort-Object) {
        if ($foundNames.ContainsKey($n)) {
            Write-Output ("  ok      {0}" -f $n)
        } else {
            $advisory += $n
            Write-Output ("  warn    {0} (not found anywhere in game source)" -f $n)
        }
    }
}

Write-Output ""
Write-Output ("Hook audit: {0} targets checked, {1} missing, {2} advisory warnings." -f $checked, $failures.Count, $advisory.Count)

if ($failures.Count -gt 0) {
    Write-Output ""
    $failures | ForEach-Object { Write-Output $_ }
    Write-Output ""
    Write-Error "Hook audit FAILED - fix the missing targets before shipping."
    exit 1
}

exit 0
