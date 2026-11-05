#Requires -Version 7.0
param(
    [Parameter(Mandatory)] [string] $SourceDirectory
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourceDirectory).Path
if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'The source storage must not be a link.'
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'presenceplus-ge-controller'))) {
    throw 'Select the original controller storage directory, not the whole experiment folder.'
}
if (Get-ChildItem -LiteralPath $source -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'The source storage contains a link and cannot be imported.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$mutex = [Threading.Mutex]::new($false, "Local\PresencePlus-$($identity.User.Value)")
$locked = $false
$staging = $null
try {
    $locked = $mutex.WaitOne(0)
    if (-not $locked) { throw 'Exit PresencePlus before importing the saved pairing.' }
    $parent = Join-Path $env:LOCALAPPDATA 'PresencePlus'
    $target = Join-Path $parent 'Matter'
    if (Test-Path -LiteralPath $target) { throw 'Matter storage already exists; it was not overwritten. Use the existing pairing or back it up explicitly first.' }
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $staging = Join-Path $parent "Matter-import-$([Guid]::NewGuid().ToString('N'))"
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $identity.User, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    $directory = [IO.DirectoryInfo]::new($staging)
    [IO.FileSystemAclExtensions]::Create($directory, $security)
    foreach ($item in Get-ChildItem -LiteralPath $source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse
    }
    $directory.MoveTo($target)
    $staging = $null
    Write-Output 'Saved Matter pairing imported into private per-user app storage. Original storage was preserved.'
} finally {
    if ($staging -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse }
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
    $identity.Dispose()
}
