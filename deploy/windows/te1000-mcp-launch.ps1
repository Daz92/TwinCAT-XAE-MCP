# te1000-mcp-launch.ps1 - stdio entry point an MCP client runs (locally or over
# ssh). Starts the daemon's scheduled task if it is not running, waits for its
# pipe, then runs the Node front in the foreground on this process's stdin/stdout
# and exits with node's exit code. The front exits on stdin EOF, so closing the
# client (or the ssh session) leaves no node.exe behind; the daemon task keeps
# running for the next client.
#
# Nothing may be written to stdout before node starts: stdout is the MCP stream.
param([string]$Pipe = 'te1000')
$ErrorActionPreference = 'Stop'
$installDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskName = "TE1000-Daemon-$Pipe"

$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if (-not $task) {
    [Console]::Error.WriteLine("Scheduled task '$taskName' not found; run $installDir\deploy\windows\install.ps1 -Pipe $Pipe first.")
    exit 2
}
if ($task.State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }

$deadline = (Get-Date).AddSeconds(30)
$up = $false
while (-not $up -and (Get-Date) -lt $deadline) {
    $client = [IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [IO.Pipes.PipeDirection]::InOut)
    try { $client.Connect(1000); $up = $true } catch { Start-Sleep -Milliseconds 500 } finally { $client.Dispose() }
}
if (-not $up) {
    [Console]::Error.WriteLine("Daemon pipe '$Pipe' did not come up within 30 s (task '$taskName'). Is the user logged on interactively?")
    exit 3
}

$env:TE1000_DAEMON_PIPE = $Pipe
$node = Get-Command node.exe -ErrorAction SilentlyContinue
$nodeExe = if ($node) { $node.Source } else { 'C:\Program Files\nodejs\node.exe' }
& $nodeExe (Join-Path $installDir 'index.js')
exit $LASTEXITCODE
