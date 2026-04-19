$ErrorActionPreference = "Stop"

$version = "1.44.55.0"
$targets = @(
    "C:\Users\Administrator\AppData\Local\Temp\quicker_cs\$version",
    "$env:LOCALAPPDATA\Temp\quicker_cs\$version"
) | Select-Object -Unique

foreach ($target in $targets) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Write-Host "Created: $target"

    $root = Split-Path -Parent $target
    icacls $root /grant "*S-1-5-32-545:(OI)(CI)F" /T | Out-Host
    icacls $root /grant "$($env:USERNAME):(OI)(CI)F" /T | Out-Host
    Write-Host "Granted write permissions: $root"
}

Write-Host "Quicker C# temp folders are ready."
