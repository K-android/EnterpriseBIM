<#
.SYNOPSIS
    EnterpriseBIM Automated Setup & Uninstaller Script for Rhino 8 & Revit (2024/2025)
.DESCRIPTION
    Supports installing/uninstalling Rhino Harvester (.rhp + HKLM registry),
    Revit Importer (.dll + .addin manifest), or both in silent/interactive enterprise modes.
#>
[CmdletBinding()]
param(
    [ValidateSet("All", "Rhino", "Revit")]
    [string]$Target = "All",

    [string]$RevitVersion = "2025",

    [string]$RhinoVersion = "8.0",

    [string]$BinarySourceDir = (Join-Path $PSScriptRoot "..\artifacts\publish"),

    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

$RhinoPluginGuid = "9C4F8D21-7A1E-4B63-8E51-3D29C84A10F2"
$ProgramData = [Environment]::GetFolderPath("CommonApplicationData")
$ExchangeStagingDir = Join-Path $ProgramData "EnterpriseBIM"
$ExchangeLogsDir = Join-Path $ExchangeStagingDir "Logs"
$RevitAddinsDir = Join-Path $ProgramData "Autodesk\Revit\Addins\$RevitVersion"
$RevitBinDir = Join-Path $RevitAddinsDir "EnterpriseBIM"
$RhinoPluginDir = Join-Path $ProgramData "McNeel\Rhinoceros\$RhinoVersion\Plug-ins\EnterpriseBIM ($RhinoPluginGuid)"
$RhinoRegKeyPath = "HKLM:\SOFTWARE\McNeel\Rhinoceros\$RhinoVersion\Plug-ins\$RhinoPluginGuid"

function Write-SetupLog([string]$Message) {
    $stamp = [DateTimeOffset]::UtcNow.ToString("O")
    Write-Host "[$stamp] [EnterpriseBIM.Setup] $Message"
}

if ($Uninstall) {
    if ($Target -in @("All", "Revit")) {
        $addinFile = Join-Path $RevitAddinsDir "EnterpriseBIM.addin"
        if (Test-Path $addinFile) { Remove-Item $addinFile -Force }
        if (Test-Path $RevitBinDir) { Remove-Item $RevitBinDir -Recurse -Force }
        Write-SetupLog "Uninstalled EnterpriseBIM Revit $RevitVersion Add-in."
    }

    if ($Target -in @("All", "Rhino")) {
        if (Test-Path $RhinoRegKeyPath) { Remove-Item $RhinoRegKeyPath -Recurse -Force }
        if (Test-Path $RhinoPluginDir) { Remove-Item $RhinoPluginDir -Recurse -Force }
        Write-SetupLog "Uninstalled EnterpriseBIM Rhino $RhinoVersion Plug-in."
    }
    exit 0
}

New-Item -ItemType Directory -Path $ExchangeStagingDir -Force | Out-Null
New-Item -ItemType Directory -Path $ExchangeLogsDir -Force | Out-Null
Write-SetupLog "Initialized staging directory: $ExchangeStagingDir"

if ($Target -in @("All", "Rhino")) {
    New-Item -ItemType Directory -Path $RhinoPluginDir -Force | Out-Null

    $coreDll = Join-Path $BinarySourceDir "EnterpriseBIM.Core.dll"
    $rhinoRhp = Join-Path $BinarySourceDir "EnterpriseBIM.Rhino.rhp"

    if (Test-Path $coreDll) { Copy-Item $coreDll -Destination $RhinoPluginDir -Force }
    if (Test-Path $rhinoRhp) { Copy-Item $rhinoRhp -Destination $RhinoPluginDir -Force }

    if (-not (Test-Path $RhinoRegKeyPath)) {
        New-Item -Path $RhinoRegKeyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $RhinoRegKeyPath -Name "Name" -Value "EnterpriseBIM.Rhino" -Type String
    Set-ItemProperty -Path $RhinoRegKeyPath -Name "FileName" -Value (Join-Path $RhinoPluginDir "EnterpriseBIM.Rhino.rhp") -Type String
    Set-ItemProperty -Path $RhinoRegKeyPath -Name "Organization" -Value "Enterprise AEC Interoperability Division" -Type String

    Write-SetupLog "Installed EnterpriseBIM Rhino $RhinoVersion Harvester to '$RhinoPluginDir'."
}

if ($Target -in @("All", "Revit")) {
    New-Item -ItemType Directory -Path $RevitAddinsDir -Force | Out-Null
    New-Item -ItemType Directory -Path $RevitBinDir -Force | Out-Null

    $addinSource = Join-Path $PSScriptRoot "EnterpriseBIM.addin"
    $coreDll = Join-Path $BinarySourceDir "EnterpriseBIM.Core.dll"
    $revitDll = Join-Path $BinarySourceDir "EnterpriseBIM.Revit.dll"

    if (Test-Path $addinSource) { Copy-Item $addinSource -Destination (Join-Path $RevitAddinsDir "EnterpriseBIM.addin") -Force }
    if (Test-Path $coreDll) { Copy-Item $coreDll -Destination $RevitBinDir -Force }
    if (Test-Path $revitDll) { Copy-Item $revitDll -Destination $RevitBinDir -Force }

    Write-SetupLog "Installed EnterpriseBIM Revit $RevitVersion Importer to '$RevitBinDir'."
}

Write-SetupLog "EnterpriseBIM setup completed successfully (Target=$Target)."
