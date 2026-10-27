<#
.SYNOPSIS
  Build, test, package and deploy the Emby.Subremover plugin.

.EXAMPLE
  .\build.ps1                      # build + test + package (the default)
  .\build.ps1 test                 # just run the tests
  .\build.ps1 deploy -PluginsFolder '\\EMBYPC\C$\Emby\programdata\plugins'
#>
param(
    [ValidateSet('all', 'build', 'test', 'package', 'deploy')]
    [string]$Task = 'all',

    [string]$Configuration = 'Release',

    # Emby's plugins folder; deploy backs up the old DLL into a "subremover-backups" folder next to it first.
    [string]$PluginsFolder = $env:SUBREMOVER_PLUGINS_FOLDER
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$project   = 'src/Emby.Subremover/Emby.Subremover.csproj'
$outDir    = "src/Emby.Subremover/bin/$Configuration/netstandard2.0"
$dll       = Join-Path $outDir 'Emby.Subremover.dll'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
$version   = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

function Step($name) { Write-Host "`n=== $name ===" -ForegroundColor Cyan }

function Invoke-Build {
    Step "Build $version ($Configuration)"
    dotnet build $project -c $Configuration -nologo
    if ($LASTEXITCODE) { throw 'Build failed' }
}

function Invoke-Test {
    Step 'Test'
    dotnet test -c $Configuration
    if ($LASTEXITCODE) { throw 'Tests failed - not packaging or deploying' }
}

function Invoke-Package {
    Step 'Package'
    New-Item -ItemType Directory -Force $artifacts | Out-Null
    $commit = (git rev-parse --short HEAD 2>$null)
    $global:LASTEXITCODE = 0
    $name = "Emby.Subremover-$version" + $(if ($commit) { "-$commit" } else { '' })
    Copy-Item $dll (Join-Path $artifacts 'Emby.Subremover.dll') -Force
    Compress-Archive -Path $dll -DestinationPath (Join-Path $artifacts "$name.zip") -Force
    Write-Host "Created artifacts\$name.zip (older builds stay in the artifacts folder)"
}

function Invoke-Deploy {
    if (-not $PluginsFolder) { throw 'Give -PluginsFolder (or set SUBREMOVER_PLUGINS_FOLDER) to deploy' }
    if (-not (Test-Path $PluginsFolder)) { throw "Plugins folder not found: $PluginsFolder" }
    Step "Deploy to $PluginsFolder"

    $target = Join-Path $PluginsFolder 'Emby.Subremover.dll'
    if (Test-Path $target) {
        $backupDir = Join-Path (Split-Path $PluginsFolder -Parent) 'subremover-backups'
        New-Item -ItemType Directory -Force $backupDir | Out-Null
        $backup = Join-Path $backupDir ("Emby.Subremover-{0:yyyyMMdd-HHmmss}.dll" -f (Get-Date))
        Copy-Item $target $backup
        Write-Host "Backed up the installed plugin to $backup"
    }

    try {
        Copy-Item $dll $target -Force
    }
    catch {
        throw "Could not copy the DLL (Emby may have it locked). Stop the Emby server, run deploy again, then start Emby. $_"
    }
    Write-Host 'Deployed. Restart Emby Server to load the new version.' -ForegroundColor Green
}

switch ($Task) {
    'build'   { Invoke-Build }
    'test'    { Invoke-Test }
    'package' { Invoke-Build; Invoke-Package }
    'deploy'  { Invoke-Build; Invoke-Test; Invoke-Package; Invoke-Deploy }
    'all'     { Invoke-Build; Invoke-Test; Invoke-Package }
}
