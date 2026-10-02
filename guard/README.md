# te1000 guard

te1000's confirm tokens are passed by the agent itself, so they cannot keep an agent away
from the target runtime. `te1000-guard.mjs` is a Claude Code `PreToolUse` hook that makes
that boundary deterministic. It needs only Node 20+ and has no dependencies.

## What it decides

| Call | Decision |
|---|---|
| `twincat_activate_configuration`, `twincat_restart_runtime`, `plc_download`, `xae_command` | deny |
| `plc_project online` / `generate_boot_project`, `plc_session logout`, `tc_license activate_response`, `tc_route add_route` / `add_project_route`, `tc_cpp publish`, `tc_measurement scope_record` with `state: "start"` | deny |
| any call whose `confirm` is a token other than `ALLOW_TWINCAT_DELETE` | deny |
| `tc_system scan_io_boxes` (talks to the live EtherCAT master) | deny |
| a call with `confirm: "ALLOW_TWINCAT_DELETE"` | ask the user |
| `xae dialog_resolve` (clicks a button on whatever XAE dialog is open), `tc_system set_netid` | ask the user |
| `tc_measurement analytics_set` with `op` `target_remove` or `stream_edit`, unless `dryRun` (unloads the project or closes the solution to edit the `.tsproj`) | ask the user |
| a rule in the repository's `.te1000-policy.json` | deny or ask, as the rule says |
| any engineering write while another agent session wrote in the last 10 minutes | deny (write lock) |
| everything else | allow |

The guard applies to every MCP server whose name contains `te1000` (`te1000`, `te1000-local`,
...). If the guard itself fails (unreadable policy file, lock directory, input), it denies the
call rather than letting it through.

**Write lock.** Every allowed te1000 call that is not a read records the calling session in
`~/.cache/te1000/write.lock` (override with `TE1000_GUARD_LOCK`). A write from a different
session within 10 minutes is denied with the holder's session id. Reads never take the lock.
The lock only covers Claude Code sessions on the same host; other clients (OpenCode, Codex)
do not run this hook.

## Install (Claude Code)

Add to `~/.claude/settings.json` (user scope, so every repository gets it):

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "mcp__.*te1000.*__.*",
        "hooks": [{ "type": "command", "command": "node /path/to/TwinCAT-XAE-MCP/guard/te1000-guard.mjs" }]
      }
    ]
  }
}
```

OpenCode can only allow, ask or deny per tool. Mirror the always-deny tools there:

```jsonc
"permission": [
  { "action": "te1000_twincat_activate_configuration", "resource": "*", "effect": "deny" },
  { "action": "te1000_twincat_restart_runtime", "resource": "*", "effect": "deny" },
  { "action": "te1000_plc_download", "resource": "*", "effect": "deny" },
  { "action": "te1000_xae_command", "resource": "*", "effect": "deny" },
  { "action": "te1000_*", "resource": "*", "effect": "ask" }
]
```

## Repository policy: `.te1000-policy.json`

A repository can add rules in a JSON file at its root. The guard looks for it from the
session's working directory upwards. It is data, not code.

```json
{
  "version": 1,
  "deny": [
    { "tool": "tc_link", "actions": ["link", "unlink", "link_batch", "unlink_batch"],
      "reason": "Process-image links are manifest-owned; use scripts/xae-mappings/xae.py." }
  ],
  "ask": [
    { "tool": "xae", "actions": ["save_all"], "reason": "Saves every open project." }
  ]
}
```

- `tool` is the te1000 tool name without the client prefix.
- `actions` is optional; without it the rule matches every action of the tool.
- `reason` is shown to the agent and the user.

## Tests

```bash
node --test guard/
```
