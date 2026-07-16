local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local sum = 0
    for index = 1, 10000 do
        sum = sum + index
    end
    checksum = checksum + sum
end
return checksum
