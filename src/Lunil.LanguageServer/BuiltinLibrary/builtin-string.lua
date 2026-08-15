-- Lunil builtin Lua standard library: the `string` library.
-- Readonly documentation page served as `lunil-builtin:string.lua`.

---@class stringlib
---Pattern matching and string manipulation.
local string = {}

---Formats values under format directives similar to C `printf`.
---Directives include `%s`, `%d`, `%x`, `%f`, and `%%`; `%s` accepts any value
---convertible with `tostring`.
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
---Pass `plain = true` for a literal substring search.
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

return string
