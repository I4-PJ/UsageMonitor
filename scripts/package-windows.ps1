[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectDirectory 'UsageMonitor.csproj'
if (!$Version) {
    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $Version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw 'Version must have the form 0.1.0 or 0.1.0-rc.1.'
}

$artifactsDirectory = [IO.Path]::GetFullPath((Join-Path $projectDirectory 'artifacts'))
$publishDirectory = [IO.Path]::GetFullPath((Join-Path $artifactsDirectory "publish/$Runtime"))
$buildDirectory = Join-Path $artifactsDirectory 'build'
$archivePath = Join-Path $artifactsDirectory "UsageMonitor-$Version-$Runtime.zip"
$artifactPrefix = $artifactsDirectory + [IO.Path]::DirectorySeparatorChar
if (!$publishDirectory.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish directory must stay within this repository''s artifacts directory.'
}
if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}

dotnet publish $projectPath --configuration Release --runtime $Runtime --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false "-p:Version=$Version" `
    --artifacts-path $buildDirectory --output $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }

python (Join-Path $PSScriptRoot 'collect-licenses.py') `
    --assets (Join-Path $buildDirectory 'obj/UsageMonitor/project.assets.json') `
    --destination (Join-Path $publishDirectory 'ThirdPartyLicenses') --runtime $Runtime
if ($LASTEXITCODE -ne 0) { throw 'Dependency license collection failed.' }

Copy-Item -LiteralPath (Join-Path $projectDirectory 'Packaging/INSTALL-Windows.txt') -Destination (Join-Path $publishDirectory 'INSTALL.txt')
foreach ($required in @('UsageMonitor.exe', 'UsageMonitor.dll', 'LICENSE', 'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt', 'ThirdPartyLicenses/README.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishDirectory $required))) {
        throw "Missing release file: $required"
    }
}
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
Write-Output "Created $archivePath"
