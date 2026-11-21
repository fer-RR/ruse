param(
    [string]$Repo = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputPath = (Join-Path $env:TEMP "ruse-license-audit.csv")
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath($Repo)
$noticesPath = Join-Path $repoRoot "THIRD-PARTY-NOTICES.md"
$notices = if (Test-Path -LiteralPath $noticesPath) {
    Get-Content -LiteralPath $noticesPath -Raw
} else {
    ""
}

$knownFallbacks = @{
    "opentk.core" = "MIT"
    "opentk.mathematics" = "MIT"
    "opentk.audio.openal" = "MIT"
    "microsoft.ml.onnxruntime.managed" = "MIT"
}

function Get-NuGetLicense([string]$id, [string]$version) {
    $key = $id.ToLowerInvariant()
    if ($knownFallbacks.ContainsKey($key)) {
        return $knownFallbacks[$key]
    }

    $packageRoot = Join-Path $env:USERPROFILE ".nuget\packages\$key\$version"
    if (-not (Test-Path -LiteralPath $packageRoot)) {
        return "UNKNOWN"
    }

    $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter "*.nuspec" -File |
        Select-Object -First 1
    if ($null -eq $nuspec) {
        return "UNKNOWN"
    }

    [xml]$xml = Get-Content -LiteralPath $nuspec.FullName
    $expression = [string]$xml.package.metadata.licenseExpression
    if (-not [string]::IsNullOrWhiteSpace($expression)) {
        return $expression.Trim()
    }

    $license = $xml.package.metadata.license
    if ($null -eq $license) {
        return "UNKNOWN"
    }

    if ($license.type -eq "expression") {
        return ([string]$license.'#text').Trim()
    }

    if ($license.type -eq "file") {
        $licenseFile = Join-Path $packageRoot ([string]$license.'#text')
        if (-not (Test-Path -LiteralPath $licenseFile)) {
            $licenseFile = Get-ChildItem -LiteralPath $packageRoot -Filter "LICENSE*" -File -Recurse |
                Select-Object -First 1 -ExpandProperty FullName
        }

        if ($licenseFile -and (Test-Path -LiteralPath $licenseFile)) {
            $text = Get-Content -LiteralPath $licenseFile -Raw
            if ($text -match "MIT License") { return "MIT" }
            if ($text -match "Apache License") { return "Apache-2.0" }
            if ($text -match "BSD 3-Clause|BSD-3-Clause") { return "BSD-3-Clause" }
            if ($text -match "GNU GENERAL PUBLIC LICENSE") { return "GPL" }
        }

        return "file:" + ([string]$license.'#text')
    }

    return "UNKNOWN"
}

function Test-NoticeCoverage([string]$id) {
    if ([string]::IsNullOrWhiteSpace($notices)) {
        return $false
    }

    if ($notices.IndexOf($id, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return $true
    }

    $lower = $id.ToLowerInvariant()
    if (($lower -match '^microsoft\.' -or $lower -match '^system\.' -or $lower -match '^communitytoolkit\.') -and
        $notices -match "Microsoft \.NET runtime") {
        return $true
    }
    if ($lower -match '^opentk\.' -and $notices -match "OpenTK") { return $true }
    if ($lower -match '^piper' -and $notices -match "Piper") { return $true }
    if ($lower -match '^kokoro' -and $notices -match "Kokoro") { return $true }
    if ($lower -match '^misaki' -and $notices -match "Misaki") { return $true }
    if ($lower -match '^google\.protobuf' -and $notices -match "Google\.Protobuf") { return $true }
    if ($lower -match '^newtonsoft' -and $notices -match "Newtonsoft") { return $true }
    if ($lower -match '^sharpcompress' -and $notices -match "SharpCompress") { return $true }
    if ($lower -match '^numsharp' -and $notices -match "NumSharp") { return $true }
    if ($lower -match '^(react|react-dom|scheduler)$' -and $notices -match "React") { return $true }
    if ($lower -match '^@xyflow|^zustand|^classcat|^use-sync-external-store' -and
        $notices -match "React Flow dependencies") {
        return $true
    }
    if ($lower -match '^@dnd-kit' -and $notices -match "dnd-kit") { return $true }
    if ($lower -match '^d3-' -and $notices -match "d3 modules") { return $true }
    if ($lower -match '^lucide' -and $notices -match "Lucide") { return $true }
    if ($lower -match '^@types|^csstype$' -and $notices -match "TypeScript type") {
        return $true
    }
    if ($lower -match '^org\.k2fsa\.sherpa\.onnx' -and $notices -match "org\.k2fsa\.sherpa\.onnx") {
        return $true
    }
    if ($lower -match '^tslib$' -and $notices -match "tslib") { return $true }
    return $false
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($assetsPath in @(
    (Join-Path $repoRoot "src\Ruse.App\obj\project.assets.json"),
    (Join-Path $repoRoot "src\Ruse.TtsHost\obj\project.assets.json")
)) {
    if (-not (Test-Path -LiteralPath $assetsPath)) {
        continue
    }

    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    foreach ($property in $assets.libraries.PSObject.Properties) {
        $library = $property.Value
        if ($library.type -ne "package") {
            continue
        }

        $parts = $property.Name.Split("/")
        if ($parts.Length -ne 2) {
            continue
        }

        $id = $parts[0]
        $version = $parts[1]
        $license = Get-NuGetLicense $id $version
        $notice = Test-NoticeCoverage $id
        $risk = "OK"
        if ($license -eq "UNKNOWN") { $risk = "UNKNOWN" }
        elseif ($license -match '(?i)\b(A?GPL|LGPL)\b') { $risk = "GPL" }
        elseif (-not $notice) { $risk = "MISSING_NOTICE" }

        $results.Add([pscustomobject]@{
            Ecosystem = "nuget"
            Id = $id
            Version = $version
            License = $license
            InNotices = $notice
            Risk = $risk
        })
    }
}

$packageLock = Join-Path $repoRoot "src\Ruse.App\WebUi\package-lock.json"
if (Test-Path -LiteralPath $packageLock) {
    $nodeScript = @'
const lock = require(process.argv[1]);
const out = [];
for (const [path, value] of Object.entries(lock.packages || {})) {
  if (!path || value.dev) continue;
  out.push({ id: path.replace(/^node_modules\//, ''), version: value.version || '', license: value.license || 'UNKNOWN' });
}
process.stdout.write(JSON.stringify(out));
'@
    $npmPackages = (& node -e $nodeScript $packageLock) | ConvertFrom-Json
    foreach ($package in $npmPackages) {
        $notice = Test-NoticeCoverage $package.id
        $risk = "OK"
        if ($package.license -eq "UNKNOWN") { $risk = "UNKNOWN" }
        elseif ($package.license -match '(?i)\b(A?GPL|LGPL)\b') { $risk = "GPL" }
        elseif (-not $notice) { $risk = "MISSING_NOTICE" }

        $results.Add([pscustomobject]@{
            Ecosystem = "npm"
            Id = $package.id
            Version = $package.version
            License = $package.license
            InNotices = $notice
            Risk = $risk
        })
    }
}

$requiredLicenses = @(
    'MIT.txt',
    'Apache-2.0.txt',
    'BSD-3-Clause.txt',
    '0BSD.txt',
    'CC-BY-4.0.txt',
    'WebView2-LICENSE.txt',
    'WebView2-NOTICE.txt',
    'ONNX-Runtime-LICENSE.txt',
    'ONNX-Runtime-ThirdPartyNotices.txt',
    'GCC-Runtime-Library-Exception.txt'
)
foreach ($file in $requiredLicenses) {
    $exists = Test-Path -LiteralPath (Join-Path $repoRoot "LICENSES\$file")
    if (-not $exists) {
        $results.Add([pscustomobject]@{
            Ecosystem = "license-file"
            Id = $file
            Version = ""
            License = "UNKNOWN"
            InNotices = $false
            Risk = "MISSING_LICENSE_FILE"
        })
    }
}

$results | Sort-Object Ecosystem, Id | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Encoding UTF8

$problems = @($results | Where-Object { $_.Risk -ne "OK" })
Write-Host ("License audit: {0} packages, {1} problem(s)." -f $results.Count, $problems.Count)
Write-Host ("Report: {0}" -f $OutputPath)
if ($problems.Count -gt 0) {
    $problems | Format-Table Ecosystem, Id, Version, License, InNotices, Risk -AutoSize | Out-String | Write-Host
    exit 1
}
