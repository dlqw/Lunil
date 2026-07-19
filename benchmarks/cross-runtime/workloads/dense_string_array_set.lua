local value = "item"

local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local parts = {}
    for index = 1, 1000 do
        parts[index] = value
    end
    checksum = checksum + #parts[1] + #parts[500] + #parts[1000]
end
return checksum
