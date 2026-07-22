# Static analysis embedding

[简体中文](static-analysis-embedding.zh-CN.md)

This guide targets hosts that consume Lunil as a compiler and code-intelligence library. The
checked-in sample is the executable source of truth:

```bash
dotnet run --project samples/Lunil.StaticAnalysis.Embedding -c Release
dotnet test tests/Lunil.Workspace.Tests/Lunil.Workspace.Tests.csproj -c Release \
  --filter FullyQualifiedName~StaticAnalysisEmbeddingSampleTests
```

The sample compiles one annotated file, prints semantic and analysis indexes, analyzes a cyclic
two-module workspace three times, demonstrates cache reuse and invalidation, and is built and
executed by the solution tests.

## Configure one pipeline

`LuaCompilerOptions.LanguageVersion` is authoritative for standalone compilation.
`LuaWorkspaceOptions.LanguageVersion` is authoritative for a workspace and aligns its nested
compiler to that value. Configure analysis limits once, then select the same version in both
contexts:

```csharp
var compilerOptions = LuaCompilerOptions.Default with
{
    LanguageVersion = LuaLanguageVersion.Lua54,
    Analysis = LuaAnalysisOptions.Default with
    {
        ReportUnknownGlobals = true,
        ReportImplicitAny = true,
        MaximumTypeCount = 20_000,
        MaximumConstraintCount = 40_000,
    },
};

var result = new LuaCompiler(compilerOptions).CompileUtf8(
    source,
    sourceName: "@game/player.lua",
    cancellationToken);
```

One `LuaCompilationResult` owns a consistent immutable snapshot:

- `Syntax`: lossless tokens and typed syntax facades;
- `Annotations`: parsed EmmyLua/LuaLS directives;
- `SemanticModel`: symbols, references, functions, and stable-key operations;
- `Analysis`: declaration types, expression types, function types, CFGs, budgets, and call graph;
- `Diagnostics`: ordered diagnostics tagged with `LuaCompilationPhase`;
- `Module`: canonical IR when compilation reached lowering and verification.

Check `Succeeded` before executing or persisting canonical IR. Static-analysis data can still be
useful when warnings or recoverable source errors are present, so retain diagnostics with the
snapshot rather than discarding the entire result.

## Byte spans and editor locations

`SourceText` stores UTF-8 bytes. Every `TextSpan` is a half-open UTF-8 byte range
`[Start, End)`, not a UTF-16 string index. Convert offsets through the owning source:

```csharp
SourceLocation start = result.Source.Text.GetLocation(span.Start);
SourceLocation end = result.Source.Text.GetLocation(span.End);
```

`SourceLocation.Line`, `ByteColumn`, and `Utf16Column` are zero-based. LSP positions can use
`Line` and `Utf16Column` directly. A one-based UI should add one to both values, as the sample's
`FormatSpan` helper does. Never apply a span to a different source snapshot.

## Correlate semantic and analysis data

`LuaSymbol.Id` and `LuaFunctionInfo.Id` are compilation-local. Within one result:

- match `LuaSymbolTypeInfo.Symbol` by object identity or symbol ID;
- match `LuaFunctionAnalysis.FunctionId` to `LuaSemanticModel.Functions.Id`;
- match expression types and syntax through byte spans;
- use `LuaSemanticModel.FindReferences(symbol)` for local/upvalue identity;
- use `FindGlobalReferences(name)` for implicit `_ENV` globals;
- read `LuaAnalysisResult.CallGraph` instead of reconstructing calls from generic AST children.

For persistence, generate `LuaSymbolKey` with a logical module identity:

```csharp
var key = result.SemanticModel.GetSymbolKey(symbol, "game.player");
var later = anotherResult.SemanticModel.ResolveSymbolKey(key, "game.player");
```

Stable keys intentionally exclude source offsets and transient IDs. Renaming a declaration,
moving it to a different lexical owner, or changing its module identity may produce a new key.
Classes, aliases, and enums use `LuaCompilationResult.GetAnnotationKey` and
`ResolveAnnotationKey`.

