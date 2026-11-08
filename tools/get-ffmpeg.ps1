param(
    [Parameter(Mandatory = $true)][string]$Zip,      # where to write ffmpeg-win64.zip (attached to the release)
    [Parameter(Mandatory = $true)][string]$HashOut   # where to write its SHA-256 (built into Pulse)
)
# Packages FFmpeg for Pulse's opt-in Clips feature. Pulse doesn't carry FFmpeg: it downloads this zip from its own
# release only when the user turns Clips on, and checks it against the SHA-256 built into that same Pulse release.
# Source: BtbN/FFmpeg-Builds (GPL build, the newest stable release line), run by Pulse as a separate program.
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'PulseOptimizer-build' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }
$release = $null
for ($try = 1; -not $release; $try++) {
    try { $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest' -Headers $headers }
    catch {
        if ($try -ge 6) { throw }
        Write-Host "GitHub API busy ($($_.Exception.Message)), retrying in $(20 * $try)s"
        if ($try -eq 3) { $headers.Remove('Authorization') }   # try the other rate-limit pool
        Start-Sleep -Seconds (20 * $try)
    }
}
$asset = $release.assets |
    Where-Object { $_.name -match '^ffmpeg-n(\d+)\.(\d+)-latest-win64-gpl-\d+\.\d+\.zip$' } |
    Sort-Object { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$1') }, { [int]($_.name -replace '^ffmpeg-n(\d+)\.(\d+).*', '$2') } |
    Select-Object -Last 1
if (-not $asset) { throw "No stable win64 GPL FFmpeg build found. Assets: $($release.assets.name -join ', ')" }
Write-Host "FFmpeg: $($asset.name)"

$work = Join-Path ([IO.Path]::GetTempPath()) "pulse-ffmpeg"
Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, "$work/pack" | Out-Null
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile "$work/src.zip" -UseBasicParsing
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
