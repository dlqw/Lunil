using Luac.Core.Text;

namespace Luac.Core.Tests.Text;

public sealed class SourceTextTests
{
    [Fact]
    public void ConstructorCopiesInputBytes()
    {
        byte[] input = [.. "return 1"u8];
        var source = new SourceText(input);

        input[0] = (byte)'x';

        Assert.Equal("return 1"u8.ToArray(), source.ToArray());
    }

    [Fact]
    public void LineMapRecognizesAllLuaLineEndings()
    {
        var source = new SourceText("a\r\nb\rc\nd"u8);

        Assert.Equal(4, source.LineCount);
        Assert.Equal(new TextSpan(0, 1), source.GetLineSpan(0));
        Assert.Equal(new TextSpan(3, 1), source.GetLineSpan(1));
        Assert.Equal(new TextSpan(5, 1), source.GetLineSpan(2));
        Assert.Equal(new TextSpan(7, 1), source.GetLineSpan(3));
    }

    [Fact]
    public void LocationTracksByteAndUtf16ColumnsIndependently()
    {
        var source = SourceText.FromUtf8("a😀b");

        var location = source.GetLocation(5);

        Assert.Equal(new SourceLocation(5, 0, 5, 3), location);
    }

    [Fact]
    public void InvalidUtf8RemainsAddressable()
    {
        var source = new SourceText([0xff, (byte)'x']);

        Assert.Equal(1, source.GetLocation(1).Utf16Column);
        Assert.Equal([0xff, (byte)'x'], source.ToArray());
    }

    [Fact]
    public void TrailingNewlineCreatesAnEmptyFinalLine()
    {
        var source = new SourceText("a\n"u8);

        Assert.Equal(2, source.LineCount);
        Assert.Equal(new TextSpan(2, 0), source.GetLineSpan(1));
    }

    [Fact]
    public void MixedLfCrSequenceIsOneLuaLineBreak()
    {
        var source = new SourceText("a\n\rb"u8);

        Assert.Equal(2, source.LineCount);
        Assert.Equal(new TextSpan(0, 1), source.GetLineSpan(0));
        Assert.Equal(new TextSpan(3, 1), source.GetLineSpan(1));
    }
}
