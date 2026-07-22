# Persisted CIL artifact model

This architecture note records the compatibility rules for persisted CIL artifacts. Lunil's public execution model uses canonical IR and runtime-selected managed or dynamic execution rather than persisted Lua AOT artifacts.

A persisted artifact binds to canonical IR identity, runtime ABI identity, language version, and manifest checksum. A loader validates every identity before exposing a delegate. Delegates remain block executors beneath the shared scheduler and preserve frame validation, budget accounting, safepoints, debug/hook checks, error exits, and deoptimization.

Artifact loading must respect host capabilities, trimming, NativeAOT, and collectible-context boundaries. Caches and delegates cannot retain a Lua state, frame, closure, or obsolete module generation.
