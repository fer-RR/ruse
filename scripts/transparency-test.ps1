param(
    [string]$OutDir = "",
    [string]$Visqol = "",
    [switch]$Build
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $repo "src\Ruse.App\bin\Debug\net10.0-windows"
$exe = Join-Path $appDir "Ruse.exe"

if ($Build) {
    dotnet build (Join-Path $repo "src\Ruse.App\Ruse.App.csproj") -v q --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Ruse.exe was not found. Build first or pass -Build."
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $env:TEMP "Ruse-transparency"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$process = Start-Process -FilePath $exe -ArgumentList "--transparency-test `"$OutDir`"" -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Transparency test failed to run (exit $($process.ExitCode))." }

$resultPath = Join-Path $appDir "transparency-test.txt"
if (Test-Path -LiteralPath $resultPath) {
    $resultText = Get-Content -LiteralPath $resultPath -Raw
    Write-Output $resultText
    if ($resultText -match '(?i)\bFAIL(?:ED)?\b') {
        throw "Transparency result reported a failure."
    }
} else {
    Write-Warning "No transparency-test.txt was produced."
}

$reference = Join-Path $OutDir "transparency-reference.wav"
$processed = Join-Path $OutDir "transparency-processed.wav"
if (-not (Test-Path -LiteralPath $reference) -or -not (Test-Path -LiteralPath $processed)) {
    throw "Transparency WAVs were not produced."
}

$visqolExe = $null
if (-not [string]::IsNullOrWhiteSpace($Visqol)) {
    $candidate = if (Test-Path -LiteralPath $Visqol -PathType Container) {
        Join-Path $Visqol "visqol.exe"
    } else {
        $Visqol
    }
    if (Test-Path -LiteralPath $candidate) { $visqolExe = $candidate }
} else {
    $command = Get-Command visqol -ErrorAction SilentlyContinue
    if ($command) { $visqolExe = $command.Source }
}

if (-not $visqolExe) {
    Write-Host ""
    Write-Host "ViSQOL is optional and not bundled." -ForegroundColor Yellow
    Write-Host "Install ViSQOL and pass -Visqol <path-to-visqol.exe>, or put visqol on PATH."
    Write-Host "Then run:"
    Write-Host "  visqol --reference_file `"$reference`" --degraded_file `"$processed`" --verbose"
    exit 0
}

Write-Host ""
Write-Host "Running ViSQOL..." -ForegroundColor Cyan
& $visqolExe --reference_file $reference --degraded_file $processed --verbose
exit $LASTEXITCODE
