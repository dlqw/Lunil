# PUC-Lua 5.4.8 observable goldens

These Lua chunks form the cross-backend observable corpus. `goldens.json` stores
the primitive return values produced by an independently built PUC-Lua 5.4.8
executable. The test suite always verifies the committed source hashes, executes
every chunk on all six Lunil backends, and compares their observations with the
committed oracle values.

Regenerate the oracle only from a binary whose provenance is already known:

```powershell
./scripts/Update-PucLua548Goldens.ps1 `
  -LuaExecutable ./artifacts/puc-lua548/lua-5.4.8/src/lua.exe `
  -ExpectedExecutableSha256 A50CA50353BD4FCFB26C5683F7B1059C2B9C6A59F2DDB0234CAF309B4807E9DB
```

The updater refuses binaries that do not match the supplied SHA-256, do not
report `Lua 5.4.8`, or expose an `_VERSION` other than `Lua 5.4`.
