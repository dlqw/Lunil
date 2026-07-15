local function escapes(cx, cy)
    local zx, zy = 0.0, 0.0
    local iteration = 0
    while zx * zx + zy * zy <= 4.0 and iteration < 50 do
        local next_zx = zx * zx - zy * zy + cx
        zy = 2.0 * zx * zy + cy
        zx = next_zx
        iteration = iteration + 1
    end
    return iteration < 50
end

local operations = ... or 1
local checksum = 0
for _ = 1, operations do
    local escaped = 0
    for y = -24, 23 do
        for x = -24, 23 do
            if escapes(x / 16.0, y / 16.0) then
                escaped = escaped + 1
            end
        end
    end
    checksum = checksum + escaped
end
return checksum
