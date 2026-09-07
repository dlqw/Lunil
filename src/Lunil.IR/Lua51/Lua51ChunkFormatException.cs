using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public sealed class Lua51ChunkFormatException(string reason, int offset = 0)
    : LuaChunkFormatException("Lua 5.1", reason, offset);
