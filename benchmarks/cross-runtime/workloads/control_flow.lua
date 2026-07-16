local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local total = 0
    local index = 0
    while index < 5000 do
        if index % 2 == 0 then
            total = total + index
        else
            total = total - 1
        end
        index = index + 1
    end
    repeat
        total = total + 1
    until total > 6247501
    checksum = checksum + total
end
return checksum
