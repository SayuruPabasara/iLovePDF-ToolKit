<#
  Builds the installer. Run on Windows with:
    - .NET 8 SDK            (https://dotnet.microsoft.com/download/dotnet/8.0)
    - Inno Setup 6          (https://jrsoftware.org/isdl.php)  -> ISCC.exe
  Usage:  powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SkipTests]
  Output: artifacts\iLovePDFTools-Setup-<version>.exe
#>
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $SkipTests) {
    dotnet test tests/PdfTools.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

Remove-Item artifacts\publish -Recurse -Force -ErrorAction SilentlyContinue
foreach ($proj in 'PdfTools.Runner', 'PdfTools.Settings') {
    dotnet publish "src/$proj" -c Release -r win-x64 --self-contained true `
        -p:PublishReadyToRun=true -p:DebugType=none -o artifacts/publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $proj" }
}

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it, then re-run." }
& $iscc installer\PdfTools.iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "`nInstaller: $(Resolve-Path artifacts\iLovePDFTools-Setup-1.0.0.exe)"
