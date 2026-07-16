local function fib(count)
    local first, second = 0, 1
    for _ = 1, count do
        first, second = second, first + second
    end
    return first
end

local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local total = 0
    for index = 1, 100 do
        total = total + fib(index % 30)
    end
    checksum = checksum + total
end
return checksum
