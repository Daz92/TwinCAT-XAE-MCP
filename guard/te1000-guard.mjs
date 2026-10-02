#!/usr/bin/env node
// Claude Code PreToolUse hook for the te1000 MCP server.
//
// te1000's confirm tokens are passed by the agent itself, so they cannot keep an
// agent away from the target runtime. This hook is the deterministic boundary:
//   1. Tools and actions that reach the runtime are always denied.
//   2. A call carrying a user-only confirm token is denied; ALLOW_TWINCAT_DELETE asks the user.
//   3. A repository can add its own deny/ask rules in .te1000-policy.json at its root.
//   4. Engineering writes take a per-host lock so two agents never write the same XAE at once.
//
// Input: the hook JSON on stdin ({tool_name, tool_input, cwd, session_id}).
// Output: a PreToolUse decision on stdout, or nothing to let the call through.

import { readFileSync, writeFileSync, mkdirSync, existsSync, openSync, closeSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { homedir } from "node:os";
import { fileURLToPath } from "node:url";

// Any MCP server whose name contains "te1000" (te1000, te1000-local, my-te1000, ...).
const PREFIX = /^mcp__[A-Za-z0-9-]*te1000[A-Za-z0-9-]*__/i;

// Tools whose every action touches the target runtime or is an unconstrained escape hatch.
const RUNTIME_TOOLS = new Set([
  "twincat_activate_configuration",
  "twincat_restart_runtime",
  "plc_download",
  "xae_command",
]);

// Runtime-touching actions inside otherwise engineering-only tools.
const RUNTIME_ACTIONS = {
  plc_project: ["online", "generate_boot_project"],
  plc_session: ["logout"],
  tc_license: ["activate_response"],
  tc_route: ["add_route", "add_project_route"],
  tc_cpp: ["publish"],
  tc_measurement: ["scope_record"],
  // ScanIoBoxes talks to the live EtherCAT master on the target.
  tc_system: ["scan_io_boxes"],
};

// Actions that need the user's yes every time: dialog_resolve clicks a button on whatever
// modal XAE shows (including activate/restart prompts); set_netid changes the activation target;
// project_unload/project_reload change what XAE has loaded for every client.
const ASK_ACTIONS = {
  xae: ["dialog_resolve", "project_unload", "project_reload"],
  tc_system: ["set_netid"],
};

// analytics_set ops that unload/reload the System Manager project or close/reopen the
// solution to edit the .tsproj on disk (stream_remove only when its live DeleteChild fails).
// A dryRun touches nothing and passes.
const OFFLINE_ANALYTICS_OPS = ["target_remove", "stream_edit", "stream_add", "stream_remove"];

const DELETE_TOKEN = "ALLOW_TWINCAT_DELETE";

// Actions that only read. Everything else is treated as an engineering write.
const READ_ACTIONS = new Set([
  "status", "error_list", "dialog_probe", "active_document", "selected_items", "solution_explorer", "list_commands",
  "get", "get_batch", "children", "exists", "exists_batch", "get_xml", "export", "focus",
  "get_decl", "get_impl", "get_document", "get_graphical", "outline", "tree", "find", "search",
  "check_objects", "info", "list", "scan", "repos", "links", "resolve", "produce",
  "tasks", "axes", "axis", "get_netid", "errors", "broadcast_search", "search_host",
  "get_rt_settings", "get_linked_task", "get_silent_mode", "get_target_platform",
  "get_independent_file", "get_disabled", "get_config", "get_current", "list_resources",
  "analytics_get", "plcopen_export",
]);

const LOCK_TTL_MS = 10 * 60 * 1000;

export function findPolicy(cwd) {
  let dir = resolve(cwd || process.cwd());
  for (;;) {
    const candidate = join(dir, ".te1000-policy.json");
    if (existsSync(candidate)) {
      return { path: candidate, policy: JSON.parse(readFileSync(candidate, "utf8")) };
    }
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

function ruleMatches(rule, tool, action) {
  if (rule.tool !== tool) return false;
  return !rule.actions || rule.actions.includes(action ?? "");
}

export function isWrite(tool, action) {
  if (tool === "xae_build" || tool === "tc_ethercat") return true;
  return !READ_ACTIONS.has(action ?? "");
}

// Returns { decision: "deny"|"ask"|"allow", reason }.
export function decide(input, { lockFile, now = Date.now() } = {}) {
  const name = input.tool_name ?? "";
  if (!PREFIX.test(name)) return { decision: "allow" };
  const tool = name.replace(PREFIX, "");
  const args = input.tool_input ?? {};
  const action = args.action;

  if (RUNTIME_TOOLS.has(tool)) {
    return { decision: "deny", reason: `te1000 guard: ${tool} reaches the target runtime; only the user runs it, in the XAE IDE.` };
  }
  const stoppingScope = tool === "tc_measurement" && action === "scope_record" && args.state === "stop";
  if (RUNTIME_ACTIONS[tool]?.includes(action) && !stoppingScope) {
    return { decision: "deny", reason: `te1000 guard: ${tool} ${action} reaches the target runtime; only the user runs it.` };
  }

  const confirm = args.confirm;
  if (confirm && confirm !== DELETE_TOKEN) {
    return { decision: "deny", reason: `te1000 guard: confirm token ${confirm} is user-only; agents never pass it.` };
  }

  const found = findPolicy(input.cwd);
  if (found) {
    for (const rule of found.policy.deny ?? []) {
      if (ruleMatches(rule, tool, action)) {
        return { decision: "deny", reason: `te1000 guard (${found.path}): ${rule.reason ?? "denied by repository policy"}` };
      }
    }
  }

  let result = { decision: "allow" };
  if (confirm === DELETE_TOKEN) {
    result = { decision: "ask", reason: "te1000 guard: this call deletes XAE configuration; confirm with the user." };
  } else if (ASK_ACTIONS[tool]?.includes(action)) {
    result = { decision: "ask", reason: `te1000 guard: ${tool} ${action} needs the user's yes every time (it can affect the target or what XAE has loaded); confirm with the user.` };
  } else if (tool === "tc_measurement" && action === "analytics_set" && OFFLINE_ANALYTICS_OPS.includes(args.op) && args.dryRun !== true) {
    result = { decision: "ask", reason: `te1000 guard: analytics_set ${args.op} unloads the System Manager project (or closes the solution) to edit the .tsproj on disk; confirm with the user.` };
  }
  if (found && result.decision === "allow") {
    for (const rule of found.policy.ask ?? []) {
      if (ruleMatches(rule, tool, action)) {
        result = { decision: "ask", reason: `te1000 guard (${found.path}): ${rule.reason ?? "repository policy asks first"}` };
        break;
      }
    }
  }

  if (isWrite(tool, action) && lockFile) {
    const me = input.session_id;
    if (!me) {
      return { decision: "deny", reason: "te1000 guard: no session id in the hook input, so the write lock cannot tell agents apart." };
    }
    const held = readLock(lockFile);
    if (held && held.session !== me && now - held.at < LOCK_TTL_MS) {
      const ageS = Math.round((now - held.at) / 1000);
      return { decision: "deny", reason: `te1000 guard: another agent session (${held.session}) wrote to XAE ${ageS} s ago and holds the write lock (${lockFile}). Wait, or ask the user to clear it.` };
    }
    // Take or refresh the lock only for a call that will run now; an "ask" may be refused.
    if (result.decision === "allow") {
      const value = { session: me, at: now, tool, action: action ?? null };
      if (!held && !createLock(lockFile, value)) {
        const winner = readLock(lockFile);
        if (winner && winner.session !== me) {
          return { decision: "deny", reason: `te1000 guard: another agent session (${winner.session}) took the XAE write lock at the same moment (${lockFile}).` };
        }
      }
      writeLock(lockFile, value);
    }
  }
  return result;
}

function readLock(file) {
  try { return JSON.parse(readFileSync(file, "utf8")); } catch { return null; }
}

// Atomic create: false when another process created the file first.
function createLock(file, value) {
  mkdirSync(dirname(file), { recursive: true });
  try {
    const fd = openSync(file, "wx");
    writeFileSync(fd, JSON.stringify(value));
    closeSync(fd);
    return true;
  } catch (err) {
    if (err.code === "EEXIST") return false;
    throw err;
  }
}

function writeLock(file, value) {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(value));
}

export const defaultLockFile = () =>
  process.env.TE1000_GUARD_LOCK ?? join(homedir(), ".cache", "te1000", "write.lock");

function main() {
  let decision, reason;
  try {
    const input = JSON.parse(readFileSync(0, "utf8") || "{}");
    ({ decision, reason } = decide(input, { lockFile: defaultLockFile() }));
  } catch (err) {
    // Fail closed: an unreadable policy, lock or input must not let a call through.
    decision = "deny";
    reason = `te1000 guard failed, so the call is blocked: ${err.message}`;
  }
  if (decision === "allow") return;
  process.stdout.write(JSON.stringify({
    hookSpecificOutput: {
      hookEventName: "PreToolUse",
      permissionDecision: decision,
      permissionDecisionReason: reason,
    },
  }));
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
