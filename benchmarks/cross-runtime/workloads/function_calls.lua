local function add(left, right)
    return left + right
end

local function values(first, second, third)
    return first, second, third
end

local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local total = 0
    for index = 1, 2000 do
        local first, second, third = values(index, index + 1, index + 2)
        total = total + add(first, second) + third
    end
    checksum = checksum + total
end
return checksum
