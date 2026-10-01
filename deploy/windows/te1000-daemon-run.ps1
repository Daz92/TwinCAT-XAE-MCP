# te1000-daemon-run.ps1 - body of the scheduled task "TE1000-Daemon-<Pipe>".
# Runs Te1000Daemon.exe in the interactive desktop session (the only session that
# can see XAE's DTE in the Running Object Table) and exits with its code.
#
#   -Pipe          named pipe the daemon serves (default te1000)
#   -SolutionPath  sets TE1000_MCP_SOLUTION_PATH (which open solution to attach to
#                  when several XAE instances run); left unset when not given
#   -AutoDismiss   let the daemon auto-click allowlisted dialogs (default off)
param(
    [string]$Pipe = 'te1000',
    [string]$SolutionPath,
    [switch]$AutoDismiss
)
$ErrorActionPreference = 'Stop'
$installDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($SolutionPath) { $env:TE1000_MCP_SOLUTION_PATH = $SolutionPath }
$daemonArgs = @('--pipe', $Pipe)
if (-not $AutoDismiss) { $daemonArgs += '--no-autodismiss' }
& (Join-Path $installDir 'daemon\bin\Release\Te1000Daemon.exe') @daemonArgs
exit $LASTEXITCODE
