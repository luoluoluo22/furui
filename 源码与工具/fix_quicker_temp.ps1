$ErrorActionPreference = "Stop"

$version = "1.44.55.0"
$targets = @(
    "C:\Users\Administrator\AppData\Local\Temp\quicker_cs\$version",
    "$env:LOCALAPPDATA\Temp\quicker_cs\$version"
) | Select-Object -Unique

foreach ($target in $targets) {
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Write-Host "Created: $target"
}

Write-Host "Quicker C# temp folders are ready."
