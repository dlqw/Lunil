local workload_path = assert(arg[1], "workload path is required")
local operations = assert(tonumber(arg[2]), "operation count is required")
local warmup_calls = assert(tonumber(arg[3]), "warmup call count is required")

local setup_started = os.clock()
local workload, load_error = loadfile(workload_path)
assert(workload, load_error)
for _ = 1, warmup_calls do
    assert(type(workload(1)) == "number", "workload must return a number")
end
local setup_elapsed = os.clock() - setup_started

local started = os.clock()
local result = workload(operations)
local elapsed = os.clock() - started
assert(type(result) == "number", "workload must return a number")
local jit_enabled = type(jit) == "table" and jit.status() and 1 or 0

io.write(string.format(
    "cross_runtime_result\telapsed=%.17g\tsetup=%.17g\toperations=%d\tresult=%.17g\tjit_enabled=%d\n",
    elapsed,
    setup_elapsed,
    operations,
    result,
    jit_enabled))
