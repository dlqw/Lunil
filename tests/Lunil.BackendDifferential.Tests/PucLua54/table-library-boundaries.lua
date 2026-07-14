local source = setmetatable({}, {
    __index = function(_, key)
        return key * 10
    end,
})
local destinationValues = {}
local destination = setmetatable({}, {
    __newindex = function(_, key, value)
        destinationValues[key] = value
    end,
})

table.move(source, 1, 3, 4, destination)

local moveOk, moveError = pcall(table.move, false, 1, 1, 1)
local unpackOk, unpackError = pcall(
    table.unpack,
    {},
    math.mininteger,
    math.maxinteger)

return destinationValues[4],
    destinationValues[5],
    destinationValues[6],
    moveOk,
    moveError:find("table expected", 1, true) ~= nil,
    unpackOk,
    unpackError:find("too many results", 1, true) ~= nil
