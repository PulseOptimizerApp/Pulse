param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Cache = ""   # a folder kept between builds (actions/cache): used when Intel's release can't be reached
)
# Downloads Intel's PresentMon console app (MIT license) from its official GitHub releases.
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'PulseOptimizer-build' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }  # avoids API rate limits on GitHub Actions
function Use-Cache([string]$why) {
    $cached = if ($Cache) { Get-ChildItem $Cache -Filter 'PresentMon*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 }
    if (-not $cached) { throw "PresentMon couldn't be downloaded and there's no cached copy: $why" }
    Write-Host "::warning::PresentMon source unavailable ($why). Using the cached copy $($cached.Name)."
    New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
    Copy-Item $cached.FullName $Out -Force
    exit 0
}
if ($env:PULSE_TEST_FALLBACK -eq '1') { Use-Cache 'test' }   # lets CI check the fallback path
$release = $null
for ($try = 1; -not $release; $try++) {
    try { $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest' -Headers $headers }
    catch {
        if ($try -ge 5) { Use-Cache $_.Exception.Message }
        if ($try -eq 3) { $headers.Remove('Authorization') }
        Start-Sleep -Seconds (15 * $try)
    }
}
$asset = $release.assets | Where-Object { $_.name -match '^PresentMon-[\d\.]+-x64\.exe$' } | Select-Object -First 1
if (-not $asset) { Use-Cache "no x64 console exe in release $($release.tag_name)" }
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
Write-Host "PresentMon $($release.tag_name): $($asset.name)"
for ($try = 1; ; $try++) {
    try { Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $Out -UseBasicParsing; break }
    catch {
        if ($try -ge 6) { Use-Cache $_.Exception.Message }
        Write-Host "Download failed ($($_.Exception.Message)), retrying in $(15 * $try)s"
        Start-Sleep -Seconds (15 * $try)
    }
}
$sig = Get-AuthenticodeSignature -FilePath $Out
Write-Host "Signature: $($sig.Status) ($($sig.SignerCertificate.Subject))"

# Keep a copy for the next build, in case Intel's release can't be reached then.
if ($Cache) {
    New-Item -ItemType Directory -Force $Cache | Out-Null
    Get-ChildItem $Cache -Filter 'PresentMon*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
    Copy-Item $Out (Join-Path $Cache $asset.name) -Force
}
