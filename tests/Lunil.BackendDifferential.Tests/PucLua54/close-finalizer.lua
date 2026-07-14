local closed = {}
local finalized = 0
local metatable = {
    __close = function(value, errorValue)
        closed[#closed + 1] = value.name .. ":" .. type(errorValue)
    end,
    __gc = function()
        finalized = finalized + 1
    end,
}

local function run()
    local first <close> = setmetatable({ name = "first" }, metatable)
    local second <close> = setmetatable({ name = "second" }, metatable)
    error("boom", 0)
end

local ok, message = pcall(run)
local closeOrder = table.concat(closed, ",")
collectgarbage("collect")
collectgarbage("collect")

return ok, message, closeOrder, finalized
