param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [double]$Seconds = 3,
    [switch]$Build,
    [switch]$Graph
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath($Repo)
$appDir = Join-Path $repoRoot "src\Ruse.App\bin\$Configuration\net10.0-windows"
$exe = Join-Path $appDir "Ruse.exe"
$resultPath = Join-Path $appDir "bench-result.txt"

if ($Build -or -not (Test-Path -LiteralPath $exe)) {
    Write-Host "Building Ruse ($Configuration)..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot "src\Ruse.App\Ruse.App.csproj") -c $Configuration -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

if (-not (Test-Path -LiteralPath $exe)) {
    throw "Ruse.exe was not found at $exe."
}

$scenarios = @(
    [pscustomobject]@{ Name = "1 source -> 1 output";   Sources = 1;  Outputs = 1 },
    [pscustomobject]@{ Name = "8 sources -> 4 outputs"; Sources = 8;  Outputs = 4 },
    [pscustomobject]@{ Name = "32 sources -> 8 outputs"; Sources = 32; Outputs = 8 }
)

$rows = foreach ($scenario in $scenarios) {
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $benchArgs = @(
        "--bench", "--sources", "$($scenario.Sources)", "--outputs", "$($scenario.Outputs)",
        "--seconds", "$Seconds"
    )
    if ($Graph) { $benchArgs += "--graph" }
    $process = Start-Process -FilePath $exe -WorkingDirectory $appDir -WindowStyle Hidden -PassThru -Wait -ArgumentList $benchArgs
    $clock.Stop()

    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultPath)) {
        throw "Benchmark failed for '$($scenario.Name)' (exit $($process.ExitCode))."
    }

    $line = (Get-Content -LiteralPath $resultPath -Raw).Trim()
    if ($line -match "FAILED") {
        throw "Benchmark failed for '$($scenario.Name)': $line"
    }

    if ($line -notmatch '([\d.,]+) frames/s mixed.*CPU ([\d.,]+) ms \(([\d.,]+) cores\).*GC alloc ([\d.,]+) KiB/s') {
        throw "Unexpected benchmark output for '$($scenario.Name)': $line"
    }

    $framesPerSecond = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    [pscustomobject]@{
        Scenario = $scenario.Name
        'Frames/s' = [Math]::Round($framesPerSecond)
        'Frames/s/output' = [Math]::Round($framesPerSecond / $scenario.Outputs)
        'Realtime x' = [Math]::Round($framesPerSecond / $scenario.Outputs / 48000.0, 1)
        'CPU ms' = [Math]::Round([double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture))
        'Cores' = $Matches[3]
        'GC KiB/s' = $Matches[4]
        'Wall s' = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
    }
}

Write-Host ""
Write-Host "Ruse benchmark ($Configuration, $Seconds s per scenario, unpaced mixer capacity)" -ForegroundColor Cyan
Write-Host ("Machine: {0} | {1} logical processors" -f $env:PROCESSOR_IDENTIFIER, [Environment]::ProcessorCount)
Write-Host ""
$rows | Format-Table -AutoSize -Wrap
