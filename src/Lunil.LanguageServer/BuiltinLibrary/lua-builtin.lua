-- Lunil builtin Lua standard library definitions.
-- This file documents the Lua 5.1-5.4 common surface; it is embedded in the language
-- server, analyzed with the same front end as user code, and served as a readonly
-- document for navigation.

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

---Returns the elements of a list (Lua 5.1 `unpack`).
---@param list table
---@param i? integer
---@param j? integer
---@return any ...
function table.unpack(list, i, j) end

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

---@class stringlib
local string = {}

---Formats values under format directives similar to C `printf`.
---@param s string
---@param ... any
---@return string
function string.format(s, ...) end

---Returns the substring between positions i and j (negative counts from the end).
---@param s string
---@param i? integer
---@param j? integer
---@return string
function string.sub(s, i, j) end

---Returns the length of the string.
---@param s string
---@return integer
function string.len(s) end

---Returns the numeric codes of the characters between positions i and j.
---@param s string
---@param i? integer
---@param j? integer
---@return integer ...
function string.byte(s, i, j) end

---Returns a string with the characters of the given numeric codes.
---@param ... integer
---@return string
function string.char(...) end

---Returns a string repeated n times.
---@param s string
---@param n integer
---@return string
function string.rep(s, n) end

---Finds the first match of a pattern; returns indices (plain find) or captures.
---@param s string
---@param pattern string
---@param init? integer
---@param plain? boolean
---@return integer|nil start
---@return integer|nil endIndex
---@return string ... captures
function string.find(s, pattern, init, plain) end

---Returns the first match of a pattern, or nil.
---@param s string
---@param pattern string
---@param init? integer
---@return string|nil
---@return string ... captures
function string.match(s, pattern, init) end

---Returns an iterator over all matches of a pattern.
---@param s string
---@param pattern string
---@param init? integer
---@return function
function string.gmatch(s, pattern, init) end

---Substitutes pattern matches with a replacement string, function, or table.
---@param s string
---@param pattern string
---@param repl string|table|function
---@param n? integer
---@return string
---@return integer replacements
function string.gsub(s, pattern, repl, n) end

---Returns the upper-case version of the string.
---@param s string
---@return string
function string.upper(s) end

---Returns the lower-case version of the string.
---@param s string
---@return string
function string.lower(s) end

---Returns the reversed string.
---@param s string
---@return string
function string.reverse(s) end

---@class tablelib
local table = {}

---Appends a value to the end of an array part.
---@param list table
---@param value any
function table.insert(list, value) end

---Inserts a value at position pos, shifting elements up.
---@param list table
---@param pos integer
---@param value any
function table.insert(list, pos, value) end

---Removes and returns the element at pos (the last by default).
---@param list table
---@param pos? integer
---@return any
function table.remove(list, pos) end

---Concatenates array elements with a separator into a string.
---@param list table
---@param sep? string
---@param i? integer
---@param j? integer
---@return string
function table.concat(list, sep, i, j) end

---Sorts the list in place, optionally with a comparator.
---@param list table
---@param comp? fun(a: any, b: any): boolean
function table.sort(list, comp) end

---Returns a table packing its arguments, with an `n` field.
---@param ... any
---@return table
function table.pack(...) end

---Moves elements between array positions; returns the destination list.
---@param a1 table
---@param f integer
---@param e integer
---@param t? integer
---@param a2? table
---@return table
function table.move(a1, f, e, t, a2) end

---@class mathlib
local math = {}

---The floating-point infinity value.
math.huge = 0.0

---The value of pi.
math.pi = 3.141592653589793

---Returns the largest integer smaller than or equal to x.
---@param x number
---@return integer
function math.floor(x) end

---Returns the smallest integer larger than or equal to x.
---@param x number
---@return integer
function math.ceil(x) end

---Returns the absolute value of x.
---@param x number
---@return number
function math.abs(x) end

---Returns the square root of x.
---@param x number
---@return number
function math.sqrt(x) end

---Trigonometric sine, cosine, and tangent (radians).
---@param x number
---@return number
function math.sin(x) end

---Trigonometric cosine.
---@param x number
---@return number
function math.cos(x) end

