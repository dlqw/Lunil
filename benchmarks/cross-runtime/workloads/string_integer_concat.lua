local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local subtotal = 0
    for index = 1, 1000 do
        subtotal = subtotal + #("item" .. (index % 100))
    end
    checksum = checksum + subtotal
end
return checksum
