#Requires -Version 5.0
<#
.SYNOPSIS
    Script build và cài đặt SheetNumberingRevit add-in cho Revit 2025

.DESCRIPTION
    Script này giúp build và cài đặt add-in vào Revit 2025

.PARAMETER Action
    Action cần thực hiện: Build, Install, All

.PARAMETER RevitApiPath
    Đường dẫn đến thư mục chứa Revit API DLLs

.EXAMPLE
    .\BuildAndInstall.ps1 -Action All

.EXAMPLE
    .\BuildAndInstall.ps1 -Action Build

.EXAMPLE
    .\BuildAndInstall.ps1 -Action Install
#>

param(
    [ValidateSet("Build", "Install", "All")]
    [string]$Action = "All",

    [Parameter(HelpMessage = "Đường dẫn đến thư mục chứa Revit API DLLs")]
    [string]$RevitApiPath = "C:\Program Files\Autodesk\Revit 2025\"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "SheetNumberingRevit - Build & Install" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Resolve lib path
$LibPath = Join-Path $ProjectRoot "lib\Revit2025"

# Validate Revit API Path (either in Program Files or in lib folder)
$RevitApiDllLocal = Join-Path $LibPath "RevitAPI.dll"
$RevitApiDllGlobal = Join-Path $RevitApiPath "RevitAPI.dll"
$RevitApiUiDllLocal = Join-Path $LibPath "RevitAPIUI.dll"
$RevitApiUiDllGlobal = Join-Path $RevitApiPath "RevitAPIUI.dll"

# Prefer local lib/Revit2025 if DLLs exist there, otherwise use provided path
$UseLocalLib = (Test-Path $RevitApiDllLocal) -and (Test-Path $RevitApiUiDllLocal)
if ($UseLocalLib) {
    Write-Host "[INFO] Using local Revit API DLLs from: $LibPath" -ForegroundColor Cyan
} elseif ((Test-Path $RevitApiDllGlobal) -and (Test-Path $RevitApiUiDllGlobal)) {
    Write-Host "[INFO] Using Revit API DLLs from: $RevitApiPath" -ForegroundColor Cyan
    # Copy to local lib for future use
    if (-not (Test-Path $LibPath)) {
        New-Item -ItemType Directory -Path $LibPath -Force | Out-Null
    }
    Copy-Item $RevitApiDllGlobal -Destination $LibPath -Force
    Copy-Item $RevitApiUiDllGlobal -Destination $LibPath -Force
    Write-Host "[OK] Copied Revit API DLLs to local lib folder" -ForegroundColor Green
} else {
    Write-Error "Không tìm thấy Revit API DLLs. Vui lòng đặt RevitAPI.dll và RevitAPIUI.dll vào thư mục '$LibPath' hoặc kiểm tra đường dẫn '$RevitApiPath'"
    exit 1
}

# Output paths
$OutputPath = Join-Path $ProjectRoot "dist\Revit2025"
$addinSourcePath = Join-Path $ProjectRoot "addin"

# Build Action
if ($Action -in @("Build", "All")) {
    Write-Host ""
    Write-Host ">>> Building Project..." -ForegroundColor Yellow

    # Determine RevitApiPath for build
    $buildRevitApiPath = if ($UseLocalLib) { $LibPath } else { $RevitApiPath }
    $buildRevitApiPath = $buildRevitApiPath.TrimEnd('\').TrimEnd('/') + '\'

    # Build with dotnet
    $projectFile = Join-Path $ProjectRoot "SheetNumberingRevit.csproj"

    $buildArgs = @(
        "build",
        $projectFile,
        "-c", "Release",
        "-p:REVIT_API_PATH=$buildRevitApiPath",
        "-p:PlatformTarget=x64"
    )

    Write-Host "Executing: dotnet $($buildArgs -join ' ')" -ForegroundColor Gray

    $buildResult = & dotnet @buildArgs 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed!"
        Write-Host $buildResult
        exit 1
    }

    Write-Host "[OK] Build completed" -ForegroundColor Green

    # Ensure output directory exists
    if (-not (Test-Path $OutputPath)) {
        New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
    }

    # Verify DLL was built
    $dllSource = Join-Path $OutputPath "SheetNumberingRevit.dll"
    if (-not (Test-Path $dllSource)) {
        Write-Error "DLL not found at expected location: $dllSource"
        exit 1
    }
    Write-Host "[OK] DLL built to: $dllSource" -ForegroundColor Green
}

# Install Action
if ($Action -in @("Install", "All")) {
    Write-Host ""
    Write-Host ">>> Installing Add-in..." -ForegroundColor Yellow

    $addinDestPath = "$env:APPDATA\Autodesk\Revit\Addins\2025"

    # Create addins folder if not exists
    if (-not (Test-Path $addinDestPath)) {
        New-Item -ItemType Directory -Path $addinDestPath -Force | Out-Null
        Write-Host "[OK] Created Addins folder: $addinDestPath" -ForegroundColor Green
    }

    # Install main addin
    $addinSource = Join-Path $addinSourcePath "SheetNumberingRevit.addin"
    $addinDest = Join-Path $addinDestPath "SheetNumberingRevit.addin"

    if (Test-Path $addinSource) {
        Copy-Item -Path $addinSource -Destination $addinDest -Force
        Write-Host "[OK] Installed: $addinDest" -ForegroundColor Green
    } else {
        Write-Warning "Addon file not found: $addinSource"
    }

    # Optionally install ribbon addin
    $addinRibbonSource = Join-Path $addinSourcePath "SheetNumberingRevit_Ribbon.addin"
    $addinRibbonDest = Join-Path $addinDestPath "SheetNumberingRevit_Ribbon.addin"

    if (Test-Path $addinRibbonSource) {
        Copy-Item -Path $addinRibbonSource -Destination $addinRibbonDest -Force
        Write-Host "[OK] Installed: $addinRibbonDest" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Hoàn thành!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Để sử dụng:" -ForegroundColor White
Write-Host "1. Khởi động Revit 2025" -ForegroundColor White
Write-Host "2. Tìm tab 'TOOLS API' > Panel 'Sheet Numbering'" -ForegroundColor White
Write-Host "3. Click logo để bắt đầu" -ForegroundColor White
Write-Host ""
Write-Host "Build output: $OutputPath" -ForegroundColor Gray
Write-Host ""
