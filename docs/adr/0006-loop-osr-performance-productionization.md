# Guarded exact-numeric loop OSR

Loop on-stack replacement transfers a hot running loop to a compiled loop entry while retaining the existing frame and scheduler boundary. A request is recorded after a completed backedge and consumed only after the frame commits the loop-header PC.

Eligible loops have verified natural-loop structure and a canonical block-leader header. The generated form supports guarded exact numeric operations, constants, moves, frame-top updates, canonical branches, and guarded close behavior. Unsupported code uses canonical execution.

Transitions never occur in the middle of an instruction. Guard failure, debug/hook activation, safepoints, errors, yields, and invalidation use shared runtime exits. OSR is separately configurable and does not alter Lua-visible semantics.
