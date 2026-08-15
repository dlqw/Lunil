-- Lunil builtin Lua standard library: the `utf8` library (Lua 5.3+).
-- Readonly documentation page served as `lunil-builtin:utf8.lua`.

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

return utf8
