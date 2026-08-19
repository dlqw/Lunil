-- Lunil builtin Lua standard library: the `debug` library.
-- Readonly documentation page served as `lunil-builtin:debug.lua`.

---@class debuglib
---Debugging hooks and introspection.
local debug = {}

---Returns a string with a traceback of the call stack.
---@param message? string
---@param level? integer
---@return string
function debug.traceback(message, level) end

---Returns debug information about a function or stack level.
---@param f function|integer
---@param what? string
---@return table
function debug.getinfo(f, what) end

return debug
