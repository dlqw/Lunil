local minimum = math.mininteger
local maximum = math.maxinteger

return minimum,
    maximum,
    maximum + 1,
    minimum - 1,
    -1 >> 1,
    1 << 63,
    1 << 64,
    maximum // -1,
    minimum // -1,
    minimum % -1
