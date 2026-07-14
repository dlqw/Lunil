local trace = {}

local function hook(event, line)
    if event == "line" then
        trace[#trace + 1] = "line:" .. line
    else
        trace[#trace + 1] = event
    end
end

local function add(left, right)
    local total = left + right
    return total
end

debug.sethook(hook, "crl")
local result = add(2, 3)
debug.sethook()

return result, table.concat(trace, ",")
