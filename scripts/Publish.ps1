#Requires -Version 7.0
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [string] $NodeDirectory,
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version = '0.1.0',
    [switch] $FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = '24.22.2'
$tools = Join-Path $root '.tools'
New-Item -ItemType Directory -Path $tools -Force | Out-Null
if (-not $NodeDirectory) {
    $NodeDirectory = Join-Path $tools "node-v$version-win-x64"
    if (-not (Test-Path -LiteralPath (Join-Path $NodeDirectory 'node.exe'))) {
        $archiveName = "node-v$version-win-x64.zip"
        $archive = Join-Path $tools $archiveName
        $checksums = Join-Path $tools 'SHASUMS256.txt'
        & curl.exe --fail --silent --show-error --location "https://nodejs.org/dist/v$version/$archiveName" --output $archive
        if ($LASTEXITCODE -ne 0) { throw 'Node download failed.' }
        & curl.exe --fail --silent --show-error --location "https://nodejs.org/dist/v$version/SHASUMS256.txt" --output $checksums
        if ($LASTEXITCODE -ne 0) { throw 'Node checksum download failed.' }
        $line = Get-Content -LiteralPath $checksums | Where-Object { $_ -match "  $([regex]::Escape($archiveName))$" }
        if (@($line).Count -ne 1 -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $line.Substring(0, 64)) {
            throw 'Node SHA256 verification failed. Nothing was extracted or run.'
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $tools
        Remove-Item -LiteralPath $archive
    }
}
$NodeDirectory = (Resolve-Path -LiteralPath $NodeDirectory).Path
$node = Join-Path $NodeDirectory 'node.exe'
$npm = Join-Path $NodeDirectory 'node_modules\npm\bin\npm-cli.js'
if ((& $node --version) -ne "v$version") { throw "Use the verified Node $version Windows x64 distribution." }
$env:PATH = $NodeDirectory + ';' + $env:PATH
$source = Join-Path $root 'src\PresencePlus.Matter'
Push-Location $source
try {
    & $node $npm ls --depth=0 --json *> $null
    if ($LASTEXITCODE -ne 0) {
        & $node $npm ci --ignore-scripts --no-fund --no-audit --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Matter dependency restore failed.' }
    }
    & $node $npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Matter helper compilation failed.' }
} finally { Pop-Location }

$bundle = Join-Path $tools 'matter-bundle'
New-Item -ItemType Directory -Path (Join-Path $bundle 'dist') -Force | Out-Null
foreach ($file in @('package.json', 'package-lock.json')) {
    Copy-Item -LiteralPath (Join-Path $source $file) -Destination $bundle
}
$lockHash = (Get-FileHash -LiteralPath (Join-Path $bundle 'package-lock.json')).Hash
$stamp = Join-Path $bundle 'installed-lock.sha256'
if (-not (Test-Path -LiteralPath (Join-Path $bundle 'node_modules')) -or
    -not (Test-Path -LiteralPath $stamp) -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $lockHash) {
    Push-Location $bundle
    try {
        & $node $npm ci --omit=dev --ignore-scripts --no-fund --no-audit --quiet
        if ($LASTEXITCODE -ne 0) { throw 'Production Matter dependency restore failed.' }
        Set-Content -LiteralPath $stamp -Value $lockHash
    } finally { Pop-Location }
}
foreach ($file in @('main.js', 'control.js')) {
    Copy-Item -LiteralPath (Join-Path $source "dist\$file") -Destination (Join-Path $bundle 'dist')
}
Copy-Item -LiteralPath $node -Destination $bundle
Copy-Item -LiteralPath (Join-Path $NodeDirectory 'LICENSE') -Destination (Join-Path $bundle 'NODE-LICENSE.txt')
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
& dotnet publish (Join-Path $root 'src\PresencePlus') -c Release --runtime win-x64 `
    --self-contained (-not $FrameworkDependent.IsPresent).ToString().ToLowerInvariant() --nologo -v quiet `
    "-p:Version=$Version" "-p:MatterBundleDirectory=$bundle" -o $output
if ($LASTEXITCODE -ne 0) { throw 'PresencePlus publish failed.' }
Write-Output "Published complete Windows x64 app: $output"
