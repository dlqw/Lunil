local function source(value, ...)
    local arguments = { ... }
    return value + 5, #arguments, arguments[1], arguments[2]
end

local binary = string.dump(source, true)
local loaded, message = load(binary, "puc-golden", "b")
assert(loaded, message)

local first, count, second, third = loaded(7, "left", "right")
return #binary > 0,
    binary:sub(1, 4) == "\27Lua",
    first,
    count,
    second,
    third
