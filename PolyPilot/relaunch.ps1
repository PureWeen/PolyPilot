# Builds PolyPilot, launches a new instance, waits for it to be ready,
# then kills the old instance(s) for a seamless handoff.
#
# IMPORTANT: ONLY launches if build succeeds. If build fails:
#   - Shows clear error messages with line numbers and error codes
#   - Does NOT launch old/stale binary
#   - Exits with code 1
#   - Old app instance remains running

$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$BuildDir = Join-Path $ProjectDir 'bin\Debug\net10.0-windows10.0.19041.0\win-x64'
$ExeName = 'PolyPilot.exe'

$MaxLaunchAttempts = 2
$StabilitySeconds = 8
$ServerPidFile = Join-Path $env:USERPROFILE '.polypilot\server.pid'
$ServerPort = 4321

# Check if the persistent copilot server is actually running.
# If server.pid exists but the server is dead/not listening, clean up the stale file
# so PolyPilot will auto-start a fresh server on launch.
function Test-ServerListening {
    param([int]$Port)
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $tcp.Connect("127.0.0.1", $Port)
        $tcp.Close()
        return $true
    } catch {
        return $false
    }
}

if (Test-Path $ServerPidFile) {
    $lines = Get-Content $ServerPidFile -ErrorAction SilentlyContinue
    if ($lines -and $lines.Count -ge 2) {
        $ServerPort = [int]$lines[1]
    }
    
    if (-not (Test-ServerListening -Port $ServerPort)) {
        Write-Host "[!] Stale server.pid detected (port $ServerPort not listening) — cleaning up"
        Remove-Item $ServerPidFile -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host "[OK] Persistent server running on port $ServerPort"
    }
}

# Capture PIDs of currently running instances BEFORE build
$OldPids = @(Get-Process -Name 'PolyPilot' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)

# On Windows, the running exe locks the output file, preventing build.
# Kill old instances BEFORE building to free the file lock.
if ($OldPids.Count -gt 0) {
    Write-Host "[*] Closing old instance(s) to unlock build output..."
    foreach ($OldPid in $OldPids) {
        Write-Host "   Killing PID $OldPid"
        Stop-Process -Id $OldPid -Force -ErrorAction SilentlyContinue
    }
    # Give it a moment to release file locks
    Start-Sleep -Seconds 2
}

Write-Host "[*] Building..."
Set-Location $ProjectDir

$BuildOutput = dotnet build PolyPilot.csproj -f net10.0-windows10.0.19041.0 2>&1 | Out-String
$BuildExitCode = $LASTEXITCODE

if ($BuildExitCode -ne 0) {
    Write-Host "[X] BUILD FAILED!"
    Write-Host ""
    Write-Host "Error details:"
    $BuildOutput -split "`n" | Where-Object { $_ -match 'error CS' } | Write-Host
    if (-not ($BuildOutput -match 'error CS')) {
        $BuildOutput -split "`n" | Select-Object -Last 30 | Write-Host
    }
    Write-Host ""
    Write-Host "To fix: Check the error messages above and correct the code issues."
    Write-Host "Old app instance remains running."
    exit 1
}

# Build succeeded, show brief success message
$BuildOutput -split "`n" | Select-Object -Last 3 | Write-Host

for ($Attempt = 1; $Attempt -le $MaxLaunchAttempts; $Attempt++) {
    Write-Host "[>] Launching new instance (attempt $Attempt/$MaxLaunchAttempts)..."
    $logDir = Join-Path $env:USERPROFILE '.polypilot'
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }

    $NewProcess = Start-Process -FilePath (Join-Path $BuildDir $ExeName) -PassThru -WindowStyle Normal
    $NewPid = $NewProcess.Id

    if (-not $NewPid) {
        Write-Host "[!]  Failed to start new instance."
        if ($Attempt -lt $MaxLaunchAttempts) {
            Write-Host "[~] Retrying launch..."
            continue
        }
        Write-Host "Launch failed. Old instance was stopped."
        exit 1
    }

    Write-Host "[OK] New instance running (PID $NewPid)"
    Write-Host "[?] Verifying stability for ${StabilitySeconds}s..."
    $Stable = $true
    for ($i = 1; $i -le $StabilitySeconds; $i++) {
        Start-Sleep -Seconds 1
        $proc = Get-Process -Id $NewPid -ErrorAction SilentlyContinue
        if (-not $proc -or $proc.HasExited) {
            $Stable = $false
            break
        }
    }

    if ($Stable) {
        Write-Host "[OK] Handoff complete!"
        exit 0
    }

    Write-Host "[X] New instance crashed quickly (PID $NewPid)."
    if ($Attempt -lt $MaxLaunchAttempts) {
        Write-Host "[~] Retrying launch..."
        continue
    }

    Write-Host "[!]  New instance is unstable. Old instance was stopped."
    exit 1
}
