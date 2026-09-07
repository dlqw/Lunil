namespace Lunil.Runtime;

/// <summary>
/// Classifies the well-known Lua runtime error shapes for error forensics. Producers set
/// the kind when constructing a runtime exception so the enricher does not have to infer
/// it from message text.
/// </summary>
internal enum LuaRuntimeErrorKind : byte
{
    None,

    /// <summary>An "attempt to ..." type error (index, call, arithmetic, compare, ...).</summary>
    AttemptTo,

    /// <summary>A number-to-integer conversion failure.</summary>
    IntegerConversion,

    /// <summary>A numeric for-loop limit error.</summary>
    NumericFor,

    /// <summary>A "bad argument #N" library argument error.</summary>
    BadArgument,

    /// <summary>A failed <c>assert</c>.</summary>
    Assertion,
}
