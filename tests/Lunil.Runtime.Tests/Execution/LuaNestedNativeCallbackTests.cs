using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaNestedNativeCallbackTests
{
    private static LuaNativeByteBuffer? _capturedForeignBuffer;
    private static bool _reuseCapturedBuffer;

    [Fact]
    public void ResumableNativeCallbackCrossesGeneratedLuaTrampolineAndCanYield()
    {
        var state = new LuaState();
        var callback = new LuaNativeFunction("callback", CallbackStep);
        var outerDescriptor = new LuaNativeFunction("outer", OuterStep);
        var outer = state.CreateNativeClosure(
            outerDescriptor,
            [LuaValue.FromFunction(callback)]);
        var thread = state.CreateThread(LuaValue.FromFunction(outer));
        var interpreter = new LuaInterpreter();

        var yielded = interpreter.Start(state, thread);
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(42)]);

        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal([LuaValue.FromInteger(41)], yielded.Values.ToArray());
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal([LuaValue.FromInteger(42)], completed.Values.ToArray());
    }

    [Fact]
    public void ProtectedNativeCallbackConvertsLuaErrorsAtTheRootBoundary()
    {
        var state = new LuaState();
        var callback = Compile(state, "local value=nil; return value()");
        var outer = state.CreateNativeClosure(
            new LuaNativeFunction("protected outer", ProtectedOuterStep),
            [LuaValue.FromFunction(callback)]);
        var thread = state.CreateThread(LuaValue.FromFunction(outer));

        var completed = new LuaInterpreter().Start(state, thread);

        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.False(completed.Values[0].AsBoolean());
        Assert.Contains(
            "attempt to call a nil value",
            completed.Values[1].AsString().ToString());
    }

    [Fact]
    public void ByteOnlyContinuationStateSurvivesLuaCallbacksFullGcAndYield()
    {
        var state = new LuaState();
        state.SetGlobal(
            "forceGc",
            LuaValue.FromFunction(new LuaNativeFunction(
                "force gc",
                static (current, _) =>
                {
                    current.Heap.CollectFull();
                    return [];
                })));
        var callback = Compile(state, "forceGc(); return 'tail'");
        var sentinel = LuaValue.FromString(state.Strings.GetOrCreate(
            "a logically collected value retained only by native state"u8));
        var outer = state.CreateNativeClosure(
            new LuaNativeFunction("buffer callback", BufferCallbackStep),
            [LuaValue.FromFunction(callback), sentinel]);

        var callbackResult = new LuaInterpreter().Start(
            state,
            state.CreateThread(LuaValue.FromFunction(outer)));

        Assert.Equal(LuaVmSignal.Completed, callbackResult.Signal);
        Assert.Equal("headtail", callbackResult.Values[0].AsString().ToString());
        Assert.Equal(sentinel, callbackResult.Values[1]);
        Assert.True(sentinel.AsString().IsAlive);

        var yielding = state.CreateNativeClosure(
            new LuaNativeFunction("buffer yield", BufferYieldStep));
        var yieldingThread = state.CreateThread(LuaValue.FromFunction(yielding));
        var interpreter = new LuaInterpreter();
        var yielded = interpreter.Start(state, yieldingThread);
        var resumed = interpreter.Resume(
            state,
            yieldingThread,
            [LuaValue.FromString(state.Strings.GetOrCreate("after"u8))]);

        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal(7, Assert.Single(yielded.Values).AsInteger());
        Assert.Equal(LuaVmSignal.Completed, resumed.Signal);
        Assert.Equal("beforeafter", Assert.Single(resumed.Values).AsString().ToString());
    }

    [Fact]
    public void NativeByteBufferRejectsCrossStateContinuationOwnership()
    {
        _capturedForeignBuffer = null;
        _reuseCapturedBuffer = false;
        try
        {
            var descriptor = new LuaNativeFunction("buffer owner", BufferOwnershipStep);
            var first = new LuaState();
            var captured = new LuaInterpreter().Start(
                first,
                first.CreateThread(LuaValue.FromFunction(
                    first.CreateNativeClosure(descriptor))));
            Assert.Equal(LuaVmSignal.Completed, captured.Signal);
            Assert.NotNull(_capturedForeignBuffer);

            _reuseCapturedBuffer = true;
            var second = new LuaState();
            var exception = Assert.Throws<LuaRuntimeException>(() =>
                new LuaInterpreter().Start(
                    second,
                    second.CreateThread(LuaValue.FromFunction(
                        second.CreateNativeClosure(descriptor)))));
            Assert.Contains(
                "different LuaState instances",
                exception.Message);
        }
        finally
        {
            _reuseCapturedBuffer = false;
            _capturedForeignBuffer = null;
        }
    }

    [Fact]
    public void NativeByteBufferTransfersPooledLongStringsAndPreservesShortInterning()
    {
        var state = new LuaState();
        var bytes = Enumerable.Range(0, 1_024)
            .Select(static index => (byte)(index * 31))
            .ToArray();
        var buffer = new LuaNativeByteBuffer(state.Heap, initialCapacity: 0);
        buffer.ReserveCapacityHint(bytes.Length);
        buffer.AppendPair(bytes.AsSpan(0, 700), bytes.AsSpan(700));

        var text = buffer.MoveToString(state.Strings);

        Assert.Equal(bytes.Length, text.Length);
        Assert.Equal(bytes, text.ToArray());
        Assert.Equal(
            state.Strings.GetOrCreate(bytes),
            text);
        state.Heap.CollectFull();
        Assert.False(text.IsAlive);

        var interned = state.Strings.GetOrCreate("short"u8);
        var shortBuffer = new LuaNativeByteBuffer(state.Heap, initialCapacity: 0);
        shortBuffer.Append("short"u8);
        Assert.Same(interned, shortBuffer.MoveToString(state.Strings));
    }

    private static LuaNativeStep CallbackStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.Yielded(
                [values[0]],
                continuationId: 1,
                stateValues: [values[0]])
            : LuaNativeStep.Completed(values.Length == 0
                ? context.InvocationState[0]
                : values[0]);

    private static LuaNativeStep OuterStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.CallLua(
                context.Captures[0],
                [LuaValue.FromInteger(41)],
                continuationId: 1)
            : LuaNativeStep.Completed(values.Length == 0 ? LuaValue.Nil : values[0]);

    private static LuaNativeStep ProtectedOuterStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values) => continuation == 0
            ? LuaNativeStep.CallLua(
                context.Captures[0],
                [],
                continuationId: 1,
                callIsProtected: true)
            : LuaNativeStep.Completed(values.ToArray());

    private static LuaNativeStep BufferCallbackStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuation == 0)
        {
            var buffer = context.CreateByteBuffer();
            buffer.Append("head"u8);
            return LuaNativeStep.CallLuaWithByteBuffer(
                context.Captures[0],
                [],
                continuationId: 1,
                stateValues: [context.Captures[1]],
                callIsYieldable: true,
                byteBuffer: buffer);
        }

        var resumed = context.ByteBuffer ??
            throw new InvalidOperationException("Native byte buffer was not resumed.");
        resumed.Append(values[0].AsString().AsSpan());
        return LuaNativeStep.Completed(
            LuaValue.FromString(context.State.Strings.GetOrCreate(resumed.WrittenSpan)),
            context.InvocationState[0]);
    }

    private static LuaNativeStep BufferYieldStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuation == 0)
        {
            var buffer = context.CreateByteBuffer();
            buffer.Append("before"u8);
            return LuaNativeStep.YieldedWithByteBuffer(
                [LuaValue.FromInteger(7)],
                continuationId: 1,
                stateValues: [],
                byteBuffer: buffer);
        }

        var resumed = context.ByteBuffer ??
            throw new InvalidOperationException("Native byte buffer was not resumed.");
        resumed.Append(values[0].AsString().AsSpan());
        return LuaNativeStep.Completed(LuaValue.FromString(
            context.State.Strings.GetOrCreate(resumed.WrittenSpan)));
    }

    private static LuaNativeStep BufferOwnershipStep(
        LuaNativeCallContext context,
        int continuation,
        ReadOnlySpan<LuaValue> values)
    {
        if (!_reuseCapturedBuffer)
        {
            _capturedForeignBuffer = context.CreateByteBuffer();
            return LuaNativeStep.Completed();
        }

        return LuaNativeStep.CallLuaWithByteBuffer(
            LuaValue.FromFunction(new LuaNativeFunction(
                "noop",
                static (_, _) => [])),
            [],
            continuationId: 1,
            stateValues: [],
            callIsYieldable: true,
            byteBuffer: _capturedForeignBuffer!);
    }

    private static LuaClosure Compile(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return state.CreateMainClosure(lowering.Module!);
    }
}
