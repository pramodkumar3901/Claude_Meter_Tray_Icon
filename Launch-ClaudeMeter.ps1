# Stops any running ClaudeMeterTrayIcon, rebuilds the Release build, and starts it again.
# Run through Launch-ClaudeMeter.cmd (double-click) or directly with PowerShell.

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$project = Join-Path $root 'ClaudeMeterTrayIcon.csproj'
$exe = Join-Path $root 'bin\Release\net10.0-windows\ClaudeMeterTrayIcon.exe'

# 1. Stop existing instances first. The running exe locks its own file, so the build
#    below would fail if we rebuilt while it was still running.
$running = Get-Process -Name 'ClaudeMeterTrayIcon' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Stopping $($running.Count) running instance(s)..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# 2. Build the Release version so the started app always matches the source.
Write-Host 'Building...'
dotnet build $project -c Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    throw 'Build failed. The tray app was stopped and has not been restarted.'
}

# 3. Start the new instance.
if (-not (Test-Path $exe)) {
    throw "Built executable not found: $exe"
}
Start-Process -FilePath $exe -WorkingDirectory $root
Write-Host 'Claude Meter started. Look for it in the tray.'
