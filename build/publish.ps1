param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "dist\win-x64",
    [string]$Project = "src\Agent\Agent.csproj"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot $Project
$outputPath = Join-Path $repoRoot $Output

if (Test-Path $outputPath) {
    Remove-Item -Recurse -Force $outputPath
}

dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:PublishTrimmed=false `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    -o $outputPath

Write-Host "Publish complete: $outputPath"
