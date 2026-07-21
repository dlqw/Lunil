# CLR 互操作

Lunil 0.11 在 `Lunil.Hosting` 中增加 opt-in CLR bridge。该 bridge 默认禁用，也不会按名称加载
assembly。Host 必须同时允许一个已经加载的 assembly，以及 Lua 可以发现或构造的每个 CLR 类型。

## 配置 Host

Assembly 与类型 allowlist 使用区分大小写的精确名称。`InstallGlobalModule` 决定是否发布 Lua
全局 `clr` table；未安装全局 table 时，嵌入应用仍可通过 `LuaHost.ClrBridge` 使用同一个 bridge。

```csharp
using Lunil.Hosting;

var options = LuaHostOptions.Restricted with
{
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.TypeDiscovery |
            LuaClrCapabilities.Construction,
        AllowedAssemblyNames = ["Example.Contracts"],
        AllowedTypeNames = ["Example.Contracts.Point"],
        InstallGlobalModule = true,
        OwnConstructedObjects = true,
    },
};

using var host = new LuaHost(options);
var run = host.RunUtf8("""
    local info = clr.type("Example.Contracts.Point")
    local point = clr.new("Example.Contracts.Point", 10, 20)
    return info.name, info.assembly, info.constructible, type(point)
    """);
```

`LuaClrOptions.Disabled` 不授予任何 CLR capability。启用发现或构造却没有提供 assembly allowlist
时，bridge 会在创建阶段拒绝配置。类型名受 `MaximumTypeNameLength` 限制，允许范围为 1 到 4096
个字符。

## Lua module

安装后的全局 `clr` table 提供两个函数：

- `clr.type(fullName)` 返回 `name`、`assembly`、`value_type`、`constructible` 和
  `constructors` 数组。每个 constructor entry 都包含一个由 CLR 参数类型名组成的 `parameters`
  数组。
- `clr.new(fullName, ...)` 选择 public constructor，并返回 payload 为 `LuaClrObject` 的 full
  userdata。

该 module 不暴露 member、reflection object、assembly loading 或无限制类型查询。Member access
与 callback 不属于 alpha.1 契约。

## Constructor 选择与转换

只有参数数量与 Lua 实参数量相同的 public instance constructor 会参与选择。Lunil 转换所有实参，
选择总转换成本最低的 candidate，并以 CLR 参数类型签名的 ordinal 顺序打破平局。因此结果不依赖
reflection 的枚举顺序。

alpha.1 的转换范围如下：

| Lua value | CLR target |
| --- | --- |
| `nil` | 引用类型与 `Nullable<T>` |
| Boolean | `bool` |
| String | `string`、单字符 `char` 或精确 enum 名称 |
| Integer 或 float | CLR 数值类型；integer 还可以初始化 enum |
| CLR userdata | 当 wrapped instance 可赋给参数类型时使用该 instance |
| 任意 Lua value | `LuaValue`；primitive value 还可回退为 `object` |

不受支持的 value、溢出和没有完整可转换实参列表的 constructor 会产生
`LuaClrErrorCode.NoMatchingConstructor`。Constructor 抛出的异常会产生
`LuaClrErrorCode.ConstructionFailed`，并在嵌入应用侧通过 `InnerException` 保留原始异常。经 Lua
module 调用时，错误代码会出现在可由 Lua 捕获的错误消息中。

## Ownership

`clr.new` 使用 `LuaClrObject` 包装构造出的 instance。默认
`OwnConstructedObjects = true`；userdata 被回收或显式释放 payload 时，
`IDisposable.Dispose` 最多调用一次。如果 instance 由嵌入应用管理，请把该选项设为 `false`。
两种模式下 wrapper 的 `Dispose` 都是幂等的。

Bridge 及其 userdata 只属于一个 `LuaState`；常规 owner 校验会阻止 Lua value 安装到其他 state。

## 发布约束

Bridge 只搜索 `AppDomain.CurrentDomain.GetAssemblies()`，并精确匹配 assembly simple name 与类型
full name。使用 trimming 或 NativeAOT 发布的应用必须通过自己的 linker metadata 保留每个
allowlist 类型的 constructor。Bridge 不增加 dynamic dependency，也不会在 metadata 缺失时替换成
其他类型。

对于静态已知类型，可以在 rooted method 上显式声明依赖：

```csharp
using System.Diagnostics.CodeAnalysis;

[DynamicDependency(
    DynamicallyAccessedMemberTypes.PublicConstructors,
    typeof(Example.Contracts.Point))]
static void PreserveClrBridgeTypes()
{
}
```
