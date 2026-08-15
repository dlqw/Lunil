-- Lunil builtin Lua standard library: the `math` library.
-- Readonly documentation page served as `lunil-builtin:math.lua`.

---@class mathlib
---Standard mathematical functions and constants.
local math = {}

---The floating-point infinity value.
math.huge = 0.0

---The value of pi.
math.pi = 3.141592653589793

---Returns the largest integer smaller than or equal to x.
---@param x number
---@return integer
function math.floor(x) end

---Returns the smallest integer larger than or equal to x.
---@param x number
---@return integer
function math.ceil(x) end

---Returns the absolute value of x.
---@param x number
---@return number
function math.abs(x) end

---Returns the square root of x.
---@param x number
---@return number
function math.sqrt(x) end

---Trigonometric sine, cosine, and tangent (radians).
---@param x number
---@return number
function math.sin(x) end

---Trigonometric cosine.
---@param x number
---@return number
function math.cos(x) end

---Trigonometric tangent.
---@param x number
---@return number
function math.tan(x) end

---Arc tangent of y/x in the correct quadrant.
---@param y number
---@param x number
---@return number
function math.atan(y, x) end

---Exponentiation (e raised to the power x).
---@param x number
---@return number
function math.exp(x) end

---Natural logarithm (base-10 and base-x variants take an extra argument).
---@param x number
---@param base? number
---@return number
function math.log(x, base) end

---Returns the minimum of the arguments.
---@param x number
---@param ... number
---@return number
function math.min(x, ...) end

---Returns the maximum of the arguments.
---@param x number
---@param ... number
---@return number
function math.max(x, ...) end

---Remainder of the division of x by y, with the sign of x.
---@param x number
---@param y number
---@return number
function math.fmod(x, y) end

---Returns the integral and fractional parts of x.
---@param x number
---@return integer
---@return number
function math.modf(x) end

---Pseudo-random number: [0,1), [1,m], or [m,n] depending on arguments.
---@param m? number
---@param n? number
---@return number
function math.random(m, n) end

---Seeds the pseudo-random generator (no-op with modern generators).
---@param seed? number
function math.randomseed(seed) end

---Converts x to an integer when it has an integral value.
---@param x number
---@return integer|nil
function math.tointeger(x) end

---Returns `"integer"` or `"float"`, or nil when x is not a number.
---@param x any
---@return string|nil
function math.type(x) end

return math
