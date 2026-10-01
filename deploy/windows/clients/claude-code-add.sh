#!/bin/sh
# Claude Code: register te1000 with `claude mcp add-json` (user scope; drop
# --scope user for the current project only). Replace <host> and <user>.
# The resulting ~/.claude.json "mcpServers" entry is shown in claude-code.json
# (local: claude-code-local.json). Always name it "te1000": tool guards match that
# name only.

# Remote Windows host over ssh (key auth; BatchMode so it never prompts):
claude mcp add-json --scope user te1000 '{"type":"stdio","command":"ssh","args":["-T","-o","BatchMode=yes","<host>","powershell.exe","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File","C:\\Users\\<user>\\AppData\\Local\\TwinCAT-XAE-MCP\\deploy\\windows\\te1000-mcp-launch.ps1","-Pipe","te1000"]}'

# Claude Code running on the Windows host itself:
# claude mcp add-json --scope user te1000 '{"type":"stdio","command":"powershell.exe","args":["-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File","C:\\Users\\<user>\\AppData\\Local\\TwinCAT-XAE-MCP\\deploy\\windows\\te1000-mcp-launch.ps1","-Pipe","te1000"]}'