## Read CFGs, calls, and declarations

Each `LuaFunctionAnalysis` exposes the inferred function type and return pack, flow iteration
count, widening state, and a `LuaControlFlowGraph`. Use `block.IsReachable` rather than assuming
every emitted block is live. Call-graph edges retain resolved, dynamic, unresolved, and unreachable
calls together with their containing function and optional target function.

`LuaAnalysisResult.TypeDeclarations` is the resolved view of class, alias, and enum directives;
`LuaAnnotationDocument.Annotations` is the syntax-level view. Use the former for types and the
latter for tooling that must preserve exact directives and source spans.

## Analyze a reusable workspace

Use logical module names for `LuaModuleIdentity` and stable source-origin strings for
`SourceIdentity`:

```csharp
var documents = new[]
{
    LuaWorkspaceDocument.FromUtf8("game.client", client, "@game/client.lua"),
    LuaWorkspaceDocument.FromUtf8("game.service", service, "@game/service.lua"),
};

using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
{
    LanguageVersion = LuaLanguageVersion.Lua54,
    Compiler = compilerOptions,
    MaximumModuleCount = 128,
    MaximumDependencyCount = 512,
    MaximumFixedPointIterations = 8,
    MaximumParallelism = 4,
});

LuaWorkspaceResult snapshot = await workspace.AnalyzeAsync(documents, cancellationToken);
```

The module name is the `require` graph identity. `SourceIdentity` identifies the source origin for
diagnostics and persistence; it should not be an unstable temporary or absolute deployment path.
The source content hash is derived from bytes. Keep module names unique in a snapshot.

For disk modules, `LuaFileSystemModuleResolver` maps a request through `?.lua` and `?/init.lua`
under root-confined directories. A request such as `game.player` therefore maps to
`game/player.lua` or `game/player/init.lua`. Custom resolvers should return the same logical module
for the same request and honor cancellation.

## Cycles, cache reuse, and invalidation

Workspace results include graph strongly connected components. `IsCyclic` identifies components
that require fixed-point analysis. For each module, persist the exported type/hash,
`FixedPointIterationCount`, and `WasWidened`; also retain workspace diagnostics such as a bounded
fixed-point warning.

Reuse a `LuaWorkspace` for successive immutable snapshots in the same project/cache domain.
`LuaWorkspaceMetrics` distinguishes cache hits, misses, invalidated modules, fixed-point
iterations, and peak internal parallelism. An unchanged snapshot should produce cache hits. When a
module's export changes, its dependents are invalidated conservatively. `ClearCache()` deliberately
drops reuse state.

`FindReferences(LuaSymbolKey)`, `FindGlobalReferences(string)`, and `GetCallGraph()` project code
indexes across the completed workspace and include module/source identities and stable containing
function keys.

## Lifetime, concurrency, diagnostics, and budgets

- Compilation and workspace result objects are immutable snapshots and can be read concurrently.
- A `LuaWorkspace` accepts concurrent callers but serializes top-level operations to preserve cache
  ordering; `MaximumParallelism` controls work inside an analysis operation.
- Reuse the workspace instead of constructing one per edit. Dispose it when the cache domain ends.
  An already active operation may finish during disposal; new operations are rejected.
- Propagate `CancellationToken` through compiler, workspace, and resolver calls.
- Store compilation diagnostics with their `LuaCompilationPhase`. Store workspace diagnostics with
  `LuaWorkspaceDiagnosticPhase`, optional nested compilation phase, module, code, severity, span,
  and source identity.
- Set compiler type/constraint/CFG/generic budgets and workspace module/dependency/source/cache/
  fixed-point/diagnostic budgets for untrusted or large projects. Treat an exhausted budget or
  widening as explicit analysis state, not as a successful precise result.

The complete, compiling implementation of every operation above is in
[`samples/Lunil.StaticAnalysis.Embedding`](../samples/Lunil.StaticAnalysis.Embedding/EmbeddingScenario.cs).
