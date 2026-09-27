<#
.SYNOPSIS
    Collects the game and BepInEx assemblies the build references into a zip
    that can be copied to the Forgejo runner and used as the CI image's build
    context. See docs/ci.md.

.EXAMPLE
    pwsh ci/collect-refs.ps1
    pwsh ci/collect-refs.ps1 -GameDir 'D:\Games\Nuclear Option' -Output C:\temp\refs.zip
#>
[CmdletBinding()]
param(
    [string] $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option',
    [string] $Output  = (Join-Path $PSScriptRoot 'custommaps-ci-refs.zip')
)

$ErrorActionPreference = 'Stop'

# Must stay in step with the <Reference> items in CustomMaps.csproj.
#
# Note this list is a superset of the one the NOAutoGCAS image carries:
# UnityEngine.AssetBundleModule, UnityEngine.ImageConversionModule, UniTask,
# UnityEngine.UI/UIModule and Unity.TextMeshPro are new. The shared gcas-ci image
# will not build this project until it is rebuilt from this zip.
$managed = @(
    'Assembly-CSharp.dll'
    'Assembly-CSharp-firstpass.dll'
    'UnityEngine.dll'
    'UnityEngine.CoreModule.dll'
    'UnityEngine.AssetBundleModule.dll'
    'UnityEngine.PhysicsModule.dll'
    'UnityEngine.ImageConversionModule.dll'
    'UnityEngine.UI.dll'
    'UnityEngine.UIModule.dll'
    'Unity.TextMeshPro.dll'
    'UniTask.dll'
    'Mirage.dll'
)
$bepinex = @(
    'BepInEx.dll'
    '0Harmony.dll'
)

$managedSrc = Join-Path $GameDir 'NuclearOption_Data\Managed'
$bepinexSrc = Join-Path $GameDir 'BepInEx\core'

foreach ($dir in @($managedSrc, $bepinexSrc)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        throw "Not found: $dir. Pass -GameDir pointing at the Nuclear Option install."
    }
}

$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("custommaps-refs-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'refs\Managed') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stage 'refs\BepInEx') -Force | Out-Null

try {
    $missing = @()
    foreach ($name in $managed) {
        $src = Join-Path $managedSrc $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $stage 'refs\Managed')
        } else { $missing += $src }
    }
    foreach ($name in $bepinex) {
        $src = Join-Path $bepinexSrc $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $stage 'refs\BepInEx')
        } else { $missing += $src }
    }

    if ($missing.Count -gt 0) {
        throw "Missing assemblies:`n  " + ($missing -join "`n  ")
    }

    # Also record the game build the references came from. MapManifest carries the
    # same string, so a bundle built against a different game version can be warned
    # about rather than silently deserializing MonoBehaviour fields to defaults.
    $buildHash = Join-Path $GameDir 'build-hash.txt'
    if (Test-Path -LiteralPath $buildHash) {
        Copy-Item -LiteralPath $buildHash -Destination (Join-Path $stage 'refs')
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Dockerfile') -Destination $stage

    if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $Output

    $count = $managed.Count + $bepinex.Count
    $mb    = [math]::Round((Get-Item -LiteralPath $Output).Length / 1MB, 1)
    Write-Host "Wrote $Output ($count assemblies, $mb MB)"
    Write-Host "Copy it to the runner, then: unzip -o <zip> -d ~/custommaps-ci && cd ~/custommaps-ci && podman build -t localhost/gcas-ci:9.0 ."
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
