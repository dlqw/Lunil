-- Lunil builtin Lua standard library: the `table` library.
-- Readonly documentation page served as `lunil-builtin:table.lua`.

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

---Returns the elements of a list as variadic results.
---@param list table
---@param i? integer
---@param j? integer
---@return any ...
function table.unpack(list, i, j) end

return table