---Trigonometric tangent.
---@param x number
---@return number
function math.tan(x) end

---Arc tangent of y/x in the correct quadrant.
---@param y number
---@param x number
---@return number
function math.atan(y, x) end

---Exponentiation and natural logarithm.
---@param x number
---@return number
function math.exp(x) end

---Natural logarithm (base-10 and base-x variants take an extra argument).
---@param x number
---@param base? number
---@return number
function math.log(x, base) end

---Returns the minimum of the arguments.
---@param x number
---@param ... number
---@return number
function math.min(x, ...) end

---Returns the maximum of the arguments.
---@param x number
---@param ... number
---@return number
function math.max(x, ...) end

---Remainder of the division of x by y, with the sign of x.
---@param x number
---@param y number
---@return number
function math.fmod(x, y) end

---Returns the integral and fractional parts of x.
---@param x number
---@return integer
---@return number
function math.modf(x) end

---Pseudo-random number: [0,1), [1,m], or [m,n] depending on arguments.
---@param m? number
---@param n? number
---@return number
function math.random(m, n) end

---Seeds the pseudo-random generator (no-op with modern generators).
---@param seed? number
function math.randomseed(seed) end

---Converts x to an integer when it has an integral value.
---@param x number
---@return integer|nil
function math.tointeger(x) end

---Returns `"integer"` or `"float"`, or nil when x is not a number.
---@param x any
---@return string|nil
function math.type(x) end

---@class oslib
local os = {}

---Returns the current calendar time, or formats a time table into a string.
---@param date? string
---@param time? table
---@return integer|string
function os.time(date, time) end

---Returns the CPU time used by the program in seconds.
---@return number
function os.clock() end

---Formats a timestamp (or the current time) according to a date pattern.
---@param format? string
---@param time? number|table
---@return string
---@return string|nil formatted table used
function os.date(format, time) end

---Returns the value of an environment variable.
---@param varname string
---@return string|nil
function os.getenv(varname) end

---Removes a file or empty directory; returns nil plus an error on failure.
---@param filename string
---@return boolean|nil
---@return string|nil error
function os.remove(filename) end

---Renames a file or directory; returns nil plus an error on failure.
---@param oldname string
---@param newname string
---@return boolean|nil
---@return string|nil error
function os.rename(oldname, newname) end

---Terminates the program with an exit status.
---@param code? boolean|integer
function os.exit(code) end

---Sets the current locale; returns nil when the request fails.
---@param locale? string
---@param category? string
---@return string|nil
function os.setlocale(locale, category) end

---@class iolib
local io = {}

---Writes values to the default output file.
---@param ... any
---@return iolib
function io.write(...) end

---Reads from the default input file.
---@param format? string
---@return any
---@return any
function io.read(format) end

---Opens a file; returns a file handle or nil plus an error.
---@param filename string
---@param mode? string
---@return table|nil
---@return string|nil error
function io.open(filename, mode) end

---Iterates over the lines of a file.
---@param filename? string
---@return function
function io.lines(filename) end

---Closes the default output file.
function io.close() end

---Flushes the default output file.
function io.flush() end

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

---@class utf8lib
local utf8 = {}

---Converts code points to a UTF-8 string.
---@param ... integer
---@return string
function utf8.char(...) end

---Returns the code points between two byte positions.
---@param s string
---@param i? integer
---@param j? integer
---@return integer ...
function utf8.codepoint(s, i, j) end

---Returns the number of UTF-8 characters, or the byte offset of the n-th character.
---@param s string
---@param i? integer
---@param j? integer
function utf8.len(s, i, j) end

---Returns the byte offset of the n-th character.
---@param s string
---@param n? integer
---@param i? integer
---@return integer
function utf8.offset(s, n, i) end

---@class debuglib
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
  unpack = table.unpack,
  collectgarbage = collectgarbage,
  load = load,
  dofile = dofile,
  _G = _G,
  _VERSION = _VERSION,
  string = string,
  table = table,
  math = math,
  os = os,
  io = io,
  coroutine = coroutine,
  utf8 = utf8,
  debug = debug,
}
