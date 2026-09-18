<#
.SYNOPSIS
    Builds HS Reconnector (HsReconnector.exe), the standalone Hearthstone reconnect tool.

.DESCRIPTION
    Compiles the app, checks the binary, and copies it to .\artifacts. With -Package it also
    creates the release zip and SHA256SUMS.txt.

.PARAMETER Package
    Also create the release zip and SHA256SUMS.txt in .\artifacts.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1

.EXAMPLE
    .\build.ps1 -Package
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'src\HsReconnector\HsReconnector.csproj'
$artifacts = Join-Path $root 'artifacts'

# --- Build -------------------------------------------------------------------
dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$exe = Join-Path $root "src\HsReconnector\bin\$Configuration\HsReconnector.exe"
$version = [Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3)

# --- Check the binary before it goes anywhere --------------------------------
# 1. It must still ask for administrator rights (SetTcpEntry and the hosts file need them).
# 2. No absolute path of the build machine may be embedded (PathMap in Directory.Build.props).
function Test-BinaryContains([byte[]]$Bytes, [string]$Text) {
    $ascii = [Text.Encoding]::ASCII.GetString($Bytes)
    if ($ascii.IndexOf($Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    foreach ($offset in 0, 1) {
        $utf16 = [Text.Encoding]::Unicode.GetString($Bytes, $offset, $Bytes.Length - $offset)
        if ($utf16.IndexOf($Text, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
    }
    return $false
}

$bytes = [IO.File]::ReadAllBytes($exe)
if (-not (Test-BinaryContains $bytes 'level="requireAdministrator"')) {
    throw 'The exe has no requireAdministrator manifest; it would start without the rights it needs.'
}
foreach ($local in @($root.TrimEnd('\'), $env:USERPROFILE) | Where-Object { $_ }) {
    if (Test-BinaryContains $bytes $local) {
        throw "The exe contains the local path '$local'. Check PathMap in Directory.Build.props."
    }
}
Write-Host "Built HsReconnector $version - binary checks passed." -ForegroundColor Green

# --- Collect / package -------------------------------------------------------
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }
New-Item -ItemType Directory -Force $artifacts | Out-Null
Copy-Item $exe $artifacts

if ($Package) {
    $staging = Join-Path $artifacts 'staging\HsReconnector'
    New-Item -ItemType Directory -Force $staging | Out-Null
    Copy-Item $exe $staging
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE.txt')

    $zip = Join-Path $artifacts "HsReconnector-v$version.zip"
    Compress-Archive -Path $staging -DestinationPath $zip
    Remove-Item (Join-Path $artifacts 'staging') -Recurse -Force

    Get-ChildItem $artifacts -File |
        Sort-Object Name |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name } |
        Set-Content (Join-Path $artifacts 'SHA256SUMS.txt') -Encoding ascii
    Write-Host "Packaged: $zip" -ForegroundColor Green
}

Write-Host "Output: $artifacts\HsReconnector.exe (asks for administrator rights on start)"
