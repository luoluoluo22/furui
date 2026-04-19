param(
  [string]$JsonPath = "",
  [string]$OutputDir = "D:\chajian\downloads",
  [int]$Limit = 3
)

$ErrorActionPreference = "Stop"

function Get-LatestJsonPath {
  $latest = Get-ChildItem -Path "$env:USERPROFILE\Downloads" -Filter "*.json" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

  if (-not $latest) {
    throw "No JSON export file was found in $env:USERPROFILE\Downloads."
  }

  return $latest.FullName
}

function Get-SafeFileName {
  param(
    [string]$Value,
    [string]$Fallback
  )

  $sourceValue = if ($null -ne $Value) { [string]$Value } else { "" }
  $safe = $sourceValue.Normalize([Text.NormalizationForm]::FormKC)
  foreach ($char in [IO.Path]::GetInvalidFileNameChars()) {
    $safe = $safe.Replace($char, "_")
  }

  $safe = ($safe -replace "\s+", "_").Trim("_")
  if ([string]::IsNullOrWhiteSpace($safe)) {
    return $Fallback
  }

  if ($safe.Length -gt 80) {
    return $safe.Substring(0, 80)
  }

  return $safe
}

function Save-VideoFile {
  param(
    [string]$Url,
    [string]$Referer,
    [string]$Destination
  )

  $request = [System.Net.HttpWebRequest]::Create($Url)
  $request.Method = "GET"
  $request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
  $request.Referer = if ($Referer) { $Referer } else { "https://www.douyin.com/" }
  $request.Accept = "*/*"
  $request.Headers["Accept-Language"] = "zh-SG,zh-CN;q=0.9,zh;q=0.8"
  $request.AddRange(0)
  $request.Timeout = 30000
  $request.ReadWriteTimeout = 30000

  $response = $null
  $source = $null
  $target = $null

  try {
    $response = [System.Net.HttpWebResponse]$request.GetResponse()
    if ($response.StatusCode -ne [System.Net.HttpStatusCode]::PartialContent -and
        $response.StatusCode -ne [System.Net.HttpStatusCode]::OK) {
      throw "Unexpected status code: $([int]$response.StatusCode)"
    }

    $source = $response.GetResponseStream()
    $target = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)

    $buffer = New-Object byte[] (1024 * 256)
    while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
      $target.Write($buffer, 0, $read)
    }
  }
  finally {
    if ($target) { $target.Dispose() }
    if ($source) { $source.Dispose() }
    if ($response) { $response.Dispose() }
  }
}

function Get-ResultItems {
  param(
    [string]$RawText
  )

  try {
    $payload = $RawText | ConvertFrom-Json
    return @($payload.results) | Where-Object { $_.videoUrl }
  }
  catch {
    $pattern = '(?s)"videoId"\s*:\s*"(?<videoId>\d+)".*?"detailUrl"\s*:\s*"(?<detailUrl>https://www\.douyin\.com/video/\d+)".*?"videoUrl"\s*:\s*"(?<videoUrl>https://[^"]+)".*?(?:"author"\s*:\s*"(?<author>[^"]*)")?.*?(?:"title"\s*:\s*"(?<title>[^"]*)")?'
    $matches = [regex]::Matches($RawText, $pattern)
    $items = @()

    foreach ($match in $matches) {
      $items += [pscustomobject]@{
        videoId = $match.Groups["videoId"].Value
        detailUrl = $match.Groups["detailUrl"].Value
        videoUrl = $match.Groups["videoUrl"].Value
        author = $match.Groups["author"].Value
        title = $match.Groups["title"].Value
      }
    }

    return $items
  }
}

if (-not $JsonPath) {
  $JsonPath = Get-LatestJsonPath
}

if (-not (Test-Path $JsonPath)) {
  throw "JSON file not found: $JsonPath"
}

New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

$rawText = Get-Content -Path $JsonPath -Raw
$results = Get-ResultItems -RawText $rawText

if ($Limit -gt 0) {
  $results = $results | Select-Object -First $Limit
}

if (-not $results -or $results.Count -eq 0) {
  throw "No downloadable videoUrl entries were found in $JsonPath"
}

$downloaded = @()

foreach ($item in $results) {
  $videoId = "$($item.videoId)".Trim()
  $baseName = Get-SafeFileName -Value "$($item.author)-$($item.title)-$videoId" -Fallback $videoId
  $targetPath = Join-Path $OutputDir "$baseName.mp4"

  Write-Host "Downloading $videoId -> $targetPath"
  Save-VideoFile -Url $item.videoUrl -Referer $item.detailUrl -Destination $targetPath

  $downloaded += [pscustomobject]@{
    videoId = $videoId
    file = $targetPath
    sourceJson = $JsonPath
  }
}

$downloaded | ConvertTo-Json -Depth 3
