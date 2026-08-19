# Connect a host contract

Hosts such as Unity, Godot, or a .NET application can export a versioned Lunil host
contract describing their global symbols, modules, callbacks, and persistence
schemas. Point Lunil at it with the `lunil.hostContractPath` setting (or inline JSON
with `lunil.hostContractJson`) and the host's API becomes part of completion,
hover, and diagnostics.

<a href="command:lunil.showHostContract">Show the virtual host contract</a> renders
the currently indexed host surface as a Lua document.

<a href="command:workbench.action.openSettings?%22lunil.hostContractPath%22">Open the host contract setting</a>
