using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunil.Analysis;

namespace Lunil.LanguageServer.Tests;

public sealed class LanguageServerTests
{
    [Fact]
    public void BuiltinLibraryServesPerLibraryPages()
    {
        var library = BuiltinLibrary.Value;

        Assert.True(library.TryGetDocument("math", out var math));
        Assert.Equal("lunil-builtin:math.lua", math.Uri);
        Assert.StartsWith("-- Lunil builtin Lua standard library: the `math` library.",
            math.Source, StringComparison.Ordinal);

        // Members resolve to the page that defines them, with docs attached.
        Assert.True(library.TryGetMemberLocation("math.max", out var maxPage, out var maxSpan));
        Assert.Equal("math", maxPage.Name);
        Assert.True(maxPage.ToPosition(maxSpan).Line > 0);
        Assert.Contains("Returns the maximum of the arguments",
            maxPage.Docs["math.max"], StringComparison.Ordinal);

        // Globals span pages: stdlib tables and base global functions.
        Assert.True(library.Globals.ContainsKey("math"));
        Assert.True(library.Globals.ContainsKey("string"));
        Assert.True(library.Globals.ContainsKey("print"));
        Assert.True(library.TryGetMemberLocation("print", out var basePage, out _));
        Assert.Equal("base", basePage.Name);
    }

