# Build Sts2UndoMod twice — once against the STS2 stable-branch install
# (v0.103.x), once against the beta-branch install (v0.104.0+) — and let
# each build's post-build copy land in its own mods/Sts2UndoMod/ folder.
#
# Why: stable and beta sts2.dll have a small API delta. A DLL built against
# one branch throws MissingMethodException on the other. To test both
# branches without toggling Steam beta opt-in (re-download, ~3GB), keep
# two parallel STS2 installs on disk and rebuild against each.
#
# Override paths via env vars before invoking:
#   $env:STS2_STABLE_PATH = "C:/sts2-stable"
#   $env:STS2_BETA_PATH   = "C:/sts2-beta"
#   ./scripts/build-both.ps1
#
# Each path must contain SlayTheSpire2.exe + data_sts2_windows_x86_64/sts2.dll.
# If only one path resolves, only that variant is built.

$ErrorActionPreference = 'Stop'

$stable = if ($env:STS2_STABLE_PATH) { $env:STS2_STABLE_PATH } else { 'C:/sts2-stable' }
$beta   = if ($env:STS2_BETA_PATH)   { $env:STS2_BETA_PATH   } else { 'C:/sts2-beta'   }

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj   = Join-Path $repoRoot 'Sts2UndoMod.csproj'
$config   = if ($env:UNDOMOD_CONFIG) { $env:UNDOMOD_CONFIG } else { 'Debug' }

function Test-Sts2Install {
    param([string]$Path)
    $dll = Join-Path $Path 'data_sts2_windows_x86_64/sts2.dll'
    return (Test-Path $Path -PathType Container) -and (Test-Path $dll -PathType Leaf)
}

function Get-Sts2Version {
    param([string]$Path)
    $info = Join-Path $Path 'release_info.json'
    if (-not (Test-Path $info)) { return '?' }
    try { (Get-Content $info -Raw | ConvertFrom-Json).version } catch { '?' }
}

function Invoke-Variant {
    param(
        [string]$Label,
        [string]$Sts2Path
    )
    if (-not (Test-Sts2Install $Sts2Path)) {
        Write-Host "[$Label] SKIP — sts2.dll not found under '$Sts2Path'" -ForegroundColor Yellow
        return $false
    }
    $ver = Get-Sts2Version $Sts2Path
    Write-Host "[$Label] STS2 $ver @ $Sts2Path" -ForegroundColor Cyan

    # Forward Sts2Path as MSBuild prop — Sts2PathDiscovery.props consumes it
    # before its auto-discovery fallbacks fire, so this fully overrides where
    # sts2.dll is referenced from AND where the post-build Copy task lands.
    & dotnet build $csproj -c $config -nologo --verbosity quiet "/p:Sts2Path=$Sts2Path"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[$Label] FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        return $false
    }
    $modDll = Join-Path $Sts2Path 'mods/Sts2UndoMod/Sts2UndoMod.dll'
    if (Test-Path $modDll) {
        $size = (Get-Item $modDll).Length
        Write-Host "[$Label] OK — $modDll ($size bytes)" -ForegroundColor Green
    } else {
        Write-Host "[$Label] built but DLL not copied to expected mods path" -ForegroundColor Yellow
    }
    return $true
}

$any = $false
if (Invoke-Variant 'stable' $stable) { $any = $true }
if (Invoke-Variant 'beta'   $beta)   { $any = $true }

if (-not $any) {
    Write-Host @"

Neither install path resolved. Configure parallel installs first:

  1. Copy your current Steam STS2 folder somewhere outside Steam, e.g.
       Copy-Item 'C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2' 'C:/sts2-stable' -Recurse
  2. In Steam: STS2 -> Properties -> Betas -> opt into the beta branch and
     let Steam download v0.104.x into the same Steam folder.
  3. Copy that beta install:
       Copy-Item 'C:/Program Files (x86)/Steam/steamapps/common/Slay the Spire 2' 'C:/sts2-beta' -Recurse
  4. Launch each via its own launch_d3d12.bat (Steam must be running).
  5. Re-run this script.

Override paths via `$env:STS2_STABLE_PATH` / `$env:STS2_BETA_PATH` if you
place them elsewhere.
"@ -ForegroundColor Yellow
    exit 1
}
