#!/usr/bin/env node
"use strict";
const fs = require("fs");
const os = require("os");
const path = require("path");
const { spawnSync } = require("child_process");

if (process.argv.length < 6) {
  console.error("usage: luau-host.cjs luau.exe workload.lua operations warmup");
  process.exit(2);
}
const luauExe = path.resolve(process.argv[2]);
const workloadPath = path.resolve(process.argv[3]);
const operations = Number(process.argv[4]);
const warmupCalls = Number(process.argv[5]);
let body = fs.readFileSync(workloadPath, "utf8");
// Luau CLI sandboxes load/loadstring; rewrite workload into a local function.
body = body.replace(/local\s+operations\s*=\s*\.\.\.\s*or\s*1\s*\r?\n/, "");
body = body.replace("local checksum = 0\n", "local checksum = 0.0\n");
body = body.replace("local sum = 0\n", "local sum = 0.0\n");
body = body.replace("local value = 0\n", "local value = 0.0\n");
body = body.replace("local total = 0\n", "local total = 0.0\n");
const script = [
  "local function __workload(operations)",
  body,
  "end",
  "local operations = " + operations,
  "local warmup_calls = " + warmupCalls,
  "local setup_started = os.clock()",
  "for _ = 1, warmup_calls do",
  "  local r = __workload(1)",
  "  assert(type(r) == \"number\")",
  "end",
  "local setup_elapsed = os.clock() - setup_started",
  "local started = os.clock()",
  "local result = __workload(operations)",
  "local elapsed = os.clock() - started",
  "assert(type(result) == \"number\")",
  "print(string.format(\"cross_runtime_result\\telapsed=%.17g\\tsetup=%.17g\\toperations=%d\\tresult=%.17g\\tjit_enabled=0\", elapsed, setup_elapsed, operations, result))",
  ""
].join("\n");
const tmp = path.join(os.tmpdir(), "lunil-luau-bench-" + process.pid + "-" + Date.now() + ".luau");
fs.writeFileSync(tmp, script, "utf8");
try {
  const run = spawnSync(luauExe, [tmp], { encoding: "utf8" });
  if (run.stdout) process.stdout.write(run.stdout);
  if (run.stderr) process.stderr.write(run.stderr);
  process.exit(run.status === null ? 1 : run.status);
} finally {
  try { fs.unlinkSync(tmp); } catch (e) {}
}
