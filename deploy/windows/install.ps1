# install.ps1 - install or refresh te1000-mcp on this Windows host (idempotent).
# Run it from the checkout/install directory, in the interactive user session or
# over ssh as that same user:
#
#   powershell -ExecutionPolicy Bypass -File <install>\deploy\windows\install.ps1 [-Pipe te1000] [-SolutionPath <sln>] [-AutoDismiss]
#
# Steps: npm ci, build the daemon, register/refresh the scheduled task
# "TE1000-Daemon-<Pipe>" (Interactive logon for the current user, runs
# te1000-daemon-run.ps1), start it, ping the daemon over its pipe, and print the
# client launch command. Re-running stops ONLY the daemon serving this -Pipe from
# this install directory before rebuilding; other pipes are left alone (if one of
# them runs this same exe, the build fails on the locked file - stop it first).
param(
    [string]$Pipe = 'te1000',
    [string]$SolutionPath,
    [switch]$AutoDismiss
)
$ErrorActionPreference = 'Stop'
$installDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskName = "TE1000-Daemon-$Pipe"
$exe = Join-Path $installDir 'daemon\bin\Release\Te1000Daemon.exe'
$runScript = Join-Path $PSScriptRoot 'te1000-daemon-run.ps1'
$launchScript = Join-Path $PSScriptRoot 'te1000-mcp-launch.ps1'

$node = Get-Command node.exe -ErrorAction SilentlyContinue
$nodeExe = if ($node) { $node.Source } else { 'C:\Program Files\nodejs\node.exe' }
if (-not (Test-Path -LiteralPath $nodeExe)) { throw "node.exe not found (install Node.js >= 20)." }

Write-Host "Install dir: $installDir"
Write-Host "Task:        $taskName (pipe $Pipe)"

# 1. Stop this pipe's daemon so the exe can be rebuilt.
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
}
Get-CimInstance Win32_Process -Filter "Name = 'Te1000Daemon.exe'" |
    Where-Object { $_.ExecutablePath -eq $exe -and $_.CommandLine -match "--pipe\s+`"?$([regex]::Escape($Pipe))`"?(\s|$)" } |
    ForEach-Object { Write-Host "Stopping daemon PID $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
Start-Sleep -Seconds 1

# 2. Node dependencies (npm-cli.js through node: npm.cmd cannot run from a UNC cwd).
$npmCli = Join-Path (Split-Path -Parent $nodeExe) 'node_modules\npm\bin\npm-cli.js'
Push-Location $installDir
try {
    & $nodeExe $npmCli ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }

# 3. Daemon.
& (Join-Path $installDir 'daemon\build.ps1')

# 4. Scheduled task (Interactive: the daemon must share the desktop session with XAE).
$taskArgs = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$runScript`" -Pipe `"$Pipe`""
if ($SolutionPath) { $taskArgs += " -SolutionPath `"$SolutionPath`"" }
if ($AutoDismiss) { $taskArgs += ' -AutoDismiss' }
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $taskArgs
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

# 5. Verify: ping over the task's pipe.
$client = [IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [IO.Pipes.PipeDirection]::InOut)
try {
    $client.Connect(30000)
    $writer = [IO.StreamWriter]::new($client)
    $writer.AutoFlush = $true
    $reader = [IO.StreamReader]::new($client)
    $writer.WriteLine('{"id":"install-ping","action":"ping","payload":{}}')
    $reply = $reader.ReadLine()
} catch {
    throw "Daemon pipe '$Pipe' did not answer: $($_.Exception.Message). Is '$env:USERNAME' logged on interactively?"
} finally { $client.Dispose() }
if ($reply -notmatch '"pong"\s*:\s*true') { throw "Unexpected ping reply: $reply" }
Write-Host "Daemon OK:   $reply"

Write-Host ''
Write-Host 'Client launch command (stdio MCP server):'
Write-Host "  local: powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launchScript`" -Pipe $Pipe"
Write-Host "  ssh:   ssh -T -o BatchMode=yes <host> powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launchScript`" -Pipe $Pipe"
Write-Host 'Snippets: deploy\windows\clients\'
