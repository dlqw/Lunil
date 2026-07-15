local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local values = {}
    local total = 0
    for index = 1, 3000 do
        values[index] = index
        values.field = index
        total = total + values[index] + values.field
    end
    checksum = checksum + total
end
return checksum
