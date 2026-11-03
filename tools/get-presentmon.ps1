param([Parameter(Mandatory = $true)][string]$Out)
# Downloads Intel's PresentMon console app (MIT license) from its official GitHub releases.
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'PulseOptimizer-build' }
if ($env:GH_TOKEN) { $headers['Authorization'] = "Bearer $env:GH_TOKEN" }  # avoids API rate limits on GitHub Actions
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/GameTechDev/PresentMon/releases/latest' -Headers $headers
$asset = $release.assets | Where-Object { $_.name -match '^PresentMon-[\d\.]+-x64\.exe$' } | Select-Object -First 1
if (-not $asset) { throw "No x64 console exe found in PresentMon release $($release.tag_name). Assets: $($release.assets.name -join ', ')" }
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
Write-Host "PresentMon $($release.tag_name): $($asset.name)"
Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $Out -UseBasicParsing
$sig = Get-AuthenticodeSignature -FilePath $Out
Write-Host "Signature: $($sig.Status) ($($sig.SignerCertificate.Subject))"
