-- Generated with the unmodified PUC Lua 5.3.6 compiler.
local function iterator(limit, control)
    control = control + 1
    if control <= limit then
        return control, control * 2
    end
end

local sum = 0
for key, value in iterator, 4, 0 do
    sum = sum + key + value
end

local function collect(prefix, ...)
    local values = {...}
    return prefix .. values[1] .. values[2], #values
end

local text, count = collect("x", "a", "b")
local values = {10, 20, 30, key = 7}
local function outer(value)
    return function(delta)
        value = value + delta
        return value
    end
end

local nextValue = outer(40)
return sum, text, count, values[2], values.key, #values,
    nextValue(1), nextValue(1), "12" | 3, "12" + 3
