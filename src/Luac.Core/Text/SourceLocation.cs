namespace Luac.Core.Text;

/// <summary>A source position with zero-based line and column values.</summary>
public readonly record struct SourceLocation(
    int ByteOffset,
    int Line,
    int ByteColumn,
    int Utf16Column);
