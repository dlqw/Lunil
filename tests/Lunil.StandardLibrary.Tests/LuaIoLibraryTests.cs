using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaIoLibraryTests
{
    [Fact]
    public void TemporaryFilesAreFinalizedUnderEveryAllocationGcStress()
    {
        var files = new MemoryFileSystem([]);
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        LuaStandardLibrary.InstallBasic(state, new LuaStandardLibraryOptions
        {
            FileSystem = files,
            Console = new MemoryConsole(),
        });
        LuaStandardLibrary.InstallIo(state);
        var lowering = LuaLowerer.Lower(LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(
            "for i=1,100 do local f=assert(io.tmpfile()); f:write(i) end " +
            "collectgarbage(); return true"))));
        Assert.Empty(lowering.Diagnostics);

        var result = new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(lowering.Module!));

        Assert.True(result.Values[0].AsBoolean());
        Assert.Equal(100, files.CloseCounts["temporary"]);
    }

    [Fact]
    public void FilesSupportAllReadFormatsWriteSeekAndLifecycle()
    {
        var files = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["input.txt"] = Encoding.ASCII.GetBytes("  0x1.fp2 rest\nsecond\nxyz"),
        });
        var values = Execute(files,
            "local f=assert(io.open('input.txt','r')); " +
            "local n,w,nl=f:read('*n',5,'*L'); local p=f:seek(); local tail=f:read('*a'); " +
            "assert(f:close()); local closed=io.type(f); " +
            "local o=assert(io.open('out.txt','w+')); assert(o:write('a',12,1.5)); " +
            "assert(o:seek('set')); local all=o:read('*a'); o:close(); " +
            "return n,w,nl,p,tail,closed,all,io.type(io.stdin),io.stdin:close()");

        Assert.Equal(7.75, values[0].AsFloat());
        Assert.Equal(" rest", values[1].AsString().ToString());
        Assert.Equal("\n", values[2].AsString().ToString());
        Assert.Equal(15, values[3].AsInteger());
        Assert.Equal("second\nxyz", values[4].AsString().ToString());
        Assert.Equal("closed file", values[5].AsString().ToString());
        Assert.Equal("a121.5", values[6].AsString().ToString());
        Assert.Equal("file", values[7].AsString().ToString());
        Assert.True(values[8].IsNil);
    }

    [Fact]
    public void LinesAutoClosesNamedFileAndFileLinesRemainOpen()
    {
        var files = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["lines.txt"] = "a\nb\n"u8.ToArray(),
        });
        var values = Execute(files,
            "local result='' local closing " +
            "for line in io.lines('lines.txt') do result=result..line end " +
            "local f=assert(io.open('lines.txt')); local it=f:lines(); " +
            "local a,b=it(),it(); local eof=it(); return result,a,b,eof,io.type(f),f:close()");

        Assert.Equal("ab", values[0].AsString().ToString());
        Assert.Equal("a", values[1].AsString().ToString());
        Assert.Equal("b", values[2].AsString().ToString());
        Assert.True(values[3].IsNil);
        Assert.Equal("file", values[4].AsString().ToString());
        Assert.True(values[5].AsBoolean());
        Assert.Equal(2, files.CloseCounts["lines.txt"]);
    }

    [Fact]
    public void ReadAndOpenValidatePucModesAndFormatArgumentNumbers()
    {
        var files = new MemoryFileSystem(new Dictionary<string, byte[]>
        {
            ["input.txt"] = [],
        });
        var values = Execute(files,
            "local f=assert(io.open('input.txt')); " +
            "local a,ae=pcall(f.read,f,1.5); local b,be=pcall(f.read,f,{}); " +
            "local c,ce=pcall(f.read,f,'z'); local d,de=pcall(io.open,'input.txt','rb+'); " +
            "f:close(); return a,ae,b,be,c,ce,d,de");

        Assert.False(values[0].AsBoolean());
        Assert.Contains("bad argument #1", values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.Contains("string expected, got table", values[3].AsString().ToString());
        Assert.False(values[4].AsBoolean());
        Assert.Contains("invalid format", values[5].AsString().ToString());
        Assert.False(values[6].AsBoolean());
        Assert.Contains("invalid mode", values[7].AsString().ToString());
    }

    private static LuaValue[] Execute(MemoryFileSystem files, string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, new LuaStandardLibraryOptions
        {
            FileSystem = files,
            Console = new MemoryConsole(),
        });
        LuaStandardLibrary.InstallIo(state);
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values.ToArray();
    }

    private sealed class MemoryFileSystem(Dictionary<string, byte[]> files) : ILuaFileSystem
    {
        public Dictionary<string, int> CloseCounts { get; } = [];

        public byte[] ReadAllBytes(string path) => files[path].ToArray();

        public bool FileExists(string path) => files.ContainsKey(path);

        public Stream Open(string path, LuaFileMode mode)
        {
            if (mode is LuaFileMode.Read or LuaFileMode.ReadUpdate && !files.ContainsKey(path))
            {
                throw new FileNotFoundException(path);
            }

            var initial = mode is LuaFileMode.Write or LuaFileMode.WriteUpdate
                ? []
                : files.GetValueOrDefault(path, []);
            return new CommitStream(initial, bytes =>
            {
                files[path] = bytes;
                CloseCounts[path] = CloseCounts.GetValueOrDefault(path) + 1;
            }, mode is LuaFileMode.Append or LuaFileMode.AppendUpdate);
        }

        public Stream OpenTemporary(out string? path)
        {
            path = "temporary";
            return Open(path, LuaFileMode.WriteUpdate);
        }
    }

    private sealed class CommitStream : MemoryStream
    {
        private readonly Action<byte[]> _commit;

        public CommitStream(byte[] initial, Action<byte[]> commit, bool append)
        {
            _commit = commit;
            Write(initial);
            Position = append ? Length : 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _commit(ToArray());
            }

            base.Dispose(disposing);
        }
    }

    private sealed class MemoryConsole : ILuaConsole
    {
        public byte[] ReadStandardInput() => [];
        public void Write(ReadOnlyMemory<byte> bytes) { }
        public void WriteLine() { }
    }
}
