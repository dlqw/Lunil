# NativeAOT 与 MSBuild 构建集成

`Lunil.Build` 将 Lua source 或 PUC Lua 5.4 binary chunk 在宿主 `CoreCompile` 前转换为
verified canonical IR、persisted CIL、可选 Portable PDB、canonical module payload 和静态
C# registry。所有入口以直接 method group 注册，因此 trimming 和 NativeAOT 无需 runtime
reflection、assembly discovery 或动态 PE load。

## 引用与 item metadata

```xml
<ItemGroup>
  <PackageReference Include="Lunil.Build" Version="0.6.0-alpha.11" />
  <LunilCompile Include="Modules/main.lua"
                ModuleName="app.main"
                InputKind="Auto"
                Optimization="Release"
                DebugSymbols="false"
                Sandbox="Restricted" />
</ItemGroup>
```

| Metadata | 默认值 | 允许值 | 含义 |
| --- | --- | --- | --- |
| `ModuleName` | 文件名（无扩展名） | identifier-like dotted name | registry 的稳定模块名 |
| `InputKind` | `Auto` | `Auto`, `Source`, `BinaryChunk` | `Auto` 通过 Lua chunk signature 判定 |
| `Optimization` | 跟随 `Optimize` | `Debug`, `Release` | 构建 manifest 与后端配置 |
| `DebugSymbols` | 跟随 `DebugSymbols` | `true`, `false` | 是否生成 Portable PDB |
| `Sandbox` | `Default` | `Default`, `Trusted`, `Restricted` | 进入兼容键的宿主策略 metadata |

可用 property：

- `LunilBuildEnabled=false`：禁用 target；
- `LunilIntermediateOutputPath`：覆盖默认 `$(BaseIntermediateOutputPath)lunil/` root；
- `LunilOptimization`、`LunilDebugSymbols`、`LunilSandbox`：修改 item definition 默认值；
- `LunilCacheEnabled`：默认 `true`，控制跨 `Clean` 的内容寻址 backend cache；
- `LunilCacheDirectory`：覆盖用户级默认 cache root；
- `LunilCacheMaximumBytes`、`LunilCacheMaximumEntryBytes`、
  `LunilCacheMaximumQuarantineBytes`：分别控制总 entry、单 entry 与损坏隔离区 quota。

## 输出与增量语义

每个 configuration/TFM/RID 目录包含：

- `Lunil.Aot.<content>.<options>.dll`：确定性 persisted CIL；
- 同名 `.pdb`：仅在 `DebugSymbols=true` 时生成；
- 同名 `.lir`：已验证 canonical module；
- `<module>.<content>.lunil.json`：source/options/build manifest；
- `LunilModules.g.cs`：`ModuleInitializer` static registry；
- `LunilCompile.stamp`：当前 module name/content ID 集合。

task 每次执行以恢复 `Compile`、`Reference` 和 `FileWrites` item，但仅在 source bytes、input kind、
optimization/debug/sandbox、TFM 或 RID 改变时重新编译。相同内容不会改写文件时间。并发 build
通过 output lock 串行提交，并以同目录临时文件和 atomic replace 防止部分写入。design-time build
使用隔离目录，只生成空 registry skeleton；`Clean` 删除整个 Lunil intermediate root。

verified canonical module 与 persisted CIL/PDB bundle 同时进入用户级 backend cache。key 还纳入
IR/Runtime ABI/codegen/profile/artifact schema、compiler version、hook mode、NativeAOT/ReadyToRun、
trimming 与 feature set，因此不同 publish mode 不会错误复用。`Clean` 不删除共享 cache；obj 输出
被删除后可从 cache 恢复。恢复前重新验证 canonical IR、PE footer/resources/manifest 与 Portable PDB
binding；截断、checksum 错误或语义无效 payload 会隔离后重新编译。完整契约见
[`backend-cache-contract.md`](backend-cache-contract.md)。

## Runtime 使用

