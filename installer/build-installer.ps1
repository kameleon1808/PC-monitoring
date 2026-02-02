param(
    [string]$Script = "installer\PCMonitoringAgent.iss"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$scriptPath = Join-Path $repoRoot $Script

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidatePaths = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidatePaths) {
        if (Test-Path $candidate) {
            $iscc = Get-Item $candidate
            break
        }
    }
}

if (-not $iscc) {
    throw "Inno Setup Compiler (ISCC.exe) not found. Install Inno Setup and add it to PATH (or install to the default location)."
}

& $iscc.FullName $scriptPath
