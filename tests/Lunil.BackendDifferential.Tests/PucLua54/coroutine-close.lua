local coroutineOne = coroutine.create(function(initial)
    local resumed = coroutine.yield(initial + 1, "pause")
    return resumed * 2
end)

local firstOk, firstValue, marker = coroutine.resume(coroutineOne, 6)
local secondOk, secondValue = coroutine.resume(coroutineOne, 21)
local deadStatus = coroutine.status(coroutineOne)
local closeDead = coroutine.close(coroutineOne)

local coroutineTwo = coroutine.create(function()
    coroutine.yield("open")
    return "unreachable"
end)
local openOk, openValue = coroutine.resume(coroutineTwo)
local closeSuspended = coroutine.close(coroutineTwo)
local closedStatus = coroutine.status(coroutineTwo)

local coroutineThree = coroutine.create(function()
    error("coroutine failure", 0)
end)
local errorOk, errorValue = coroutine.resume(coroutineThree)
local closeErrorOk, closeErrorValue = coroutine.close(coroutineThree)

return firstOk,
    firstValue,
    marker,
    secondOk,
    secondValue,
    deadStatus,
    closeDead,
    openOk,
    openValue,
    closeSuspended,
    closedStatus,
    errorOk,
    errorValue,
    closeErrorOk,
    closeErrorValue
