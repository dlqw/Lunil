-- Lunil builtin Lua standard library: base globals.
-- The builtin library is embedded in the language server, analyzed with the same
-- front end as user code, and served as readonly documents (`lunil-builtin:*.lua`)
-- for navigation. This page documents the Lua 5.1-5.4 common global surface.

---Writes the given values to the standard output stream.
---@param ... any Values to print.
function print(...) end

---Returns the type of its only argument as a string.
---@param v any
---@return string type `"nil"`, `"boolean"`, `"number"`, `"string"`, `"table"`, `"function"`, `"thread"`, or `"userdata"`
function type(v) end

---Iterates over table keys and values. Returns a stateful iterator triple.
---@param t table
---@return function iterator
---@return any table
---@return nil key
function pairs(t) end

---Iterates over array elements with integer keys starting at 1.
---@param t table
---@return function iterator
---@return any table
---@return integer index
function ipairs(t) end

---Converts a value to a string. Tables use `tostring` on their `__tostring` metamethod.
---@param v any
---@return string
function tostring(v) end

---Converts a string to a number, or nil when the string has no numeric representation.
---@param e string
---@param base? integer
---@return number|nil
function tonumber(e, base) end

---Calls a function in protected mode; errors are captured instead of propagating.
---@param f function
---@param ... any arguments forwarded to `f`
---@return boolean success
---@return any result Or the error message on failure
function pcall(f, ...) end

---Calls a function in protected mode with a custom error handler.
---@param f function
---@param err function error handler called before the stack unwinds
---@param ... any
---@return boolean success
---@return any result
function xpcall(f, err, ...) end

---Terminates execution with an error message. Level 1 points at the caller.
---@param message any
---@param level? integer
function error(message, level) end

---Raises an error when the first argument is false or nil; returns all arguments.
---@param v any
---@param message? any
---@return any
function assert(v, message) end

---Returns variadic arguments starting at index `n` (negative counts from the end).
---@param n integer
---@param ... any
---@return any
function select(n, ...) end

---Sets the metatable of a table and returns it.
---@param t table
---@param metatable table|nil
---@return table
function setmetatable(t, metatable) end

---Returns the metatable of a value, or nil when it has none.
---@param v any
---@return table|nil
function getmetatable(v) end

---Reads a table element without invoking metamethods.
---@param t table
---@param index any
---@return any
function rawget(t, index) end

---Writes a table element without invoking metamethods.
---@param t table
---@param index any
---@param v any
---@return table
function rawset(t, index, v) end

---Compares two values for equality without invoking metamethods.
---@param v1 any
---@param v2 any
---@return boolean
function rawequal(v1, v2) end

---Returns the length of a value without invoking the `__len` metamethod.
---@param v table|string
---@return integer
function rawlen(v) end

---Returns the first key and value of a table, or nil after the last key.
---@param t table
---@param key? any
---@return any
---@return any
function next(t, key) end

---Loads a module and returns its exported value.
---@param modname string
---@return any
function require(modname) end

---Returns the elements of a list (the Lua 5.1 `unpack` global).
---@param list table
---@param i? integer
---@param j? integer
---@return any ...
function unpack(list, i, j) end

---Collects garbage; mostly a no-op with modern collectors.
---@param opt? string
---@return any
function collectgarbage(opt) end

---Loads a chunk from a string or function and returns it, or nil plus an error.
---@param chunk string|function
---@param chunkname? string
---@param mode? string `"b"`, `"t"`, or `"bt"`
---@param env? table
---@return function|nil
---@return string|nil error
function load(chunk, chunkname, mode, env) end

---Loads and runs a Lua file.
---@param filename? string
---@return any
function dofile(filename) end

---The global environment table itself.
_G = {}

---The Lua version string, for example `"Lua 5.4"`.
_VERSION = ""

return {
  print = print,
  type = type,
  pairs = pairs,
  ipairs = ipairs,
  tostring = tostring,
  tonumber = tonumber,
  pcall = pcall,
  xpcall = xpcall,
  error = error,
  assert = assert,
  select = select,
  setmetatable = setmetatable,
  getmetatable = getmetatable,
  rawget = rawget,
  rawset = rawset,
  rawequal = rawequal,
  rawlen = rawlen,
  next = next,
  require = require,
  unpack = unpack,
  collectgarbage = collectgarbage,
  load = load,
  dofile = dofile,
  _G = _G,
  _VERSION = _VERSION,
}