    [Fact]
    public async Task LibraryFoldersTypeHostInjectedGlobals()
    {
        var metaRoot = Path.Combine(Path.GetTempPath(), "lunil-library-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(metaRoot);
        await File.WriteAllTextAsync(Path.Combine(metaRoot, "game_api.lua"), string.Join("\n",
        [
            "---@meta",
            "---@class Vector",
            "---@field x number",
            "---@field y number",
            "Game = {}",
            "---@param v Vector",
            "---@return number",
            "function Game.sum(v) return v.x + v.y end",
        ]));
        try
        {
            var folder = new Uri("file:///src/");
            using var workspace = new LanguageServerWorkspace();
            workspace.Initialize([folder]);
            workspace.ConfigureLibraryFolders([metaRoot]);
            await WaitForAsync(() => workspace.GetDocuments().Any(document =>
                document.Uri.AbsoluteUri.EndsWith("game_api.lua", StringComparison.Ordinal)));

            var appUri = new Uri("file:///src/app.lua");
            workspace.Open(appUri, 1, "local total = Game.sum({ x = 1, y = 2 })\nreturn total");
            await workspace.ReindexNowAsync(CancellationToken.None);
            var service = new LuaLanguageService(workspace);

            // The host-injected global's member keeps its declared signature instead
            // of degrading to `any`: the analysis chain is healed by the stub folder.
            var sumHover = await service.HoverAsync(Element(new
            {
                textDocument = new { uri = appUri.AbsoluteUri },
                position = new { line = 0, character = 20 },
            }), CancellationToken.None);
            var sumValue = sumHover!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("sum(v: Vector)", sumValue, StringComparison.Ordinal);
            Assert.Contains(": number", sumValue, StringComparison.Ordinal);

            // The receiver hover keeps the declared table shape instead of `unknown`.
            var gameHover = await service.HoverAsync(Element(new
            {
                textDocument = new { uri = appUri.AbsoluteUri },
                position = new { line = 0, character = 14 },
            }), CancellationToken.None);
            var gameValue = gameHover!["contents"]!["value"]!.GetValue<string>();
            Assert.Contains("Game: {", gameValue, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(metaRoot, recursive: true);
        }
    }

    [Fact]
    public async Task BuiltinLibraryReceiverHoverIsCompact()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///scratch.lua");
        workspace.Open(uri, 1, "local m = math.max(1, 4)\nreturn m");
        var service = new LuaLanguageService(workspace);

        // Hovering the `math` receiver shows a compact library card: member count,
        // doc comment, page link — never the full structural table dump.
        var receiverHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 11 },
        }), CancellationToken.None);
        var receiverValue = receiverHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("```lua\nmath\n```", receiverValue, StringComparison.Ordinal);
        Assert.Contains("members", receiverValue, StringComparison.Ordinal);
        Assert.Contains("Standard mathematical functions", receiverValue, StringComparison.Ordinal);
        Assert.Contains("command:lunil._openBuiltinLocation", receiverValue, StringComparison.Ordinal);
        Assert.DoesNotContain("huge: 0", receiverValue, StringComparison.Ordinal);
        Assert.DoesNotContain("floor: fun(", receiverValue, StringComparison.Ordinal);

        // `print` (a function global) keeps its signature form.
        var printHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 1 },
        }), CancellationToken.None);
        Assert.Null(printHover);
    }

    [Fact]
    public async Task PrimitiveAnnotationTypesHoverWithDescription()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///scratch.lua");
        workspace.Open(uri, 1, "---@param count number\nlocal function add(count) return count end\nreturn add");
        var service = new LuaLanguageService(workspace);

        var hover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 20 },
        }), CancellationToken.None);
        var value = hover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("```lua\nnumber\n```", value, StringComparison.Ordinal);
        Assert.Contains("Lua number", value, StringComparison.Ordinal);

        service.Localization.Locale = LunilLocale.SimplifiedChinese;
        var chinese = (await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 20 },
        }), CancellationToken.None))!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("64 位浮点", chinese, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HoverSignaturesLinkNamedTypesAndSummarizeLargeTables()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var vecUri = new Uri("file:///src/vec.lua");
        var utilUri = new Uri("file:///src/util.lua");
        workspace.Open(vecUri, 1,
            "---@class Vec\n" +
            "---@field x number\n" +
            "---@field y number\n" +
            "local Vec = {}\n" +
            "function Vec.new(x, y) return setmetatable({}, Vec) end\n" +
            "return Vec");
        workspace.Open(utilUri, 1,
            "local Vec = require(\"vec\")\n" +
            "---@param a Vec\n" +
            "---@return Vec\n" +
            "local function flip(a) return Vec.new(-a.x, -a.y) end\n" +
            "local config = { width = 1, height = 2, depth = 3, scale = 4, bias = 5, alpha = 6 }\n" +
            "return { flip = flip, config = config }");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // A member signature links the workspace class names it mentions below the fence.
        var flipHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = utilUri.AbsoluteUri },
            position = new { line = 3, character = 18 },
        }), CancellationToken.None);
        var flipValue = flipHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("flip: fun(a: Vec): Vec", flipValue, StringComparison.Ordinal);
        Assert.Contains("**Types** [Vec](command:lunil._openLocation", flipValue, StringComparison.Ordinal);

        // Large structural tables summarize as `table` instead of dumping every field.
        var configHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = utilUri.AbsoluteUri },
            position = new { line = 4, character = 8 },
        }), CancellationToken.None);
        var configValue = configHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("config: {width, height, depth, …}", configValue, StringComparison.Ordinal);
        Assert.Contains("**Members (6)**", configValue, StringComparison.Ordinal);
        Assert.Contains("width: 1", configValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeExtendEdgesCarryInheritedMembers()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var classUri = new Uri("file:///src/classlib.lua");
        var midUri = new Uri("file:///src/mid.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(classUri, 1,
            "---@class Class\n" +
            "local Class = {}\n" +
            "function Class:new(...) return setmetatable({}, self) end\n" +
            "return Class");
        // `---@class Mid` declares no base; the runtime `Class:extend` edge must carry
        // `new` through to Mid and its subclass anyway.
        workspace.Open(midUri, 1,
            "local Class = require(\"classlib\")\n" +
            "---@class Mid\n" +
            "local Mid = Class:extend(\"Mid\", {})\n" +
            "return Mid");
        workspace.Open(appUri, 1,
            "local Mid = require(\"mid\")\n" +
            "local instance = Mid:new()\n" +
            "return instance");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var newDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 23 },
        }), false, CancellationToken.None);
        Assert.NotNull(newDefinition);
        Assert.Equal(classUri.AbsoluteUri, newDefinition!["uri"]!.GetValue<string>());

        var newHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 23 },
        }), CancellationToken.None);
        Assert.Contains("new(", newHover!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        // The class card lists the runtime base group and extends row.
        var midHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = midUri.AbsoluteUri },
            position = new { line = 2, character = 6 },
        }), CancellationToken.None);
        var midValue = midHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("Inherited from Class", midValue, StringComparison.Ordinal);
        Assert.Contains("| Extends | [Class](", midValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequiredModuleTypesFlowThroughConstructorsArraysAndLoops()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var classUri = new Uri("file:///src/classlib.lua");
        var subUri = new Uri("file:///src/sub.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(classUri, 1,
            "---@class Class\n" +
            "local Class = {}\n" +
            "Class.__index = Class\n" +
            "function Class:new(...) return setmetatable({}, self) end\n" +
            "return Class");
        workspace.Open(subUri, 1,
            "local Class = require(\"classlib\")\n" +
            "---@class Sub\n" +
            "local Sub = Class:extend(\"Sub\", {})\n" +
            "function Sub:configure() end\n" +
            "return Sub");
        workspace.Open(appUri, 1,
            "local Sub = require(\"sub\")\n" +
            "local systems = { Sub:new(), Sub:new() }\n" +
            "for _, system in ipairs(systems) do system:configure() end\n" +
            "systems[2]:configure()\n" +
            "return systems");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);
        var hoverAt = async (int line, int character) => (await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line, character },
        }), CancellationToken.None))?["contents"]!["value"]!.GetValue<string>();
        var definitionAt = async (int line, int character) => (await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line, character },
        }), false, CancellationToken.None));

        // The require alias carries the exported class type through `new`; loop
        // variables over instances hover with their class card.
        var loopVariable = await hoverAt(2, 7);
        Assert.Contains("class Sub", loopVariable, StringComparison.Ordinal);
        Assert.Contains("[configure]", loopVariable, StringComparison.Ordinal);

        // Loop variables over constructed arrays resolve members (hover + definition).
        Assert.Contains("configure()", await hoverAt(2, 45), StringComparison.Ordinal);
        var loopDefinition = await definitionAt(2, 45);
        Assert.Equal(subUri.AbsoluteUri, loopDefinition!["uri"]!.GetValue<string>());

        // Indexed elements (`systems[2]`) resolve members the same way.
        Assert.Contains("configure()", await hoverAt(3, 13), StringComparison.Ordinal);
        var indexDefinition = await definitionAt(3, 13);
        Assert.Equal(subUri.AbsoluteUri, indexDefinition!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task FunctionScopedSystemArraysResolveMembersThroughLoops()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        workspace.Open(new Uri("file:///src/class.lua"), 1, string.Join("\n",
        [
            "---@class Class",
            "local Class = {}",
            "Class.__index = Class",
            "---@param name string",
            "---@param fields table",
            "---@return Class",
            "function Class:extend(name, fields)",
            "  local Sub = setmetatable({}, Class)",
            "  return Sub",
            "end",
            "function Class:new(...)",
            "  local instance = setmetatable({}, self)",
            "  if instance.init then instance:init(...) end",
            "  return instance",
            "end",
            "return Class",
        ]));
        workspace.Open(new Uri("file:///src/system.lua"), 1, string.Join("\n",
        [
            "local Class = require(\"class\")",
            "---@class System",
            "local System = Class:extend(\"System\", {})",
            "function System:configure() end",
            "function System:constrain() end",
            "return System",
        ]));
        workspace.Open(new Uri("file:///src/spawn.lua"), 1, string.Join("\n",
        [
            "local System = require(\"system\")",
            "---@class SpawnSystem : System",
            "local SpawnSystem = System:extend(\"SpawnSystem\", {})",
            "function SpawnSystem:init() end",
            "return SpawnSystem",
        ]));
        var appUri = new Uri("file:///src/main.lua");
        workspace.Open(appUri, 1, string.Join("\n",
        [
            "local SpawnSystem = require(\"spawn\")",
            "local System = require(\"system\")",
            "local function bootstrap()",
            "  local systems = {",
            "    SpawnSystem:new(),",
            "    System:new(),",
            "  }",
            "  for _, system in ipairs(systems) do",
            "    system:configure()",
            "  end",
            "  systems[2]:constrain()",
            "end",
            "return bootstrap()",
        ]));
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // The generic library constructor's instance resolves over the SUBCLASS the
        // call is made on, so loop variables and indexed elements keep class members.
        var configureHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 8, character = 12 },
        }), CancellationToken.None);
        Assert.Contains("configure()", configureHover!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        var configureDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 8, character = 12 },
        }), false, CancellationToken.None);
        Assert.Equal(
            new Uri("file:///src/system.lua").AbsoluteUri,
            configureDefinition!["uri"]!.GetValue<string>());

        var constrainHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 10, character = 15 },
        }), CancellationToken.None);
        Assert.Contains("constrain()", constrainHover!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        var constrainDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 10, character = 15 },
        }), false, CancellationToken.None);
        Assert.Equal(
            new Uri("file:///src/system.lua").AbsoluteUri,
            constrainDefinition!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnnotatedClassInstancesResolveMembersAcrossModules()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        workspace.Open(new Uri("file:///src/logger.lua"), 1, string.Join("\n",
        [
            "---@class Logger",
            "---@field scope string",
            "local Logger = {}",
            "Logger.__index = Logger",
            "---@param scope string",
            "---@return Logger",
            "function Logger.new(scope)",
            "  return setmetatable({ scope = scope }, Logger)",
            "end",
            "---@param message string",
            "function Logger:info(message) end",
            "return Logger",
        ]));
        var sessionUri = new Uri("file:///src/session.lua");
        workspace.Open(sessionUri, 1, string.Join("\n",
        [
            "local Logger = require(\"logger\")",
            "---@class Session",
            "---@field logger Logger",
            "local Session = {}",
            "Session.__index = Session",
            "---@return Session",
            "function Session.new()",
            "  return setmetatable({ logger = Logger.new(\"session\") }, Session)",
            "end",
            "function Session:connect()",
            "  self.logger:info(\"x\")",
            "end",
            "return Session",
        ]));
        var appUri = new Uri("file:///src/main.lua");
        workspace.Open(appUri, 1, string.Join("\n",
        [
            "local Logger = require(\"logger\")",
            "local logger = Logger.new(\"boot\")",
            "logger:info(\"hi\")",
        ]));
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);
        var hoverAt = async (Uri uri, int line, int character) => (await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line, character },
        }), CancellationToken.None))?["contents"]!["value"]!.GetValue<string>();
        var definitionAt = async (Uri uri, int line, int character) => (await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line, character },
        }), false, CancellationToken.None));

        // Annotation-typed instances (`---@return Logger`) navigate to the declaring
        // module's member and hover with its signature.
        Assert.Contains("info(message: string)", await hoverAt(appUri, 2, 8), StringComparison.Ordinal);
        Assert.Equal(
            new Uri("file:///src/logger.lua").AbsoluteUri,
            (await definitionAt(appUri, 2, 8))!["uri"]!.GetValue<string>());

        // Instance locals hover with their class card rather than a bare type name.
        var instanceHover = await hoverAt(appUri, 1, 7);
        Assert.Contains("class Logger", instanceHover, StringComparison.Ordinal);
        Assert.Contains("[info]", instanceHover, StringComparison.Ordinal);
        Assert.Contains("(message: string)", instanceHover, StringComparison.Ordinal);

        // Chained receivers (`self.logger:info`) resolve through the engine's recorded
        // member-chain type, not just the head symbol.
        Assert.Contains("info(message: string)", await hoverAt(sessionUri, 10, 15), StringComparison.Ordinal);
        Assert.Equal(
            new Uri("file:///src/logger.lua").AbsoluteUri,
            (await definitionAt(sessionUri, 10, 15))!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task PositionalArrayTablesSummarizeInHovers()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///scratch.lua");
        workspace.Open(uri, 1,
            "local systems = { make(), make(), make() }\n" +
            "function make() return { id = 1 } end\n" +
            "return systems");
        var service = new LuaLanguageService(workspace);

        var hover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 8 },
        }), CancellationToken.None);
        var value = hover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("systems: table", value, StringComparison.Ordinal);
        Assert.DoesNotContain("[unknown]", value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalizedHoverCardsFollowLocale()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var greeterUri = new Uri("file:///src/greeter.lua");
        workspace.Open(greeterUri, 1,
            "---@class Greeter\n" +
            "local Greeter = {}\n" +
            "function Greeter:hello() end\n" +
            "return Greeter");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace, new ServerLocalization());

        var hoverOnce = async () => await service.HoverAsync(Element(new
        {
            textDocument = new { uri = greeterUri.AbsoluteUri },
            position = new { line = 1, character = 6 },
        }), CancellationToken.None);

        var english = (await hoverOnce())!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("| Module | [greeter]", english, StringComparison.Ordinal);
        Assert.Contains("**Members (1)**", english, StringComparison.Ordinal);

        service.Localization.Locale = LunilLocale.SimplifiedChinese;
        var chinese = (await hoverOnce())!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("| 模块 | [greeter]", chinese, StringComparison.Ordinal);
        Assert.Contains("**成员 (1)**", chinese, StringComparison.Ordinal);

        service.Localization.Locale = LunilLocale.English;
        var restored = (await hoverOnce())!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("**Members (1)**", restored, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    [Fact]
    public void Utf16PositionsRoundTripUtf8BytesAndIncrementalChanges()
    {
        var document = new LspTextDocument(new Uri("file:///unicode.lua"), 1, "a😀b\r\nç");

        Assert.Equal(5, document.ToByteOffset(new LspPosition(0, 3)));
        Assert.Equal(new LspPosition(0, 3), document.ToPosition(5));
        Assert.Equal(new LspPosition(1, 1), document.ToPosition(document.ByteLength));

        var updated = document.Apply(2,
        [
            new LspTextChange(
                new LspRange(new LspPosition(0, 3), new LspPosition(0, 4)),
                "value"),
        ]);
        Assert.Equal("a😀value\r\nç", updated.Text);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void PositionInsideSurrogatePairClampsToCodePointBoundary()
    {
        var document = new LspTextDocument(new Uri("file:///unicode.lua"), 1, "😀");

        Assert.Equal(0, document.ToByteOffset(new LspPosition(0, 1)));
        Assert.Equal(new LspPosition(0, 0), document.ToPosition(2));
    }

    [Fact]
    public async Task SemanticTokensCoverAnnotationDirectives()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///annotated.lua");
        workspace.Open(uri, 1,
            "---@class Point\n" +
            "---@field x number\n" +
            "---@param p Point\n" +
            "local function len(p) return p.x end\n" +
            "return len");
        var service = new LuaLanguageService(workspace);
        var tokens = await service.SemanticTokensAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
        }), false, CancellationToken.None);
        var decoded = DecodeTokens(tokens!);

        // @tag keywords are macros; declared names carry the declaration modifier; type
        // expressions (class references and primitives) use the type kind.
        Assert.Contains((0, 3, 6, MacroTokenType, 0), decoded);          // @class
        Assert.Contains((0, 10, 5, ClassTokenType, DeclarationModifier), decoded); // Point
        Assert.Contains((1, 3, 6, MacroTokenType, 0), decoded);          // @field
        Assert.Contains((1, 10, 1, 3, DeclarationModifier), decoded);    // x
        Assert.Contains((1, 12, 6, TypeTokenType, 0), decoded);          // number
        Assert.Contains((2, 3, 6, MacroTokenType, 0), decoded);          // @param
        Assert.Contains((2, 10, 1, 1, DeclarationModifier), decoded);    // p
        Assert.Contains((2, 12, 5, TypeTokenType, 0), decoded);          // Point
    }

    private const int MacroTokenType = 5;
    private const int ClassTokenType = 6;
    private const int TypeTokenType = 7;
    private const int DeclarationModifier = 1;

    private static List<(int Line, int Character, int Length, int Type, int Modifiers)> DecodeTokens(JsonNode tokens)
    {
        var data = tokens["data"]!.AsArray().Select(static value => value!.GetValue<int>()).ToArray();
        var result = new List<(int, int, int, int, int)>(data.Length / 5);
        var line = 0;
        var character = 0;
        for (var index = 0; index < data.Length; index += 5)
        {
            var lineDelta = data[index];
            line += lineDelta;
            character = lineDelta == 0 ? character + data[index + 1] : data[index + 1];
            result.Add((line, character, data[index + 2], data[index + 3], data[index + 4]));
        }

        return result;
    }

    [Fact]
    public async Task SemanticTokenDeltaIsEmptyWhenDocumentIsUnchanged()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///stable.lua");
        workspace.Open(uri, 1, "local value = 1\nreturn value");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new { textDocument = new { uri = uri.AbsoluteUri } });
        var full = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);
        var deltaParameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            previousResultId = full!["resultId"]!.GetValue<string>(),
        });

        var delta = await service.SemanticTokensAsync(deltaParameters, true, CancellationToken.None);

        Assert.Empty(delta!["edits"]!.AsArray());
        Assert.Equal(full["resultId"]!.GetValue<string>(), delta["resultId"]!.GetValue<string>());
    }

    [Fact]
    public async Task MemberReferencesIncludeCrossModuleCallSitesFromTheDefinitionModule()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var utilUri = new Uri("file:///src/lib/util.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(utilUri, 1,
            "local M = {}\nfunction M.greet(name) return name end\nM.version = 2\nreturn M");
        workspace.Open(appUri, 1,
            "local util = require(\"lib.util\")\nreturn util.greet(\"world\")");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // References from the definition site (receiver M is the local module table) must
        // reach the cross-module call site, not just same-file occurrences.
        var fromDefinition = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = utilUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), CancellationToken.None);
        var definitionUris = fromDefinition!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(appUri.AbsoluteUri, definitionUris);
        Assert.Contains(utilUri.AbsoluteUri, definitionUris);

        // References from the call site still include the exported definition.
        var fromCallSite = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), CancellationToken.None);
        Assert.Contains(fromCallSite!.AsArray(),
            location => location!["uri"]!.GetValue<string>() == utilUri.AbsoluteUri);
    }

    [Fact]
    public async Task GlobalNameReferencesAndDefinitionUseTheWorkspaceIndex()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var helperUri = new Uri("file:///src/helper.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(helperUri, 1, "function helper() return 1 end\nreturn helper()");
        workspace.Open(appUri, 1, "return helper() + 1");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 9 },
        });

        var references = await service.ReferencesAsync(parameters, CancellationToken.None);
        var referenceUris = references!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(helperUri.AbsoluteUri, referenceUris);
        Assert.Contains(appUri.AbsoluteUri, referenceUris);

        var definition = await service.DefinitionAsync(parameters, false, CancellationToken.None);
        Assert.NotNull(definition);
        Assert.Equal(helperUri.AbsoluteUri, definition!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequireStringReferencesListEveryRequireSite()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var utilUri = new Uri("file:///src/lib/util.lua");
        var appUri = new Uri("file:///src/app.lua");
        var secondUri = new Uri("file:///src/second.lua");
        workspace.Open(utilUri, 1, "local M = {}\nfunction M.f() return 1 end\nreturn M");
        workspace.Open(appUri, 1, "local util = require(\"lib.util\")\nreturn util.f()");
        workspace.Open(secondUri, 1, "local u2 = require('lib.util')\nreturn u2.f()");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var references = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 26 },
        }), CancellationToken.None);
        var referenceUris = references!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();

        Assert.Contains(appUri.AbsoluteUri, referenceUris);
        Assert.Contains(secondUri.AbsoluteUri, referenceUris);
    }

    [Fact]
    public async Task ReindexResolvesPendingStatusAndRetryFilesRepublish()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        // Loaded (not opened) documents carry no fire-and-forget publish task, so the
        // pending state before reindexing is deterministic.
        workspace.LoadDocumentsForScale(
        [
            new LspTextDocument(new Uri("file:///src/first.lua"), 0, "return 1", isOpen: false),
            new LspTextDocument(new Uri("file:///src/second.lua"), 0, "return 2", isOpen: false),
        ]);

        var pending = workspace.GetIndexStatus();
        Assert.Equal(2, (int)pending["pending"]!);
        Assert.Equal(0, (int)pending["succeeded"]!);

        await workspace.ReindexNowAsync(CancellationToken.None);
        var indexed = workspace.GetIndexStatus();
        Assert.Equal(0, (int)indexed["pending"]!);
        Assert.Equal(2, (int)indexed["succeeded"]!);

        var retried = await workspace.RetryFilesAsync(
            [new Uri("file:///src/first.lua")], CancellationToken.None);
        Assert.Equal(1, retried);
        var afterRetry = workspace.GetIndexStatus();
        Assert.Equal(0, (int)afterRetry["failed"]!);
        Assert.Equal(0, (int)afterRetry["inProgress"]!);
        Assert.Equal(0, (int)afterRetry["pending"]!);
        Assert.Empty(afterRetry["failedFiles"]!.AsArray());
        Assert.Empty(workspace.GetFailedDocuments());
    }

    [Fact]
    public async Task ClassInheritanceMembersResolveAcrossModules()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var baseUri = new Uri("file:///src/base.lua");
        var animalUri = new Uri("file:///src/animal.lua");
        var npcUri = new Uri("file:///src/npc.lua");
        workspace.Open(baseUri, 1,
            "---@class Base\n" +
            "---@field tag string\n" +
            "local Base = {}\n" +
            "Base.__index = Base\n" +
            "function Base:extend(name) return Base end\n" +
            "function Base:describe() return self.tag end\n" +
            "return Base");
        workspace.Open(animalUri, 1,
            "local Base = require(\"base\")\n" +
            "---@class Animal : Base\n" +
            "---@field name string\n" +
            "local Animal = Base:extend(\"Animal\")\n" +
            "function Animal:init(name) self.name = name end\n" +
            "return Animal");
        workspace.Open(npcUri, 1,
            "local Animal = require(\"animal\")\n" +
            "local npc = Animal:extend(\"npc\")\n" +
            "return npc");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // Hover over the inherited `extend` shows its signature from the base module.
        var hover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = npcUri.AbsoluteUri },
            position = new { line = 1, character = 20 },
        }), CancellationToken.None);
        Assert.NotNull(hover);
        Assert.Contains("(name", hover!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        // Definition of the inherited member lands in the base module.
        var extendDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = npcUri.AbsoluteUri },
            position = new { line = 1, character = 20 },
        }), false, CancellationToken.None);
        Assert.NotNull(extendDefinition);
        Assert.Equal(baseUri.AbsoluteUri, extendDefinition!["uri"]!.GetValue<string>());
        Assert.Equal(4, extendDefinition["range"]!["start"]!["line"]!.GetValue<int>());

        // References cover the definition and the cross-module call site.
        var extendReferences = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = npcUri.AbsoluteUri },
            position = new { line = 1, character = 20 },
        }), CancellationToken.None);
        var referenceUris = extendReferences!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(baseUri.AbsoluteUri, referenceUris);
        Assert.Contains(npcUri.AbsoluteUri, referenceUris);

        // The require alias passes through to the exported class value's definition.
        var aliasDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = npcUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), false, CancellationToken.None);
        Assert.NotNull(aliasDefinition);
        Assert.Equal(animalUri.AbsoluteUri, aliasDefinition!["uri"]!.GetValue<string>());
        Assert.Equal(3, aliasDefinition["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task BuiltinLibraryProvidesTypesDocsAndNavigation()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///scratch.lua");
        var source = string.Join("\n",
        [
            "local name = string.format('%s', 42)",
            "local floor = math.floor(3.7)",
            "print(name, floor)",
        ]);
        workspace.Open(uri, 1, source);
        var service = new LuaLanguageService(workspace);

        // Member hover shows the annotated signature, the doc comment, and a link into
        // the readonly builtin document.
        var formatHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 20 },
        }), CancellationToken.None);
        var formatValue = formatHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("format(s: string", formatValue, StringComparison.Ordinal);
        Assert.Contains("Formats values under format directives", formatValue, StringComparison.Ordinal);
        Assert.Contains("command:lunil._openBuiltinLocation", formatValue, StringComparison.Ordinal);

        // Go-to-definition opens the readonly builtin document at the definition.
        var formatDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 20 },
        }), false, CancellationToken.None);
        Assert.Equal("lunil-builtin:string.lua", formatDefinition!["uri"]!.GetValue<string>());

        // Global functions hover with their signature and link.
        var printHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 2, character = 2 },
        }), CancellationToken.None);
        var printValue = printHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("print(", printValue, StringComparison.Ordinal);
        Assert.Contains("Writes the given values", printValue, StringComparison.Ordinal);

        // Member completion on a stdlib table lists annotated members.
        workspace.Open(uri, 2, "local sorted = table." + "\n" + "return sorted");
        var completion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 22 },
        }), CancellationToken.None);
        var labels = completion!["items"]!.AsArray()
            .Select(item => item!["label"]!.GetValue<string>()).ToArray();
        Assert.Contains("insert", labels);
        Assert.Contains("concat", labels);
        Assert.Contains("sort", labels);
    }

    [Fact]
    public async Task AnnotationElementsNavigateAndHover()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var vecUri = new Uri("file:///src/vec.lua");
        var appUri = new Uri("file:///src/app.lua");
        var vecSource = string.Join("\n",
        [
            "---A two-component vector.",
            "---@class Vec",
            "---@field x number",
            "local Vec = {}",
            "Vec.__index = Vec",
            "---Adds two vectors.",
            "function Vec:add(other) return Vec.new() end",
            "function Vec.new() return setmetatable({}, Vec) end",
            "return Vec",
        ]);
        var appSource = string.Join("\n",
        [
            "local Vec = require(\"vec\")",
            "---@param v Vec",
            "---@return Vec",
            "local function length(v)",
            "  return (v.x ^ 2) ^ 0.5",
            "end",
            "return length",
        ]);
        workspace.Open(vecUri, 1, vecSource);
        workspace.Open(appUri, 1, appSource);
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // F12 on the type reference in app's @param jumps to vec.lua's class declaration.
        var definition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), false, CancellationToken.None);
        Assert.NotNull(definition);
        Assert.Equal(vecUri.AbsoluteUri, definition!["uri"]!.GetValue<string>());
        Assert.Equal(3, definition["range"]!["start"]!["line"]!.GetValue<int>());

        // References on the type name cover both files' annotation mentions.
        var references = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), CancellationToken.None);
        var referenceUris = references!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(vecUri.AbsoluteUri, referenceUris);
        Assert.Contains(appUri.AbsoluteUri, referenceUris);

        // Hover on the type reference shows the class card.
        var typeHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), CancellationToken.None);
        var typeHoverValue = typeHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("class Vec", typeHoverValue, StringComparison.Ordinal);
        Assert.Contains("| Module | [vec]", typeHoverValue, StringComparison.Ordinal);
        Assert.Contains("[add]", typeHoverValue, StringComparison.Ordinal);
        Assert.Contains("(other", typeHoverValue, StringComparison.Ordinal);

        // Hover on the class declaration name in the annotation shows the same card.
        var declarationHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = vecUri.AbsoluteUri },
            position = new { line = 1, character = 10 },
        }), CancellationToken.None);
        Assert.Contains(
            "class Vec",
            declarationHover!["contents"]!["value"]!.GetValue<string>(),
            StringComparison.Ordinal);

        // References from the class declaration name include the require site.
        var declarationReferences = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = vecUri.AbsoluteUri },
            position = new { line = 1, character = 10 },
        }), CancellationToken.None);
        var declarationReferenceUris = declarationReferences!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(appUri.AbsoluteUri, declarationReferenceUris);
    }

    [Fact]
    public async Task ClassValueNavigationFromDeclarationAndAlias()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var baseUri = new Uri("file:///src/base.lua");
        var toolUri = new Uri("file:///src/tool.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(baseUri, 1,
            "---@class Base" + "\n" +
            "local Base = {}" + "\n" +
            "Base.__index = Base" + "\n" +
            "function Base:extend(name) return Base end" + "\n" +
            "function Base:new() return setmetatable({}, Base) end" + "\n" +
            "return Base");
        workspace.Open(toolUri, 1,
            "local Base = require(\"base\")" + "\n" +
            "---@class Tool : Base" + "\n" +
            "---@field size number" + "\n" +
            "local Tool = Base:extend(\"Tool\")" + "\n" +
            "---Uses the tool." + "\n" +
            "function Tool:use() return 1 end" + "\n" +
            "return Tool");
        workspace.Open(appUri, 1,
            "local Tool = require(\"tool\")" + "\n" + "return Tool.use");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // References from the class declaration include the module's require sites.
        var references = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = toolUri.AbsoluteUri },
            position = new { line = 3, character = 6 },
        }), CancellationToken.None);
        var referenceUris = references!.AsArray()
            .Select(location => location!["uri"]!.GetValue<string>()).ToHashSet();
        Assert.Contains(appUri.AbsoluteUri, referenceUris);
        Assert.Contains(toolUri.AbsoluteUri, referenceUris);

        // Hover on the declaration shows the class, its inheritance, and members.
        var declaredHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = toolUri.AbsoluteUri },
            position = new { line = 3, character = 6 },
        }), CancellationToken.None);
        var declaredValue = declaredHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("class Tool : Base", declaredValue, StringComparison.Ordinal);
        Assert.Contains("| Module | [tool]", declaredValue, StringComparison.Ordinal);
        Assert.Contains("[size]", declaredValue, StringComparison.Ordinal);
        Assert.Contains(": number", declaredValue, StringComparison.Ordinal);
        Assert.Contains("[use](", declaredValue, StringComparison.Ordinal);
        Assert.Contains("command:lunil._openLocation", declaredValue, StringComparison.Ordinal);
        Assert.Contains("Uses the tool.", declaredValue, StringComparison.Ordinal);
        Assert.Contains("Inherited from Base", declaredValue, StringComparison.Ordinal);
        Assert.Contains("[extend]", declaredValue, StringComparison.Ordinal);
        Assert.Contains("| Extends | [Base](", declaredValue, StringComparison.Ordinal);
        Assert.Contains("\n---\n", declaredValue, StringComparison.Ordinal);

        // The alias in the consuming module hovers with the same class view.
        var aliasHover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 7 },
        }), CancellationToken.None);
        var aliasValue = aliasHover!["contents"]!["value"]!.GetValue<string>();
        Assert.Contains("class Tool : Base", aliasValue, StringComparison.Ordinal);
        Assert.Contains("[use]", aliasValue, StringComparison.Ordinal);

        // F12 on the alias declaration passes through to the class line.
        var aliasDeclaration = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 7 },
        }), false, CancellationToken.None);
        Assert.NotNull(aliasDeclaration);
        Assert.Equal(toolUri.AbsoluteUri, aliasDeclaration!["uri"]!.GetValue<string>());
        Assert.Equal(3, aliasDeclaration["range"]!["start"]!["line"]!.GetValue<int>());

        // Rename prepares from the declaration token as well.
        var prepare = await service.PrepareRenameAsync(Element(new
        {
            textDocument = new { uri = toolUri.AbsoluteUri },
            position = new { line = 3, character = 6 },
        }), CancellationToken.None);
        Assert.NotNull(prepare);
        Assert.Equal("Tool", prepare!["placeholder"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClassInheritanceMembersAppearInCompletion()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var baseUri = new Uri("file:///src/base.lua");
        var animalUri = new Uri("file:///src/animal.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(baseUri, 1,
            "---@class Base\nlocal Base = {}\nBase.__index = Base\n" +
            "function Base:extend(name) return Base end\nreturn Base");
        workspace.Open(animalUri, 1,
            "local Base = require(\"base\")\n---@class Animal : Base\n" +
            "local Animal = Base:extend(\"Animal\")\nfunction Animal:init() end\nreturn Animal");
        workspace.Open(appUri, 1,
            "local Animal = require(\"animal\")\nlocal x = Animal.");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var completion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 15 },
        }), CancellationToken.None);
        var labels = completion!["items"]!.AsArray()
            .Select(item => item!["label"]!.GetValue<string>()).ToArray();

        Assert.Contains("extend", labels);
        Assert.Contains("init", labels);
    }

    [Fact]
    public async Task WorkspaceRejectsStaleVersionsAndKeepsUnsavedOverlay()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///overlay.lua");
        workspace.Open(uri, 4, "local value = 1\nreturn value");

        Assert.False(workspace.Change(uri, 4, [new LspTextChange(null, "return nil")]));
        Assert.True(workspace.Change(uri, 5,
        [
            new LspTextChange(
                new LspRange(new LspPosition(0, 6), new LspPosition(0, 11)),
                "answer"),
            new LspTextChange(
                new LspRange(new LspPosition(1, 7), new LspPosition(1, 12)),
                "answer"),
        ]));

        var analysis = await workspace.GetAnalysisAsync(uri, CancellationToken.None);
        Assert.NotNull(analysis);
        Assert.Equal(5, analysis.Document.Version);
        Assert.Contains(analysis.Compilation.SemanticModel.Symbols, static symbol => symbol.Name == "answer");
    }

    [Fact]
    public async Task DocumentProvidersUseAnalysisOnlyFrontEndWithoutAHostContract()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///injected-globals.lua");
        workspace.Open(
            uri,
            1,
            "local function render(value) return NativeBridge.draw(value) end\n" +
            "local result = render(InjectedGameState.value)\n" +
            "return result");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 8 },
        });

        var analysis = await workspace.GetAnalysisAsync(uri, CancellationToken.None);
        var symbols = await service.DocumentSymbolsAsync(parameters, CancellationToken.None);
        var semanticTokens = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);
        var inlayHints = await service.InlayHintsAsync(parameters, CancellationToken.None);

        Assert.NotNull(analysis);
        Assert.True(analysis.Compilation.IsAnalysisOnly);
        Assert.Equal(Lunil.Compiler.LuaFrontEndStage.Analysis, analysis.Compilation.FrontEndSnapshot!.Stage);
        Assert.Null(analysis.Compilation.Module);
        Assert.NotEmpty(symbols!.AsArray());
        Assert.NotEmpty(semanticTokens!["data"]!.AsArray());
        Assert.NotEmpty(inlayHints!.AsArray());
    }

    [Fact]
    public async Task DocumentSymbolsAndSemanticTokensWorkWithoutACursorPosition()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///no-position.lua");
        workspace.Open(uri, 1, "local function render() return 1 end\nreturn render()");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new { textDocument = new { uri = uri.AbsoluteUri } });

        var symbols = await service.DocumentSymbolsAsync(parameters, CancellationToken.None);
        var semanticTokens = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);

        Assert.NotEmpty(symbols!.AsArray());
        Assert.NotEmpty(semanticTokens!["data"]!.AsArray());
    }

    [Fact]
    public async Task MemberNavigationResolvesModuleExportsAndRequireStrings()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var utilUri = new Uri("file:///src/lib/util.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(utilUri, 1,
            "local M = {}\nfunction M.greet(name) return name end\nM.version = 2\nreturn M");
        workspace.Open(appUri, 1,
            "local util = require(\"lib.util\")\nreturn util.greet(util.version)");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var definition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), false, CancellationToken.None);
        Assert.NotNull(definition);
        Assert.Equal(utilUri.AbsoluteUri, definition!["uri"]!.GetValue<string>());
        Assert.Equal(1, definition["range"]!["start"]!["line"]!.GetValue<int>());

        var fieldDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 27 },
        }), false, CancellationToken.None);
        Assert.NotNull(fieldDefinition);
        Assert.Equal(utilUri.AbsoluteUri, fieldDefinition!["uri"]!.GetValue<string>());

        var requireDefinition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 26 },
        }), false, CancellationToken.None);
        Assert.NotNull(requireDefinition);
        Assert.Equal(utilUri.AbsoluteUri, requireDefinition!["uri"]!.GetValue<string>());

        var references = await service.ReferencesAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 13 },
        }), CancellationToken.None);
        Assert.Contains(references!.AsArray(),
            location => location!["uri"]!.GetValue<string>() == utilUri.AbsoluteUri);
    }

    [Fact]
    public async Task CompletionUsesMemberAndRequireContexts()
    {
        var folder = new Uri("file:///src/");
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([folder]);
        var utilUri = new Uri("file:///src/lib/util.lua");
        var appUri = new Uri("file:///src/app.lua");
        workspace.Open(utilUri, 1,
            "local M = {}\nfunction M.greet(name) return name end\nM.version = 2\nreturn M");
        workspace.Open(appUri, 1,
            "local util = require(\"lib.util\")\nlocal x = util.");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var memberCompletion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 15 },
        }), CancellationToken.None);
        var memberLabels = memberCompletion!["items"]!.AsArray()
            .Select(item => item!["label"]!.GetValue<string>()).ToArray();
        Assert.Contains("greet", memberLabels);
        Assert.Contains("version", memberLabels);
        Assert.DoesNotContain("function", memberLabels);

        var methodCompletion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 1, character = 15 },
            context = new { triggerCharacter = ":" },
        }), CancellationToken.None);
        _ = methodCompletion;

        workspace.Open(appUri, 2, "local util = require(\"");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var requireCompletion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 22 },
        }), CancellationToken.None);
        var requireLabels = requireCompletion!["items"]!.AsArray()
            .Select(item => item!["label"]!.GetValue<string>()).ToArray();
        Assert.Contains("lib.util", requireLabels);
        Assert.DoesNotContain("local", requireLabels);
    }

    [Fact]
    public async Task SemanticTokensIncludeMemberReferences()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///members.lua");
        workspace.Open(uri, 1,
            "local t = {}\nt.value = 1\nlocal function run() return t.value end\nreturn t.value");
        var service = new LuaLanguageService(workspace);
        var tokens = await service.SemanticTokensAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
        }), false, CancellationToken.None);
        var data = tokens!["data"]!.AsArray().Select(static value => value!.GetValue<int>()).ToArray();
        Assert.NotEmpty(data);
        // Every reference (names and members) contributes a token; member accesses
        // must be present beyond the plain lexical references.
        Assert.True(data.Length / 5 >= 6, $"expected at least 6 tokens, got {data.Length / 5}");
        var memberTypes = new HashSet<int>();
        for (var index = 0; index < data.Length; index += 5)
        {
            memberTypes.Add(data[index + 3]);
        }

        Assert.Contains(3, memberTypes);
    }

    private static readonly string[] UnannotatedClassHoverLines =
    [
        "local Animal = {}",
        "Animal.__index = Animal",
        "function Animal.new(name) return setmetatable({}, Animal) end",
        "function Animal:speak() return self.name .. \"...\" end",
        "local Dog = setmetatable({}, { __index = Animal })",
        "Dog.__index = Dog",
        "function Dog.new(name) return setmetatable({}, Dog) end",
        "function Dog:fetch() return \"ball\" end",
        "local dog = Dog.new(\"rex\")",
        "return dog",
    ];

    private static readonly string[] UnannotatedClassCompletionLines =
    [
        "local Dog = {}",
        "Dog.__index = Dog",
        "function Dog.new(name) return setmetatable({}, Dog) end",
        "function Dog:fetch() return \"ball\" end",
        "function Dog:bark() return \"woof\" end",
        "local dog = Dog.new(\"rex\")",
        "local sound = dog.",
        "return sound",
    ];

    [Fact]
    public async Task UnannotatedMetatableClassesProvideInstanceTypesAndOutline()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///oop.lua");
        workspace.Open(uri, 1, string.Join("\n", UnannotatedClassHoverLines));
        var service = new LuaLanguageService(workspace);

        // Instance hover shows the constructor-inferred metatable type.
        var hover = await service.HoverAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 9, character = 11 },
        }), CancellationToken.None);
        Assert.NotNull(hover);
        Assert.DoesNotContain("any", hover!["contents"]!["value"]!.GetValue<string>(), StringComparison.Ordinal);

        // Member completion on the instance lists own and __index-inherited members.
        workspace.Open(uri, 2, string.Join("\n", UnannotatedClassCompletionLines));
        var completion = await service.CompletionAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 6, character = 18 },
        }), CancellationToken.None);
        var labels = completion!["items"]!.AsArray()
            .Select(item => item!["label"]!.GetValue<string>()).ToArray();
        Assert.Contains("fetch", labels);
        Assert.Contains("bark", labels);
        Assert.Contains("new", labels);

        // Outline includes table-assigned functions without annotations.
        var symbols = await service.DocumentSymbolsAsync(Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
        }), CancellationToken.None);
        var names = symbols!.AsArray().Select(item => item!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("fetch", names);
        Assert.Contains("bark", names);
        Assert.Contains("new", names);
    }

    [Fact]
    public async Task HoverReferencesAndCapturedLocalRenameUseStableBinding()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///capture.lua");
        workspace.Open(uri, 1, "local value = 1\nlocal function read() return value end\nreturn value");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 29 },
            context = new { includeDeclaration = true },
        });

        var hover = await service.HoverAsync(parameters, CancellationToken.None);
        var references = await service.ReferencesAsync(parameters, CancellationToken.None);
        var renameParameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 29 },
            newName = "captured",
        });
        var rename = await service.RenameAsync(renameParameters, CancellationToken.None);

        Assert.Contains("upvalue", hover!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(references!.AsArray().Count >= 2);
        Assert.Contains("captured", rename!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticTokenDeltaReplacesPriorVersionDeterministically()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///tokens.lua");
        workspace.Open(uri, 1, "local value = 1\nreturn value");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 0 },
        });
        var full = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);
        var previousId = full!["resultId"]!.GetValue<string>();
        // Rename a reference use (declarations are not references, so their tokens do
        // not change; the use on line 1 does).
        Assert.True(workspace.Change(uri, 2,
            [new LspTextChange(new LspRange(new LspPosition(1, 7), new LspPosition(1, 12)), "answer")]));
        var deltaParameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 0 },
            previousResultId = previousId,
        });

        var delta = await service.SemanticTokensAsync(deltaParameters, true, CancellationToken.None);

        Assert.NotEmpty(delta!["edits"]!.AsArray());
    }

    [Fact]
    public async Task CompactIndexSupportsCrossModuleWorkspaceSymbolsAndReferences()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var moduleUri = new Uri("file:///service.lua");
        var appUri = new Uri("file:///app.lua");
        workspace.Open(moduleUri, 1, "local M = {}\nfunction M.run() return 1 end\nreturn M");
        workspace.Open(appUri, 1, "local service = require('service')\nreturn service.run()");

        await workspace.ReindexNowAsync(CancellationToken.None);
        var snapshot = workspace.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.ExportGraph.Symbols, static symbol => symbol.Name == "run");
        Assert.Contains(snapshot.CallBindings.Edges, static edge => edge.MemberPath == "run");
    }

    [Fact]
    public async Task WorkspaceSymbolsQueryReturnsCrossModuleExports()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var moduleUri = new Uri("file:///service.lua");
        workspace.Open(moduleUri, 1,
            "local M = {}\nfunction M.run() return 1 end\nfunction M.stop() return 0 end\nreturn M");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var symbols = service.WorkspaceSymbols("run");
        var names = symbols.AsArray()
            .Select(symbol => symbol!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("run", names);
        Assert.DoesNotContain("stop", names);
        Assert.NotEmpty(symbols.AsArray());
    }

    [Fact]
    public async Task ClassHierarchyListsBasesAndDerivedClasses()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var baseUri = new Uri("file:///src/base.lua");
        var midUri = new Uri("file:///src/mid.lua");
        var derivedUri = new Uri("file:///src/derived.lua");
        workspace.Open(baseUri, 1, "---@class Base\nlocal Base = {}\nreturn Base");
        workspace.Open(midUri, 1, "---@class Mid : Base\nlocal Mid = {}\nreturn Mid");
        workspace.Open(derivedUri, 1, "---@class Derived : Mid\nlocal Derived = {}\nreturn Derived");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        var hierarchy = await service.ClassHierarchyAsync(Element(new
        {
            textDocument = new { uri = midUri.AbsoluteUri },
            position = new { line = 0, character = 11 },
        }), CancellationToken.None);
        Assert.NotNull(hierarchy);
        Assert.Equal("Mid", hierarchy!["name"]!.GetValue<string>());
        var bases = hierarchy["bases"]!.AsArray()
            .Select(item => item!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("Base", bases);
        var derived = hierarchy["derived"]!.AsArray()
            .Select(item => item!["name"]!.GetValue<string>()).ToArray();
        Assert.Contains("Derived", derived);
    }

    [Fact]
    public async Task DottedAnnotationClassMemberNavigatesToClassDeclaration()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var stubsUri = new Uri("file:///stubs.lua");
        var appUri = new Uri("file:///app.lua");
        workspace.Open(stubsUri, 1,
            "---@class host.Engine.Utility.TimeUtil\n" +
            "local TimeUtil = {}\n" +
            "function TimeUtil:now() return os.time() end\n" +
            "return TimeUtil");
        workspace.Open(appUri, 1,
            "return host.Engine.Utility.TimeUtil.now()");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);

        // The class segment of the dotted path is addressable as a member of the
        // namespace prefix; F12 opens the generated stub's annotation declaration.
        var definition = await service.DefinitionAsync(Element(new
        {
            textDocument = new { uri = appUri.AbsoluteUri },
            position = new { line = 0, character = 28 },
        }), false, CancellationToken.None);
        Assert.NotNull(definition);
        Assert.Equal(stubsUri.AbsoluteUri, definition!["uri"]!.GetValue<string>());
        Assert.Equal(3, definition["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public async Task HostContractDefinitionsAndImplementationsMapToExternalSources()
    {
        var number = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Number };
        var contract = new LuaHostContractBuilder("lsp-host")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "game.run",
                Returns = [number],
                Source = new LuaHostSourceLocation
                {
                    Uri = "cpp://engine/game#run",
                    ImplementationUri = "cpp-implementation://engine/game#run",
                    Line = 4,
                    Column = 2,
                },
            })
            .Build();
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        workspace.ConfigureHostContract(contract.ToJson(), path: null);
        var uri = new Uri("file:///host.lua");
        workspace.Open(uri, 1, "return game.run()");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 13 },
        });

        var definition = await service.DefinitionAsync(parameters, false, CancellationToken.None);
        var implementation = await service.DefinitionAsync(parameters, true, CancellationToken.None);

        Assert.Equal("cpp://engine/game#run", definition!["uri"]!.GetValue<string>());
        Assert.Equal("cpp-implementation://engine/game#run", implementation!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonRpcCancellationReturnsLspCancellationError()
    {
        var request = Frame("""{"jsonrpc":"2.0","id":7,"method":"slow","params":{}}""");
        var cancel = Frame("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":7}}""");
        await using var input = new MemoryStream(request.Concat(cancel).ToArray());
        await using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);

        await connection.RunAsync(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return JsonValue.Create(true);
        });

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        Assert.Equal(-32800, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task JsonRpcInternalErrorsWriteFullExceptionToLocalDiagnosticStream()
    {
        var request = Frame(
            """{"jsonrpc":"2.0","id":11,"method":"textDocument/semanticTokens/full","params":{}}""");
        await using var input = new MemoryStream(request);
        await using var output = new MemoryStream();
        using var errors = new StringWriter();
        await using var connection = new JsonRpcConnection(input, output, errors);

        await connection.RunAsync((_, _) =>
            throw new KeyNotFoundException("The analysis key was not present."));

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Equal("KeyNotFoundException: The analysis key was not present.",
            error.GetProperty("data").GetString());
        Assert.Contains("method=textDocument/semanticTokens/full", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("System.Collections.Generic.KeyNotFoundException", errors.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("The analysis key was not present.", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains(" at ", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRpcNotificationInternalErrorsAreLoggedWithoutStoppingTheConnection()
    {
        var notification = Frame(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""");
        var request = Frame(
            """{"jsonrpc":"2.0","id":12,"method":"lunil/reindexWorkspace","params":{}}""");
        await using var input = new MemoryStream(notification.Concat(request).ToArray());
        await using var output = new MemoryStream();
        using var errors = new StringWriter();
        await using var connection = new JsonRpcConnection(input, output, errors);

        await connection.RunAsync((message, _) => message.IsNotification
            ? throw new KeyNotFoundException("The notification analysis key was not present.")
            : Task.FromResult<JsonNode?>(JsonValue.Create(true)));

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        Assert.True(response.RootElement.GetProperty("result").GetBoolean());
        Assert.Contains("method=textDocument/didOpen", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("The notification analysis key was not present.", errors.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAdvertisesLsp317Capabilities()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);
        using var server = new LuaLanguageServer(connection);
        var request = new JsonRpcRequest("initialize", Element("""{"workspaceFolders":[]}"""),
            Element("1"));

        var result = await server.DispatchAsync(request, CancellationToken.None);

        Assert.Equal("utf-16", result!["capabilities"]!["positionEncoding"]!.GetValue<string>());
        Assert.True(result["capabilities"]!["renameProvider"]!["prepareProvider"]!.GetValue<bool>());
        Assert.True(result["capabilities"]!["semanticTokensProvider"]!["full"]!["delta"]!.GetValue<bool>());
    }

    [Fact]
    public async Task LateCancellationAfterRequestCompletionDoesNotKillTheConnection()
    {
        // A $/cancelRequest racing the response used to Cancel a disposed source and
        // crash the whole process; the connection must survive it and keep answering.
        var request = Frame("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"quick\",\"params\":{}}");
        var lateCancel = Frame("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":7}}");
        var followUp = Frame("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"quick\",\"params\":{}}");
        await using var input = new MemoryStream(request.Concat(lateCancel).Concat(followUp).ToArray());
        await using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);

        await connection.RunAsync((message, _) => Task.FromResult<JsonNode?>(JsonValue.Create(true)));

        var header = Encoding.ASCII.GetBytes("Content-Length:");
        var written = output.ToArray();
        var responses = 0;
        for (var index = 0; (index = IndexOfBytes(written, header, index)) >= 0; index += header.Length)
        {
            responses++;
        }

        Assert.Equal(2, responses);
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int start)
    {
        for (var index = start; index <= haystack.Length - needle.Length; index++)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[index + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return index;
            }
        }

        return -1;
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
    }

    private static byte[] ReadFirstPayload(byte[] framed)
    {
        var separator = Encoding.ASCII.GetBytes("\r\n\r\n");
        var index = framed.AsSpan().IndexOf(separator);
        Assert.True(index >= 0);
        return framed[(index + separator.Length)..];
    }
}