```csharp
if (!LuaStaticAotRegistry.TryGetModule("app.main", out var module) || module is null)
{
    throw new InvalidOperationException("Missing precompiled Lua module.");
}

var state = new LuaState();
var closure = module.CreateMainClosure(state);
var result = new LuaStaticAotExecutor().Execute(state, closure);
```

`LuaStaticAotExecutor` 按 canonical module content ID 查找静态函数。未注册模块返回
`UnsupportedInstruction` deopt，由共享 scheduler 只解释执行当前 canonical PC，不重复副作用。
NativeAOT 下 `LuaJitExecutor.IsDynamicCodeAvailable` 为 `false`；动态 persisted PE loader 返回
`AOT2010`，不会调用 `AssemblyLoadContext`。即使宿主配置 `EnableTier2=true` 或
`EnableLoopOsr=true`，dynamic-code capability gate 也会在 registry entry 创建前关闭对应路径：
不会采集 Tier 2 profile，也不会分析、预热或编译 Loop OSR。`EnableLoopOsrManagedFallback` 不改变
NativeAOT 行为；动态 Lua 模块继续精确使用解释器，预注册模块继续使用构建期 AOT。
CoreCLR 上即使显式开启 Loop OSR，natural-loop 分析和 specialized emitter 也会延后到 verified
backedge hotness 与运行时 exact-numeric 资格通过之后；该惰性路径不改变 NativeAOT capability gate。

## Publish mode

```bash
# NativeAOT
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishAot=true

# Trimmed self-contained single file
dotnet publish -c Release -r linux-x64 --self-contained true \
  -p:PublishTrimmed=true -p:PublishSingleFile=true

# ReadyToRun/CoreCLR
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishReadyToRun=true
```

仓库用以下命令执行同一 fixture corpus：

```powershell
./scripts/Test-NativeAotFixture.ps1 -RuntimeIdentifier win-x64
./scripts/Test-LunilPublishModes.ps1 -RuntimeIdentifier win-x64
```

CI 对 `win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`
分别 native publish 并运行；IL2026/IL3050 等 trimming/AOT warning 作为 error。

## 稳定诊断码

| Code | 含义 |
| --- | --- |
| `LUNIL1001` | source/chunk 文件不存在 |
| `LUNIL1002` | module name 非法 |
| `LUNIL1003` | optimization 非法 |
| `LUNIL1004` | debug-symbol value 非法 |
| `LUNIL1005` | sandbox value 非法 |
| `LUNIL1006` | module name 重复 |
| `LUNIL1007` | output path 非法 |
| `LUNIL1008` | input kind 非法 |
| `LUNIL2001` | frontend/canonical compilation 失败 |
| `LUNIL2002` | persisted artifact emission 失败 |
| `LUNIL2003` | binary chunk 无效 |
| `LUNIL9001` | 未预期 build-task failure |

Lexer/parser/binder/lowering 和 CIL verifier 的既有稳定 code 会原样报告。source 诊断使用
一基 line/UTF-16 column；binary chunk 错误报告一基 byte column。

## Cache 故障排查

- 默认 cache root 为 `LocalApplicationData/Lunil/backend-cache`；没有该系统目录时依次回退到
  `~/.cache/Lunil/backend-cache` 和临时目录。可用 `LunilCacheDirectory` 固定 CI 或容器路径。
- 需要确认问题是否来自 cache 时，先设置 `LunilCacheEnabled=false` 重建；禁用 cache 只影响
  构建性能，不改变 artifact 或运行语义。
- `obj/lunil/` 可由 `Clean` 删除；共享 cache 不属于项目输出。需要完全冷构建时应显式使用一个
  新的空 `LunilCacheDirectory`，不要依赖 `Clean` 删除用户级数据。
- `quarantine/` 中的目录表示 descriptor、payload、manifest、checksum、canonical IR、PE/PDB
  binding 或兼容维度验证失败。task 会安全 miss 并重新编译；不要把隔离内容手工移回 `entries-v1/`。
- cache lock/权限/I/O 故障以低重要性消息 fail-soft，构建继续走本地编译。若持续发生，检查目录
  ACL、磁盘配额、残留进程，并把 cache root 指向当前用户可独占写入的本地文件系统。
