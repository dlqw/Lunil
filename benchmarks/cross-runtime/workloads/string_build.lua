local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local parts = {}
    for index = 1, 1000 do
        parts[index] = "item" .. (index % 100)
    end
    checksum = checksum + #table.concat(parts, ",")
end
return checksum
