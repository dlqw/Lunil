local closed = false
local metatable = { __close = function() closed = true end }
do
    local value <close> = setmetatable({}, metatable)
end
return closed
