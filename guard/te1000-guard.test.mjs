import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, writeFileSync, mkdirSync } from "node:fs";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { decide } from "./te1000-guard.mjs";

const tmp = () => mkdtempSync(join(tmpdir(), "te1000-guard-"));
const call = (tool, input = {}, extra = {}) =>
  ({ tool_name: `mcp__te1000__${tool}`, tool_input: input, cwd: extra.cwd ?? tmp(), session_id: extra.session ?? "s1" });

test("non-te1000 tools pass", () => {
  assert.equal(decide({ tool_name: "Bash", tool_input: {} }).decision, "allow");
});

test("runtime tools are denied", () => {
  for (const t of ["twincat_activate_configuration", "twincat_restart_runtime", "plc_download", "xae_command"]) {
    assert.equal(decide(call(t)).decision, "deny", t);
  }
});

test("runtime actions are denied, scope stop is allowed", () => {
  assert.equal(decide(call("plc_project", { action: "online" })).decision, "deny");
  assert.equal(decide(call("plc_project", { action: "generate_boot_project" })).decision, "deny");
  assert.equal(decide(call("tc_measurement", { action: "scope_record", state: "start" })).decision, "deny");
  assert.equal(decide(call("tc_measurement", { action: "scope_record", state: "stop" })).decision, "allow");
  assert.equal(decide(call("plc_project", { action: "info" })).decision, "allow");
});

test("user-only confirm tokens are denied, delete token asks", () => {
  assert.equal(decide(call("tc_route", { action: "list", confirm: "ALLOW_TWINCAT_ROUTE_WRITE" })).decision, "deny");
  assert.equal(decide(call("tc_tree", { action: "delete", confirm: "ALLOW_TWINCAT_DELETE" })).decision, "ask");
});

test("repository policy deny and ask rules apply from a parent directory", () => {
  const root = tmp();
  writeFileSync(join(root, ".te1000-policy.json"), JSON.stringify({
    version: 1,
    deny: [{ tool: "tc_link", actions: ["link", "unlink"], reason: "manifest-owned" }],
    ask: [{ tool: "xae", actions: ["save_all"], reason: "check other agents" }],
  }));
  const sub = join(root, "a", "b");
  mkdirSync(sub, { recursive: true });
  const linked = decide(call("tc_link", { action: "link" }, { cwd: sub }));
  assert.equal(linked.decision, "deny");
  assert.match(linked.reason, /manifest-owned/);
  assert.equal(decide(call("tc_link", { action: "links" }, { cwd: sub })).decision, "allow");
  assert.equal(decide(call("xae", { action: "save_all" }, { cwd: sub })).decision, "ask");
});

test("write lock blocks a second session and expires", () => {
  const lockFile = join(tmp(), "write.lock");
  const write = (session, now) =>
    decide(call("plc_pou", { action: "replace" }, { session }), { lockFile, now });
  assert.equal(write("A", 1000).decision, "allow");
  assert.equal(write("A", 2000).decision, "allow");
  assert.equal(write("B", 3000).decision, "deny");
  // Reads never need the lock.
  assert.equal(decide(call("plc_pou", { action: "get_impl" }, { session: "B" }), { lockFile, now: 3000 }).decision, "allow");
  // After the TTL the lock is free again.
  assert.equal(write("B", 2000 + 10 * 60 * 1000 + 1).decision, "allow");
});

test("builds count as writes", () => {
  const lockFile = join(tmp(), "write.lock");
  decide(call("xae_build", { action: "build" }, { session: "A" }), { lockFile, now: 0 });
  assert.equal(decide(call("xae_build", { action: "build" }, { session: "B" }), { lockFile, now: 1 }).decision, "deny");
});
