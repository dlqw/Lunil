#!/usr/bin/env node
"use strict";
const fs = require("fs");
const path = require("path");
const { performance } = require("perf_hooks");

function resolveWasmoon() {
  const candidates = [
    process.env.LUNIL_WASMOON_NODE_PATH,
    path.resolve(__dirname, "../../artifacts/cross-runtime-tools/win-x64/optional/wasmoon-1.16.0/node_modules"),
    path.resolve(__dirname, "../../../artifacts/cross-runtime-tools/win-x64/optional/wasmoon-1.16.0/node_modules"),
  ].filter(Boolean);
  for (const dir of candidates) {
    const pkg = path.join(dir, "wasmoon");
    if (fs.existsSync(pkg)) {
      module.paths.unshift(dir);
      return require("wasmoon");
    }
  }
  return require("wasmoon");
}

async function main() {
  if (process.argv.length < 5) {
    console.error("usage: wasmoon-host.cjs <workload.lua> <operations> <warmup>");
    process.exit(2);
  }
  const { LuaFactory } = resolveWasmoon();
  const workloadPath = path.resolve(process.argv[2]);
  const operations = Number(process.argv[3]);
  const warmupCalls = Number(process.argv[4]);
  const source = fs.readFileSync(workloadPath, "utf8");
  const factory = new LuaFactory();
  const lua = await factory.createEngine();
  const setupStarted = performance.now();
  lua.global.set("WORKLOAD_SOURCE", source);
  await lua.doString(`
    local chunk = assert(load(WORKLOAD_SOURCE, "@workload"))
    function __run(ops)
      return chunk(ops)
    end
  `);
  const run = lua.global.get("__run");
  for (let i = 0; i < warmupCalls; i++) run(1);
  const setup = (performance.now() - setupStarted) / 1000;
  const started = performance.now();
  const result = run(operations);
  const elapsed = (performance.now() - started) / 1000;
  process.stdout.write(
    "cross_runtime_result\telapsed=" + elapsed +
    "\tsetup=" + setup +
    "\toperations=" + operations +
    "\tresult=" + result +
    "\tjit_enabled=0\n"
  );
}
main().catch((err) => {
  console.error(err && err.stack ? err.stack : String(err));
  process.exit(1);
});
