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

import { readFileSync, writeFileSync, mkdirSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { homedir } from "node:os";
import { fileURLToPath } from "node:url";

const PREFIX = /^mcp__te1000__/;

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
};

const DELETE_TOKEN = "ALLOW_TWINCAT_DELETE";

// Actions that only read. Everything else is treated as an engineering write.
const READ_ACTIONS = new Set([
  "status", "error_list", "dialog_probe", "active_document", "selected_items", "list_commands",
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
    const held = readLock(lockFile);
    const me = input.session_id ?? "unknown";
    if (held && held.session !== me && now - held.at < LOCK_TTL_MS) {
      const ageS = Math.round((now - held.at) / 1000);
      return { decision: "deny", reason: `te1000 guard: another agent session (${held.session}) wrote to XAE ${ageS} s ago and holds the write lock (${lockFile}). Wait, or ask the user to clear it.` };
    }
    writeLock(lockFile, { session: me, at: now, tool, action: action ?? null });
  }
  return result;
}

function readLock(file) {
  try { return JSON.parse(readFileSync(file, "utf8")); } catch { return null; }
}

function writeLock(file, value) {
  mkdirSync(dirname(file), { recursive: true });
  writeFileSync(file, JSON.stringify(value));
}

export const defaultLockFile = () =>
  process.env.TE1000_GUARD_LOCK ?? join(homedir(), ".cache", "te1000", "write.lock");

function main() {
  const input = JSON.parse(readFileSync(0, "utf8") || "{}");
  const { decision, reason } = decide(input, { lockFile: defaultLockFile() });
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
