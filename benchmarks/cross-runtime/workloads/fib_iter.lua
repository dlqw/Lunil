local function fib(count)
    local first = 0
    local second = 1
    for _ = 1, count do
        local nextValue = first + second
        first = second
        second = nextValue
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
