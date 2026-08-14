# Debug Lua scripts

Lunil ships a Debug Adapter Protocol server. Create a launch configuration
(Run and Debug → create a launch.json → Lunil Lua) or pick one of the snippets:

* **Launch script** runs a `.lua` file with the Lunil interpreter.
* **Attach to host** connects to a Lunil game-loop host over a named pipe.

Breakpoints, stepping, pause, stack traces, locals, and upvalues are supported.
