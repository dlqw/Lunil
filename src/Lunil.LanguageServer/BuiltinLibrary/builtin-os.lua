-- Lunil builtin Lua standard library: the `os` library.
-- Readonly documentation page served as `lunil-builtin:os.lua`.

---@class oslib
---Operating system facilities: time, clock, environment, and files.
local os = {}

---Returns the current calendar time, or formats a time table into a number.
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

return os
