# 类型检查参考

[English](type-checking.pub.md)

Lunil 注解驱动类型检查的参考：产生的诊断、抑制面与检查边界。类型检查对携带 EmmyLua 风格
注解的 Lua 文件**默认启用**；无注解文件不受影响。`---@type`、`---@param`、`---@return`、
`---@class`、`---@alias`、`---@enum` 与 `---@cast` 注解输入一个有界流分析
（`LuaTypeAnalyzer`），在 `LUA6000` 线报告诊断；workspace 还会对带注解的 `require` 消费方
额外检查跨模块一致性。`---@class` 名称可以带点号（例如 `---@class host.Engine.Utility.TimeUtil`），
用于生成的宿主 API stub；完整点号路径就是 class 名，参与 navigation、hover、reference 与
semantic token。

## 诊断

| 码 | 报告内容 |
| --- | --- |
| `LUA6001` | 未知注解类型名。 |
| `LUA6002` | 类型名重复声明。 |
| `LUA6003` | 值不可赋给注解类型：参数、返回值、初始化、赋值或操作数。 |
| `LUA6004` | 已知类型的值不可调用。 |
| `LUA6006` | 调用实参数量与所选签名不匹配。 |
| `LUA6007` | 索引访问没有静态暴露的值。 |
| `LUA6008` | 局部变量在显式赋值前被读取。 |
| `LUA6009` | 当前流类型下语句不可达。 |
| `LUA6010` | 静态分析超过配置预算，剩余值被拓宽为 `unknown`。 |
| `LUA6012` | 递归类型声明被拓宽为 `unknown`。 |
| `LUA6013` | `---@cast` 产生不可能类型 `never`。 |
| `LUA6014` | 参数具有隐式类型 `any`（启用隐式 any 报告时）。 |
| `LUA6015` | 全局变量没有已知静态类型（启用未知全局报告时）。 |
| `LUA6016` | 当前流类型下条件恒真或恒假。 |
| `LUA6017` | 冒号调用给未声明 `self` 的函数传隐式 `self`。 |
| `LUA6018` | 点调用省略了冒号方法要求的隐式 `self`。 |
| `LUA6019` | 运行时原型成员与其类注解类型冲突。 |
| `LUA6020` | 访问路径在此访问前可能为 `nil`。 |
| `LUA6022` | `require` 消费方的 `---@type` 注解不可赋给模块导出类型（workspace 诊断）。 |

诊断默认为警告，可通过 `--warnings-as-errors` 提升为错误。报告是保守的：`any` 与 `unknown`
值从不产生失配，union 逐成员检查，递归声明拓宽而非循环。

## 跨模块一致性（LUA6022）

当模块在 `require` 局部变量上声明类型时，workspace 将声明类型与解析到的目标模块导出类型
比较：

```lua
---@type { value: string }   -- 不匹配：模块导出 { value = 42 }
local dep = require('dep')
return dep.value
```

只报告明确不兼容：未解析的注解名与 `any`/`unknown` 声明或导出类型一律跳过，无类型代码不会
产生噪音。

## 抑制

通过 `SuppressedDiagnosticCodes` 通路抑制指定码：

| 面 | 配置 |
| --- | --- |
| CLI | 每个码重复 `--suppress <code>`，例如 `lunil check src --suppress LUA6022 --suppress LUA6016`。 |
| Language server / VS Code | `lunil.server.suppressedDiagnosticCodes` 码数组。 |
| 宿主（嵌入） | `LuaAnalysisOptions.SuppressedDiagnosticCodes`。 |

```json
{
  "lunil.server.suppressedDiagnosticCodes": ["LUA6022", "LUA6016"]
}
```

## 边界

v1 检查不做泛型实例化推断与约束求解，不进行导出一致性（`LUA6022`）之外的跨模块类型检查，
也不以类型驱动补全排序。

## 参见

- [调试参考](debugging-reference.zh-CN.pub.md) — DAP 协议面。
- [分析事实](analysis-facts.zh-CN.pub.md) — `LuaSemanticModel` 与 `LuaAnalysisResult`
  暴露的事实。
- [静态分析嵌入](static-analysis-embedding.zh-CN.pub.md) — 从 .NET 运行分析。
- [CLI 参考](cli.zh-CN.pub.md) — `--suppress` 与源码命令相关选项。
- [迁移指南](migration-0.16.0.zh-CN.pub.md) — 0.16 的变化。
