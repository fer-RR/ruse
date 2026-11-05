param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [int]$TimeoutSeconds = 300,
    [switch]$Build,
    [uint32]$ProcessId = 0,
    [string]$Vst3Plugin = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath($Repo)
$appDir = Join-Path $repoRoot "src\Ruse.App\bin\Debug\net10.0-windows"
$exe = Join-Path $appDir "Ruse.exe"
$failures = [System.Collections.Generic.List[string]]::new()
$results = [System.Collections.Generic.List[string]]::new()

if ($Build) {
    Write-Host "Building Ruse..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot "src\Ruse.App\Ruse.App.csproj") -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed."
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Error "Ruse.exe was not found at $exe. Build first or pass -Build."
    exit 1
}

function Invoke-RuseTest {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [string]$ResultFile
    )

    $resultPath = Join-Path $appDir $ResultFile
    Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    $stdout = Join-Path $env:TEMP ("ruse-regress-" + [Guid]::NewGuid().ToString("N") + ".out")
    $stderr = Join-Path $env:TEMP ("ruse-regress-" + [Guid]::NewGuid().ToString("N") + ".err")

    $argumentLine = ($Arguments | ForEach-Object {
        if ($_ -match '\s') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }) -join ' '

    try {
        $process = Start-Process -FilePath $exe -ArgumentList $argumentLine -WorkingDirectory $appDir `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    } catch {
        $failures.Add("${Name}: could not start Ruse: $($_.Exception.Message)")
        return
    }

    $exited = $process.WaitForExit($TimeoutSeconds * 1000)
    if (-not $exited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $failures.Add("${Name}: timed out after $TimeoutSeconds s")
        return
    }

    $process.Refresh()
    $exitCode = $process.ExitCode

    $text = ""
    if (Test-Path -LiteralPath $resultPath) {
        $text = (Get-Content -LiteralPath $resultPath -Raw).Trim()
    }

    if ([string]::IsNullOrWhiteSpace($text)) {
        $failures.Add("${Name}: missing result file $ResultFile")
        Write-Host ("[FAIL] {0}: missing result" -f $Name) -ForegroundColor Red
        return
    }

    $results.Add("${Name}: $text")
    $failed = $text -match "\b(FAIL|FAILED)\b"
    if (($null -ne $exitCode -and $exitCode -ne 0) -or $failed) {
        $failures.Add("${Name}: exit=$exitCode $text")
        Write-Host ("[FAIL] {0}: exit={1} {2}" -f $Name, $exitCode, $text) -ForegroundColor Red
    } else {
        Write-Host ("[ OK ] {0}: {1}" -f $Name, $text) -ForegroundColor Green
    }

    Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
}

$tests = @(
    @{ Name = "web";       Arguments = @("--web-test");       File = "web-test.txt" },
    @{ Name = "selftest";  Arguments = @("--selftest");       File = "latency-selftest.txt" },
    @{ Name = "effects";   Arguments = @("--effects-test");   File = "effects-test.txt" },
    @{ Name = "dsp";       Arguments = @("--dsp-test");       File = "dsp-test.txt" },
    @{ Name = "sidechain"; Arguments = @("--sidechain-test"); File = "sidechain-test.txt" },
    @{ Name = "clipguard"; Arguments = @("--clip-guard-test"); File = "clip-guard-test.txt" },
    @{ Name = "ring"; Arguments = @("--ring-test"); File = "ring-test.txt" },
    @{ Name = "transparency"; Arguments = @("--transparency-test"); File = "transparency-test.txt" },
    @{ Name = "record";    Arguments = @("--record-test");    File = "record-test.txt" },
    @{ Name = "chain";     Arguments = @("--chain-test");     File = "chain-test.txt" },
    @{ Name = "fanout";    Arguments = @("--fanout-test");    File = "fanout-test.txt" },
    @{ Name = "converter"; Arguments = @("--converter-test"); File = "converter-test.txt" },
    @{ Name = "modifier";  Arguments = @("--modifier-test");  File = "modifier-test.txt" },
    @{ Name = "preset";    Arguments = @("--preset-test");    File = "preset-test.txt" },
    @{ Name = "filter";    Arguments = @("--filter-test");    File = "filter-test.txt" },
    @{ Name = "align";     Arguments = @("--align-test");     File = "align-test.txt" }
)

foreach ($test in $tests) {
    Invoke-RuseTest -Name $test.Name -Arguments $test.Arguments -ResultFile $test.File
}

if ($ProcessId -gt 0) {
    Invoke-RuseTest -Name "graph" -Arguments @("--graph-test", "--pid", "$ProcessId", "--seconds", "20") -ResultFile "graph-test.txt"
    Invoke-RuseTest -Name "device" -Arguments @("--device-test", "--pid", "$ProcessId", "--seconds", "20") -ResultFile "device-test.txt"
}

if (-not [string]::IsNullOrWhiteSpace($Vst3Plugin)) {
    $vstFile = "vst3-isolated-test.txt"
    $vstOut = Join-Path $appDir $vstFile
    Invoke-RuseTest -Name "vst3-isolated" -Arguments @("--vst3-isolated-test", $Vst3Plugin, $vstOut) -ResultFile $vstFile
}

Invoke-RuseTest -Name "bench" -Arguments @("--bench", "--sources", "8", "--outputs", "4", "--seconds", "3") -ResultFile "bench-result.txt"

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host ("Regression FAILED ({0} test(s)):" -f $failures.Count) -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    exit 1
}

Write-Host ("Regression PASS ({0} test(s))." -f $results.Count) -ForegroundColor Green
exit 0
