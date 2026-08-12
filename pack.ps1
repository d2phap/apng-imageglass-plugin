<#
.SYNOPSIS
    Publishes the APNG codec plugin and packs it as apng-codec_<version>_<arch>.igplugin.zip.

.DESCRIPTION
    Produces the same layout as the release workflow: a single "Plugin_ApngCodec" folder
    holding the native library and its manifest. ImageGlass accepts that either through
    Settings > Plugins > Add or as a manual copy into the _plugins directory.

.PARAMETER Rid
    Which runtimes to build. Defaults to the targets the current OS can produce: both
    Windows architectures on Windows, linux-x64 on Linux, osx-arm64 on macOS. Native AOT
    cannot cross-compile between operating systems, so a foreign RID is rejected up front.

.PARAMETER Deploy
    Also copy the staged folder into %LOCALAPPDATA%\ImageGlass\_plugins for local testing.
    Requires exactly one -Rid. Close ImageGlass first: a loaded plugin's library is locked.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'osx-arm64')]
    [string[]] $Rid,

    [switch] $Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$pluginFolder = 'Plugin_ApngCodec'

# $IsLinux / $IsMacOS only exist on PowerShell 6+; on Windows PowerShell 5.1 they are $null,
# which lands on the Windows branch — the only OS that ships 5.1 anyway.
$hostOs = if ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { 'win' }
if (-not $Rid) {
    $Rid = switch ($hostOs) {
        'linux' { @('linux-x64') }
        'osx'   { @('osx-arm64') }
        default { @('win-x64', 'win-arm64') }
    }
}

foreach ($runtime in $Rid) {
    if (-not $runtime.StartsWith($hostOs)) {
        throw "cannot build $runtime here: Native AOT does not cross-compile between operating systems - run this on the target OS."
    }
}

# Windows only: ILCompiler shells out to vcvarsall.bat, which calls vswhere.exe by bare name.
# Outside a Developer Command Prompt that failure is captured into the linker path and the
# native link breaks with a confusing error, so make sure the Installer directory is reachable.
# Linux and macOS link through clang, which needs nothing from here.
if ($hostOs -eq 'win') {
    $installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
    if ((Test-Path $installer) -and ($env:PATH -notlike "*$installer*")) {
        $env:PATH = "$installer;$env:PATH"
    }
}

if ($Deploy -and $Rid.Count -ne 1) {
    throw '-Deploy needs exactly one -Rid.'
}

# The manifest is the version ImageGlass shows in Settings > Plugins, so name the package after
# it rather than after the assembly version — those two are kept in step, and this is the one a
# user can check without unzipping.
$manifestPath = Join-Path $root 'source/igplugin.json'
$version = (Get-Content $manifestPath -Raw | ConvertFrom-Json).version
if ([string]::IsNullOrWhiteSpace($version)) { throw "no version in $manifestPath" }

foreach ($runtime in $Rid) {
    $platform = if ($runtime.EndsWith('-arm64')) { 'ARM64' } else { 'x64' }

    # The release assets say "mac", not the RID's "osx"; keep the local packages identical.
    $arch = if ($runtime -eq 'osx-arm64') { 'mac-arm64' } else { $runtime }

    # Native AOT names the shared library per platform, and the csproj rewrites the published
    # manifest's "executable" to match — so the file to verify differs by target too.
    $libName = switch -Wildcard ($runtime) {
        'linux-*' { 'ApngCodec.so' }
        'osx-*'   { 'ApngCodec.dylib' }
        default   { 'ApngCodec.dll' }
    }

    $publishDir = Join-Path $root "dist/$runtime"
    $staged = Join-Path $root "dist/staging/$runtime/$pluginFolder"

    Write-Host "`n=== publishing $runtime ===" -ForegroundColor Cyan
    dotnet publish (Join-Path $root 'source/ApngCodec.csproj') `
        --configuration Release --runtime $runtime -p:Platform=$platform --output $publishDir
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $runtime" }

    # Ship only what the host loads: no debug symbols (.pdb on Windows, .dbg on Linux, .dSYM
    # on macOS — a directory, so -File skips it anyway) and no .xml IntelliSense docs.
    # libSkiaSharp rides along from the ImageGlass.SDK package reference; this plugin does call
    # Skia, but ImageGlass ships its own copy next to the executable and the plugin resolves it
    # from there, so a bundled second one is ~12 MB of dead weight.
    if (Test-Path $staged) { Remove-Item $staged -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    Get-ChildItem $publishDir -File |
        Where-Object { $_.Extension -notin '.pdb', '.dbg', '.dSYM', '.xml' } |
        Where-Object { $_.Name -notlike 'libSkiaSharp*' } |
        Copy-Item -Destination $staged

    # A package missing either file installs cleanly and then does nothing, so fail loudly here.
    foreach ($required in $libName, 'igplugin.json') {
        if (-not (Test-Path (Join-Path $staged $required))) {
            throw "package for $runtime is missing $required"
        }
    }

    $zip = Join-Path $root "dist/apng-codec_${version}_$arch.igplugin.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $staged -DestinationPath $zip

    $size = [math]::Round((Get-Item $zip).Length / 1KB)
    Write-Host "packed $zip ($size KB)" -ForegroundColor Green
    Get-ChildItem $staged | Select-Object Name, Length | Format-Table -AutoSize

    if ($Deploy) {
        if ($hostOs -ne 'win') { throw '-Deploy only knows the Windows _plugins location.' }
        $target = Join-Path $env:LOCALAPPDATA "ImageGlass/_plugins/$pluginFolder"

        # A loaded plugin's library is locked, so move the old folder aside instead of deleting
        # in place: a half-failed recursive delete can strip the manifest and leave the plugin in
        # a state the host cannot even describe.
        if (Test-Path $target) {
            $retired = "$target.old-$(Get-Random)"
            Move-Item $target $retired
            try { Remove-Item $retired -Recurse -Force } catch {
                Write-Warning "left $retired behind (ImageGlass may still have it open)"
            }
        }

        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item "$staged/*" $target -Recurse
        Write-Host "deployed to $target" -ForegroundColor Green
    }
}
