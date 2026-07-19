local parts = rawget(_G, "__lunil_table_concat_string_parts")
if parts == nil then
    parts = {}
    for index = 1, 1000 do
        parts[index] = "item" .. (index % 100)
    end
    rawset(_G, "__lunil_table_concat_string_parts", parts)
end

local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    checksum = checksum + #table.concat(parts, ",")
end
return checksum
