local value = 0
local i = 0
while i < 10 do
    i = i + 1
    if i % 2 == 0 then
        value = value + i
    end
end
repeat
    value = value + 1
until value == 42
return value
