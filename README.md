# EnterpriseBIM — Offline Local Rhino-to-Revit Data Transfer Pipeline

Enterprise-grade, asynchronous local Rhino-to-Revit interoperability pipeline operating entirely offline via `%ProgramData%\EnterpriseBIM\exchange.json` without Rhino.Inside.Revit (RIR).

## Architecture

- **`EnterpriseBIM.Core` (.NET 8)**: Immutable data transfer contract (`ExchangePackage`, `BimElementPayload`, `Point3D`, `Polyline3D`, `Mesh3D`, `TransformMatrix4x4`), SHA-256 deterministic payload integrity hashing, and cross-process mutex-synchronized atomic file stream serialization (`ExchangeStreamSerializer`).
- **`EnterpriseBIM.Rhino` (.NET 8 / `.rhp`)**: Native Rhino 8 plug-in (`EnterpriseBIM_Harvest` command) that filters selected `Curve`, `Brep`, and `Extrusion` objects, extracts layer hierarchies, UserText dictionaries, and world transformation matrices, and writes to `%ProgramData%\EnterpriseBIM\exchange.json`.
- **`EnterpriseBIM.Revit` (.NET 8 / `.dll`)**: Resilient transactional Revit 2025 importer (`EnterpriseBimImportCommand`) with automated modal suppression (`EnterpriseFailurePreProcessor`), isolated `TransactionGroup` + per-element `SubTransaction` rollback protection, parametric `Wall` / `Floor` generation, and manifold `DirectShape` fallback.
- **Deployment & Installers (`deploy/` & `installers/`)**:
  - **WiX v4 MSI Packages**:
    - `deploy/Package.wxs` (`EnterpriseBIM.Installer.wixproj`) — Unified Rhino 8 + Revit 2025 MSI
    - `deploy/RhinoInstaller.wxs` (`EnterpriseBIM.Rhino.Installer.wixproj`) — Standalone Rhino 8 Harvester MSI
    - `deploy/RevitInstaller.wxs` (`EnterpriseBIM.Revit.Installer.wixproj`) — Standalone Revit 2025 Importer MSI
  - **Standalone Windows Setup Executables (`installers/`)**:
    - `installers/EnterpriseBIM-Unified-Setup.exe` (Interactive GUI & Silent `/S` CLI installer for both Rhino & Revit)
    - `installers/EnterpriseBIM-Rhino-Setup.exe` (Dedicated Rhino Harvester setup installer)
    - `installers/EnterpriseBIM-Revit-Setup.exe` (Dedicated Revit Importer setup installer)
  - **PowerShell Enterprise Deployment Script**:
    - `deploy/Install-EnterpriseBIM.ps1` (`-Target All|Rhino|Revit`, `-Uninstall`)

## Build & Silent Deployment Commands

```powershell
# Full MSBuild Orchestration (.NET 8 + Authenticode Signing + WiX v4 MSI)
dotnet msbuild .\EnterpriseBIM.build.proj /t:BuildAndPackage /p:Configuration=Release

# Silent Setup Executables
.\installers\EnterpriseBIM-Unified-Setup.exe /S
.\installers\EnterpriseBIM-Rhino-Setup.exe /S /rhino
.\installers\EnterpriseBIM-Revit-Setup.exe /S /revit

# Silent MSI Installation (SCCM / Intune)
msiexec.exe /i "EnterpriseBIM.EnterpriseInstaller.msi" /qn /norestart ALLUSERS=1
```
