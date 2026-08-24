# 从 Lunil 0.17 迁移到 0.18

[English](migration-0.18.0.pub.md)

Lunil 0.18 保持 0.17 的 compiler、runtime、hosting、analysis 与 engine 入口源码兼容。本版本聚焦
workspace 级编辑器智能：可配置的 require 搜索根、class factory 识别、用于生成宿主 stub 的带点号
注解类名，以及新的 VS Code 生产力命令。升级时需要注意两处展示变化：hover card 改用紧凑 Markdown
表格，结构表 hover 显示成员摘要而不是只显示 `table`。

## 1. 更新包与工具

将全部 Lunil 包引用更新到同一兼容线：

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.18.0" />
<PackageReference Include="Lunil.Hosting" Version="0.18.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.18.0
```

## 2. 通过搜索根解析 require

`lunil.require.searchPaths` 让 workspace 可以把 `require("A.B.C")` 解析到带前缀的 module 标识，
例如 `Libs.client.A.B.C`。原始名称始终最先尝试，然后按顺序尝试每个配置的根。这是可选行为：默认
为空时，require 字符串仍必须精确匹配 module 标识。

```json
{
  "lunil.require.searchPaths": ["scripts/client", "scripts/shared"]
}
```

修改该设置会无重启地重新扫描 workspace。

## 3. 识别 class factory

`lunil.analysis.classFactories` 告诉分析器哪些全局函数定义 class。factory 的第一个字符串字面量参数
是 class 名；当 `baseArguments` 为 `true` 时，其余裸标识符参数是基类。

```json
{
  "lunil.analysis.classFactories": [
    "defineView",
    { "name": "class", "baseArguments": true }
  ]
}
```

没有该设置时，`local X = class("Name", Base)` 只是普通调用。启用后，hover 会显示 class card，对
`X` 的成员写入会定义方法，`X.new()` 会产生实例，基类成员可解析，class hierarchy 命令也会包含该
class。

## 4. 带点号的注解类名

生成的宿主 API stub 可以声明带点号的 class，例如 `---@class host.Engine.Utility.TimeUtil`。完整
点号路径就是 class 名，因此 navigation、hover、reference 与 semantic token 会把 namespace 路径
视为一个 class 标识，而不是只使用最后一段。

## 5. 兼容性清单

- 没有移除或重签名任何 public member；0.17 API 面保持源码兼容。
- 新增公共 API：`LuaWorkspaceOptions.RequireSearchPaths`、
  `LuaWorkspaceOptions.ClassFactoryCalls`、`LuaAnalysisEnvironment.ClassFactoryCalls`、
  `RequireNameExpansion`、Lunil.Workspace 中的 compact snapshot 保存/恢复与 contribution
  adoption API。
- `api/0.18.0/` 基线取代 `api/0.17.0/` 成为冻结的兼容线。
- Hover card 现在用 Markdown 表格渲染 module 与继承元数据，而不是粗体内联标签；抓取 hover
  Markdown 的脚本应改用新布局。
- 结构表 hover 会在表足够小、可读时给出成员摘要（例如 `config: {width, height, depth, …}` 加
  成员列表），而不是只显示 `config: table`。
- VS Code 插件新增 `lunil.require.searchPaths`、`lunil.analysis.classFactories`、
  `lunil.searchEverywhere`、`lunil.classHierarchy` 与 `lunil.findUsages`。