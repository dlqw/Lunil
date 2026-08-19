# Index your workspace

When a workspace containing `.lua` files opens, Lunil analyzes every module,
builds the cross-module call and reference index, and keeps diagnostics current.
Large repositories index incrementally with a disk cache.

The status bar shows indexing progress with a percentage. Once indexing completes,
the status item returns to the ready state.

<a href="command:lunil.showIndexStatus">Show index status</a> lists failed and
pending documents with the failure reason. Use **Retry Failed** (or the restart
button on a failed file) to re-analyze them, and
<a href="command:lunil.reindexWorkspace">Reindex workspace</a> to rebuild the
whole index on demand.
