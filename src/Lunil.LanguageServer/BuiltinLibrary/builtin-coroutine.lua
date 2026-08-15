-- Lunil builtin Lua standard library: the `coroutine` library.
-- Readonly documentation page served as `lunil-builtin:coroutine.lua`.

---@class coroutinelib
local coroutine = {}

---Creates a coroutine from a function.
---@param f function
---@return thread
function coroutine.create(f) end

---Starts or continues a coroutine; values pass both ways.
---@param co thread
---@param ... any
---@return boolean success
---@return any ...
function coroutine.resume(co, ...) end

---Suspends the running coroutine; arguments pass to `resume`.
---@param ... any
---@return any ...
function coroutine.yield(...) end

---Returns `"suspended"`, `"running"`, `"normal"`, or `"dead"`.
---@param co thread
---@return string
function coroutine.status(co) end

---Wraps a function into a resumable coroutine function.
---@param f function
---@return function
function coroutine.wrap(f) end

---Returns true when the running coroutine can yield.
---@return boolean
function coroutine.isyieldable() end

---Returns the running coroutine and whether it is the main one.
---@return thread
---@return boolean
function coroutine.running() end

return coroutine
