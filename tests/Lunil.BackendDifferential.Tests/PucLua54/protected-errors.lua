local arithmeticOk, arithmeticError = pcall(function()
    return nil + 1
end)

local callOk, callError = pcall(function()
    local value
    return value()
end)

local object = { tag = 42 }
local objectOk, objectError = pcall(function()
    error(object)
end)

local xpcallOk, xpcallError = xpcall(function()
    error("handled", 0)
end, function(message)
    return "handler:" .. message
end)

return arithmeticOk,
    arithmeticError:find("attempt to perform arithmetic on a nil value", 1, true) ~= nil,
    callOk,
    callError:find("attempt to call a nil value", 1, true) ~= nil,
    objectOk,
    type(objectError),
    objectError.tag,
    xpcallOk,
    xpcallError
