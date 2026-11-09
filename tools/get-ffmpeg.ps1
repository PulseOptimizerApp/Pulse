param(
    [Parameter(Mandatory = $true)][string]$Zip,      # where to write ffmpeg-win64.zip (attached to the release)
    [Parameter(Mandatory = $true)][string]$HashOut   # where to write its SHA-256 (built into Pulse)
)
# Packages FFmpeg for Pulse's opt-in Clips feature. Pulse doesn't carry FFmpeg: it downloads this zip from its own
# release only when the user turns Clips on, and checks it against the SHA-256 built into that same Pulse release.
# Source: BtbN/FFmpeg-Builds (GPL build, the newest stable release line), run by Pulse as a separate program.
$ErrorActionPreference = 'Stop'

# If FFmpeg's own site is down (it timed out once and broke a whole release), reuse the exact zip from Pulse's previous
# release instead: it's the same kind of file, already published and checked, and Pulse verifies it by hash either way.
function Use-PreviousRelease([string]$why) {
    $repo = if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'PulseOptimizerApp/Pulse' }
    Write-Host "::warning::FFmpeg source unavailable ($why). Reusing ffmpeg-win64.zip from $repo's latest release."
    New-Item -ItemType Directory -Force (Split-Path $Zip), (Split-Path $HashOut) | Out-Null
    for ($try = 1; ; $try++) {
        try { Invoke-WebRequest -Uri "https://github.com/$repo/releases/latest/download/ffmpeg-win64.zip" -OutFile $Zip -UseBasicParsing; break }
        catch { if ($try -ge 5) { throw "Neither FFmpeg's source nor Pulse's previous release could be downloaded: $why / $($_.Exception.Message)" }; Start-Sleep -Seconds (15 * $try) }
    }
    $hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
    "$hash ffmpeg-win64.zip (reused from the previous Pulse release)" | Set-Content $HashOut -Encoding ascii
    Write-Host "ffmpeg-win64.zip (reused): $hash"
    exit 0
}

$headers = @{ 'User-Agent' = 'PulseOptimizer-build' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }
$release = $null
for ($try = 1; -not $release; $try++) {
    try { $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest' -Headers $headers }
    catch {
        if ($try -ge 6) { Use-PreviousRelease $_.Exception.Message }
        Write-Host "GitHub API busy ($($_.Exception.Message)), retrying in $(20 * $try)s"
        if ($try -eq 3) { $headers.Remove('Authorization') }   # try the other rate-limit pool
        Start-Sleep -Seconds (20 * $try)
    }
}
$asset = $release.assets |
    Where-Object { $_.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-\d+\.\d+\.zip$' } |
    Sort-Object { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$1') }, { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$2') } |
    Select-Object -Last 1
if (-not $asset) { Use-PreviousRelease "no stable win64 GPL build listed" }
Write-Host "FFmpeg: $($asset.name)"

$work = Join-Path ([IO.Path]::GetTempPath()) "pulse-ffmpeg"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, "$work/pack" | Out-Null
# GitHub's download servers sometimes time out (504); retry instead of failing the whole release.
for ($try = 1; ; $try++) {
    try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "$work/src.zip" -UseBasicParsing; break }
    catch {
        if ($try -ge 6) { Use-PreviousRelease $_.Exception.Message }
        Write-Host "Download failed ($($_.Exception.Message)), retrying in $(15 * $try)s"
        Start-Sleep -Seconds (15 * $try)
    }
}
Expand-Archive "$work/src.zip" -DestinationPath "$work/src"
$exe = Get-ChildItem "$work/src" -Recurse -Filter ffmpeg.exe | Select-Object -First 1
$license = Get-ChildItem "$work/src" -Recurse -Filter LICENSE.txt | Select-Object -First 1
Copy-Item $exe.FullName "$work/pack/ffmpeg.exe"
if ($license) { Copy-Item $license.FullName "$work/pack/LICENSE.txt" }
@"
FFmpeg for Pulse Optimizer's Clips feature.

FFmpeg is free software by the FFmpeg developers (https://ffmpeg.org), licensed under the GNU GPL (see LICENSE.txt).
This copy is the unmodified build $($asset.name) from https://github.com/BtbN/FFmpeg-Builds, whose page links the
exact source code and build scripts. Pulse runs it as a separate program; it is not part of Pulse's own code.
"@ | Set-Content "$work/pack/README.txt" -Encoding utf8

New-Item -ItemType Directory -Force (Split-Path $Zip), (Split-Path $HashOut) | Out-Null
Remove-Item $Zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$work/pack/*" -DestinationPath $Zip -CompressionLevel Optimal
$hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
"$hash $($asset.name)" | Set-Content $HashOut -Encoding ascii
Write-Host "ffmpeg-win64.zip: $hash ($([math]::Round((Get-Item $Zip).Length / 1MB)) MB)"
