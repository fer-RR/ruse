param(
    [string]$OutputRoot = "",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $packageParent = Split-Path -Parent $repo
    if ((Split-Path -Leaf $packageParent) -eq 'wt') {
        $packageParent = Split-Path -Parent $packageParent
    }
    $OutputRoot = Join-Path $packageParent "Ruse-package"
}

$project = Join-Path $repo "src\Ruse.App\Ruse.App.csproj"

[xml]$projectXml = Get-Content -LiteralPath $project
$version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not read the project version."
}

$stage = Join-Path $env:TEMP ("ruse-package-" + [Guid]::NewGuid().ToString("N"))
$appDir = Join-Path $stage "Ruse"
$localizationDir = Join-Path $appDir "Localization"
$zipName = "Ruse-$version-$Runtime.zip"
$zipPath = Join-Path $OutputRoot $zipName
$shaPath = Join-Path $OutputRoot ($zipName -replace '\.zip$', '.sha256')

try {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $appDir | Out-Null

    Write-Host "Publishing $version ($Runtime, $Configuration)..." -ForegroundColor Cyan
    dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
        -o $appDir --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "Publish failed."
    }
    $ttsProject = Join-Path $repo "src\Ruse.TtsHost\Ruse.TtsHost.csproj"
    if (Test-Path -LiteralPath $ttsProject) {
        $ttsDir = Join-Path $appDir "tts-host"
        dotnet publish $ttsProject -c $Configuration -r $Runtime --self-contained true -o $ttsDir --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            throw "TTS host publish failed."
        }
    }

    New-Item -ItemType Directory -Force -Path $localizationDir | Out-Null
    $cultureDirs = Get-ChildItem -LiteralPath $appDir -Directory | Where-Object {
        $_.Name -match '^[a-z]{2}(-[A-Za-z]+)?$' -and
        (Get-ChildItem -LiteralPath $_.FullName -Filter "*.resources.dll" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1)
    }

    foreach ($culture in $cultureDirs) {
        Move-Item -LiteralPath $culture.FullName -Destination (Join-Path $localizationDir $culture.Name)
    }

    foreach ($document in @("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md")) {
        $source = Join-Path $repo $document
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $stage $document)
        }
    }

    $licenseDir = Join-Path $repo "LICENSES"
    if (Test-Path -LiteralPath $licenseDir) {
        Copy-Item -LiteralPath $licenseDir -Destination (Join-Path $stage "LICENSES") -Recurse
    }

    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $shaPath -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zipPath -CompressionLevel Optimal

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $zipName" | Set-Content -LiteralPath $shaPath -Encoding ascii

    Write-Host ""
    Write-Host "Package: $zipPath" -ForegroundColor Green
    Write-Host "SHA-256: $hash" -ForegroundColor Green
    Write-Host "Layout:  Ruse\Ruse.exe, Ruse\Localization\<culture>, README.md, LICENSE, THIRD-PARTY-NOTICES.md, LICENSES\"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
