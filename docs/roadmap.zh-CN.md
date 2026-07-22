# Lunil 架构指南

[English](roadmap.md)

## 语言兼容性

宿主选择 `LuaLanguageVersion` 后，该身份会贯穿源代码解析、binary chunk 加载、canonical IR、运行时状态和标准库安装。未显式选择时默认 Lua 5.4。某个版本的 module 或 chunk 不能在另一版本的 state 中执行，更不能静默继承相邻版本的行为。

各版本 adapter 分别负责语法门控、词法行为、指令格式、binary chunk codec 和标准库接口。共享 canonical IR 与 scheduler 是转换边界，因此版本特有行为会在进入普通执行前完成转换。

## 执行架构

解释器是参考执行器。Tier 1、Tier 2 和 Loop OSR 都是可选的 block executor，位于同一个可恢复 scheduler 之下。scheduler 负责 frame 生命周期、coroutine、call、错误、close、hook、逻辑 GC 和 host 边界值。

生成代码在使用前会验证 frame 与运行时假设，并保留 canonical program counter 和 64 位 instruction accounting。guard 失败或 dynamic code 不可用时，执行会回到 canonical executor，Lua 可观察行为不变。

## 优化安全性

数值特化使用已验证 region、运行时 guard、共享 ABI helper 和明确的 deoptimization point。table PIC 与 direct compiled call 均为有界、generation-aware 且弱引用持有的缓存。profile 只能标识候选项，不能证明当前运行时值；cache entry 不会保留 Lua state 或已退役 module。

## Hosting 与互操作

Host 控制执行策略、instruction budget、语言版本、module identity 和可选 CLR capability。CLR 互操作采用 capability scope：host 必须明确公开可见类型和成员，Lua 契约不提供无限制 reflection 或任意 assembly 加载。

当 dynamic code 不可用时，NativeAOT 和 trimmed host 通过 managed execution path 获得相同的公开语义。module 发布和 function slot 使用 content identity 与 generation，避免过期编译入口在替换后继续执行。

生产热更新通过带签名且受资源上限约束的 Patch Bundle 进入。隔离后台 prepare 会把 bundle 绑定到
在线 module 的 expected revision，随后由游戏循环 update window 原子发布依赖有序的 module 集合。
已挂起 frame 保留其捕获的代码 generation，commit 后新进入的调用则观察 replacement generation。
签名 state schema 与可逆 host adapter 会在同一 publication journal 中迁移 Lua table、userdata
payload、coroutine、timer、subscription 和 task。进程内 coordinator 进一步提供全 State barrier
 发布、顺序 canary/ring 灰度、health gate 回滚，以及由 host 核对 crash outcome 的持久 hash-chain
 deployment journal。Journal 通过生命周期 sidecar lock 强制单 writer，同时支持并发 verified reader，
 并使用原子、可配置 retention 的 compaction，避免长期运行的部署服务永久耗尽 active file。

## 设计文档导览

[架构说明](adr/) 介绍 execution ABI、数值特化、invalidation、heap identity 与语言 adapter。编译器、runtime continuation、conformance 和互操作文档给出各公开边界的 API 与使用细节。
