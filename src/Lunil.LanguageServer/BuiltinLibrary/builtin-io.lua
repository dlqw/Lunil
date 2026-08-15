-- Lunil builtin Lua standard library: the `io` library.
-- Readonly documentation page served as `lunil-builtin:io.lua`.

---@class iolib
---Basic input and output with the default file handles.
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
---Modes: `"r"` (read), `"w"` (write), `"a"` (append), optionally followed by
---`"b"` (binary) or `+` (update).
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

return io
