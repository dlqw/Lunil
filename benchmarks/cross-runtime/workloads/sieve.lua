local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local limit = 10000
    local sieve = {}
    for index = 2, limit do
        sieve[index] = true
    end
    for index = 2, math.floor(math.sqrt(limit)) do
        if sieve[index] then
            for composite = index * index, limit, index do
                sieve[composite] = false
            end
        end
    end
    local count = 0
    for index = 2, limit do
        if sieve[index] then
            count = count + 1
        end
    end
    checksum = checksum + count
end
return checksum
