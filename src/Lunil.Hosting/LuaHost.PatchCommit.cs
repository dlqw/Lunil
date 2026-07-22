using System.Collections.Immutable;
using System.Diagnostics;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

public sealed partial class LuaHost
{
    private LuaPatchUpdateWindow? _activePatchUpdateWindow;
    private readonly Dictionary<string, string> _patchStateSchemaVersions =
        new(StringComparer.Ordinal);

    /// <summary>Registers the live version for a host-owned state schema.</summary>
    public void SetPatchStateSchemaVersion(string schemaId, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_executionGate)
        {
            ThrowIfDisposed();
            if (!State.IsIdle)
            {
                throw new InvalidOperationException(
                    "A state schema version can change only while the Lua state is idle.");
            }

            _patchStateSchemaVersions[schemaId] = version;
        }
    }

    public bool TryGetPatchStateSchemaVersion(string schemaId, out string? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        lock (_executionGate)
        {
            ThrowIfDisposed();
            return _patchStateSchemaVersions.TryGetValue(schemaId, out version);
        }
    }

    /// <summary>
    /// Compiles every patch module in an isolated host and then captures the live module revisions
    /// under the execution gate. No candidate code is executed on the live state.
    /// </summary>
    public LuaPatchPrepareResult PreparePatch(
        LuaPatchBundle bundle,
        LuaPatchPrepareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var snapshot = SnapshotPrepareOptions(options ?? LuaPatchPrepareOptions.Default);
        return PreparePatchCore(bundle, snapshot, cancellationToken);
    }

    /// <summary>
    /// Performs isolated compilation on a worker thread, then briefly enters the execution gate to
    /// bind expected revisions. Cancellation never publishes a live-state change.
    /// </summary>
    public Task<LuaPatchPrepareResult> PreparePatchAsync(
        LuaPatchBundle bundle,
        LuaPatchPrepareOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var snapshot = SnapshotPrepareOptions(options ?? LuaPatchPrepareOptions.Default);
        return Task.Run(
            () => PreparePatchCore(bundle, snapshot, cancellationToken),
            cancellationToken);
    }

    private LuaPatchPrepareResult PreparePatchCore(
        LuaPatchBundle bundle,
        LuaPatchPrepareOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = LuaPatchTelemetry.Start(
            "lunil.patch.prepare",
            bundle.Manifest.PatchId);
        try
        {
            options.ResourceLimits.Validate();
            var moduleCount = bundle.Entries.Count(static entry => entry.ModuleName is not null);
            LuaPatchResourceLimits.EnsureWithin(
                nameof(options.ResourceLimits.MaximumPatchModules),
                moduleCount,
                options.ResourceLimits.MaximumPatchModules);
            activity?.SetTag("lunil.patch.module_count", moduleCount);
            var migrationSchema = LuaPatchMigrationSchemaSerializer.ReadFromBundle(
                bundle,
                options.ResourceLimits);
            var preflight = LuaPatchPreflight.Analyze(
                bundle,
                Options,
                options.CanonicalIrDecoder,
                cancellationToken);
            var result = BindPreparedPatch(
                preflight,
                migrationSchema,
                options,
                cancellationToken);
            var duration = Stopwatch.GetElapsedTime(started);
            LuaPatchTelemetry.Complete(activity, result.Status.ToString(), result.Message);
            LuaPatchTelemetry.RecordPreparation(result.Status.ToString(), duration);
            return result;
        }
        catch (Exception exception) when (IsRecoverablePatchException(exception))
        {
            LuaPatchTelemetry.Failed(activity, exception);
            LuaPatchTelemetry.RecordPreparation("Exception", Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <summary>
    /// Attempts to reserve a same-thread game-loop safe point. A successful window retains the
    /// host execution gate until the returned object is disposed.
    /// </summary>
    public LuaPatchUpdateWindowResult TryOpenPatchUpdateWindow(
        LuaPatchUpdateWindowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= LuaPatchUpdateWindowOptions.Default;
        ValidateDuration(options.WaitTimeout, allowInfinite: true, nameof(options));
        ValidateDuration(options.MaximumDuration, allowInfinite: true, nameof(options));

        var waitStarted = Stopwatch.GetTimestamp();
        var entered = false;
        try
        {
            while (!entered)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new LuaPatchUpdateWindowResult(
                        LuaPatchUpdateWindowStatus.Cancelled,
                        null,
                        "Update-window acquisition was cancelled.");
                }

                var remaining = Remaining(options.WaitTimeout, waitStarted);
                if (remaining == TimeSpan.Zero)
                {
                    entered = Monitor.TryEnter(_executionGate);
                    if (!entered)
                    {
                        return new LuaPatchUpdateWindowResult(
                            LuaPatchUpdateWindowStatus.Deferred,
                            null,
                            "The host is executing; defer the patch to a later update window.");
                    }

                    break;
                }

                var slice = remaining == Timeout.InfiniteTimeSpan ||
                    remaining > TimeSpan.FromMilliseconds(25)
                    ? TimeSpan.FromMilliseconds(25)
                    : remaining;
                entered = Monitor.TryEnter(_executionGate, slice);
                if (!entered && Remaining(options.WaitTimeout, waitStarted) == TimeSpan.Zero)
                {
                    return new LuaPatchUpdateWindowResult(
                        LuaPatchUpdateWindowStatus.Deferred,
                        null,
                        "The update-window wait budget elapsed while the host was executing.");
                }
            }

            ThrowIfDisposed();
            if (_activePatchUpdateWindow is not null)
            {
                return new LuaPatchUpdateWindowResult(
                    LuaPatchUpdateWindowStatus.Deferred,
                    null,
                    "A patch update window is already active.");
            }

            if (!State.IsIdle)
            {
                return new LuaPatchUpdateWindowResult(
                    LuaPatchUpdateWindowStatus.Deferred,
                    null,
                    "The Lua state is not at a safe point.");
            }

            var window = new LuaPatchUpdateWindow(this, options.MaximumDuration);
            _activePatchUpdateWindow = window;
            entered = false; // Ownership of the monitor passes to the window.
            return new LuaPatchUpdateWindowResult(
                LuaPatchUpdateWindowStatus.Opened,
                window,
                null);
        }
        finally
        {
            if (entered)
            {
                Monitor.Exit(_executionGate);
            }
        }
    }

    /// <summary>
    /// Executes all candidates against a temporary dependency-first cache overlay, then publishes
    /// cache records, table patches, and closure generations as one rollback-capable transaction.
    /// </summary>
    public LuaPatchCommitResult CommitPatch(
        LuaPreparedPatch preparedPatch,
        LuaPatchUpdateWindow updateWindow,
        LuaPatchCommitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparedPatch);
        ArgumentNullException.ThrowIfNull(updateWindow);
        options ??= LuaPatchCommitOptions.Default;
        ValidateDuration(options.MaximumPauseDuration, allowInfinite: true, nameof(options));
        if (!ReferenceEquals(preparedPatch.Owner, this))
        {
            throw new ArgumentException(
                "The prepared patch belongs to a different Lua host.",
                nameof(preparedPatch));
        }

        var started = Stopwatch.GetTimestamp();
        using var activity = LuaPatchTelemetry.Start(
            "lunil.patch.commit",
            preparedPatch.Manifest.PatchId);
        activity?.SetTag("lunil.patch.module_count", preparedPatch.Modules.Length);
        try
        {
            var result = CommitPatchCore(
                preparedPatch,
                updateWindow,
                options,
                cancellationToken);
            LuaPatchTelemetry.Complete(activity, result.Status.ToString(), result.Message);
            LuaPatchTelemetry.RecordCommit(
                result.Status.ToString(),
                result.PauseDuration,
                result.SideEffectsMayHaveOccurred && !result.Succeeded);
            return result;
        }
        catch (Exception exception) when (IsRecoverablePatchException(exception))
        {
            LuaPatchTelemetry.Failed(activity, exception);
            LuaPatchTelemetry.RecordCommit(
                "Exception",
                Stopwatch.GetElapsedTime(started),
                rollbackAttempted: false);
            throw;
        }
    }

    private LuaPatchCommitResult CommitPatchCore(
        LuaPreparedPatch preparedPatch,
        LuaPatchUpdateWindow updateWindow,
        LuaPatchCommitOptions options,
        CancellationToken cancellationToken)
    {
        var preparation = PreparePatchCommitSession(
            preparedPatch,
            updateWindow,
            options,
            cancellationToken);
        if (preparation.Failure is not null)
        {
            return preparation.Failure;
        }

        using var session = preparation.Session!;
        var publishFailure = session.Publish(cancellationToken);
        if (publishFailure is not null)
        {
            return publishFailure;
        }

        try
        {
            session.FinalizePublication();
            return session.BuildCommittedResult();
        }
        catch (Exception exception) when (IsRecoverablePatchException(exception))
        {
            return session.Rollback(
                LuaPatchCommitStatus.PublicationFailed,
                exception.Message,
                sideEffectsMayHaveOccurred: true);
        }
    }

    internal PatchCommitSessionPreparation PreparePatchCommitSession(
        LuaPreparedPatch preparedPatch,
        LuaPatchUpdateWindow updateWindow,
        LuaPatchCommitOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedPatch);
        ArgumentNullException.ThrowIfNull(updateWindow);
        ArgumentNullException.ThrowIfNull(options);
        if (!ReferenceEquals(preparedPatch.Owner, this))
        {
            throw new ArgumentException(
                "The prepared patch belongs to a different Lua host.",
                nameof(preparedPatch));
        }

        ValidateDuration(options.MaximumPauseDuration, allowInfinite: true, nameof(options));
        ValidateActiveWindow(updateWindow);
        ThrowIfDisposed();
        var started = Stopwatch.GetTimestamp();
        if (!State.IsIdle)
        {
            return PatchCommitSessionPreparation.FromFailure(EmptyCommitResult(
                preparedPatch,
                LuaPatchCommitStatus.Deferred,
                "The Lua state left the update safe point.",
                started));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return PatchCommitSessionPreparation.FromFailure(EmptyCommitResult(
                preparedPatch,
                LuaPatchCommitStatus.Cancelled,
                "Patch commit was cancelled before execution.",
                started));
        }

        if (BudgetExceeded(updateWindow, options.MaximumPauseDuration, started))
        {
            return PatchCommitSessionPreparation.FromFailure(EmptyCommitResult(
                preparedPatch,
                LuaPatchCommitStatus.Deferred,
                "The patch pause budget was exhausted before execution.",
                started));
        }

        var observed = new Dictionary<string, LuaModuleRecord>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in preparedPatch.Modules)
        {
            if (!State.TryGetModule(module.ModuleName, out var record) || record is null)
            {
                conflicts.Add(module.ModuleName);
                continue;
            }

            observed.Add(module.ModuleName, record);
            if (record.Revision != module.ExpectedRevision)
            {
                conflicts.Add(module.ModuleName);
            }
        }

        if (conflicts.Count != 0)
        {
            var results = preparedPatch.Modules.Select(module =>
            {
                observed.TryGetValue(module.ModuleName, out var record);
                return new LuaPatchModuleCommitResult(
                    module.ModuleName,
                    conflicts.Contains(module.ModuleName)
                        ? LuaPatchModuleCommitStatus.RevisionConflict
                        : LuaPatchModuleCommitStatus.NotExecuted,
                    module.ExpectedRevision,
                    record?.Revision,
                    record,
                    null,
                    null,
                    conflicts.Contains(module.ModuleName)
                        ? "The live module revision no longer matches the prepared revision."
                        : null,
                    0,
                    0,
                    0,
                    0);
            }).ToImmutableArray();
            return PatchCommitSessionPreparation.FromFailure(new LuaPatchCommitResult(
                preparedPatch.Manifest.PatchId,
                LuaPatchCommitStatus.RevisionConflict,
                results,
                "At least one module changed after patch preparation.",
                SideEffectsMayHaveOccurred: false,
                Stopwatch.GetElapsedTime(started)));
        }

        if (preparedPatch.MigrationSchema is { } liveSchema &&
            (!_patchStateSchemaVersions.TryGetValue(liveSchema.SchemaId, out var schemaVersion) ||
                !string.Equals(
                    schemaVersion,
                    preparedPatch.ExpectedStateSchemaVersion,
                    StringComparison.Ordinal)))
        {
            return PatchCommitSessionPreparation.FromFailure(EmptyCommitResult(
                preparedPatch,
                LuaPatchCommitStatus.RevisionConflict,
                "The live state schema version no longer matches the prepared patch.",
                started));
        }

        var transactions = new List<PatchModuleTransaction>(preparedPatch.Modules.Length);
        var transferred = false;
        try
        {
            foreach (var module in preparedPatch.Modules)
            {
                transactions.Add(new PatchModuleTransaction(
                    this,
                    module,
                    observed[module.ModuleName]));
            }

            NormalizeTargetModules(transactions, stagedCount: 0);
            for (var index = 0; index < transactions.Count; index++)
            {
                var transaction = transactions[index];
                if (TryStopBeforePublication(
                    preparedPatch,
                    updateWindow,
                    options,
                    started,
                    transactions,
                    out var stopped,
                    cancellationToken))
                {
                    return PatchCommitSessionPreparation.FromFailure(stopped!);
                }

                try
                {
                    transaction.ExecuteCandidate();
                }
                catch (Exception exception) when (IsRecoverablePatchException(exception))
                {
                    transaction.FailureStatus = LuaPatchModuleCommitStatus.ExecutionFailed;
                    transaction.Message = exception.Message;
                    var restored = RollbackTransactions(transactions);
                    return PatchCommitSessionPreparation.FromFailure(BuildFailedCommit(
                        preparedPatch,
                        transactions,
                        restored ? LuaPatchCommitStatus.ExecutionFailed :
                            LuaPatchCommitStatus.PublicationFailed,
                        exception.Message,
                        sideEffectsMayHaveOccurred: true,
                        started));
                }

                if (transaction.Execution!.Signal != LuaVmSignal.Completed)
                {
                    transaction.FailureStatus = LuaPatchModuleCommitStatus.ExecutionFailed;
                    transaction.Message = transaction.Execution.Values.IsEmpty
                        ? "The replacement loader failed."
                        : transaction.Execution.Values[0].ToString();
                    var restored = RollbackTransactions(transactions);
                    return PatchCommitSessionPreparation.FromFailure(BuildFailedCommit(
                        preparedPatch,
                        transactions,
                        restored ? LuaPatchCommitStatus.ExecutionFailed :
                            LuaPatchCommitStatus.PublicationFailed,
                        transaction.Message,
                        sideEffectsMayHaveOccurred: true,
                        started));
                }

                try
                {
                    transaction.ApplyMigrations(
                        preparedPatch.StateMigrationAdapters,
                        preparedPatch.ResourceMigrationAdapters);
                }
                catch (Exception exception) when (IsRecoverablePatchException(exception))
                {
                    transaction.FailureStatus = LuaPatchModuleCommitStatus.MigrationFailed;
                    transaction.Message = exception.Message;
                    var restored = RollbackTransactions(transactions);
                    return PatchCommitSessionPreparation.FromFailure(BuildFailedCommit(
                        preparedPatch,
                        transactions,
                        restored ? LuaPatchCommitStatus.MigrationFailed :
                            LuaPatchCommitStatus.PublicationFailed,
                        exception.Message,
                        sideEffectsMayHaveOccurred: true,
                        started));
                }

                NormalizeTargetModules(transactions, stagedCount: index + 1);
            }

            NormalizeTargetModules(transactions, stagedCount: 0);
            foreach (var transaction in transactions)
            {
                try
                {
                    transaction.PreparePublication();
                }
                catch (Exception exception) when (IsRecoverablePatchException(exception))
                {
                    transaction.FailureStatus = LuaPatchModuleCommitStatus.CachePolicyFailed;
                    transaction.Message = exception.Message;
                    var restored = RollbackTransactions(transactions);
                    return PatchCommitSessionPreparation.FromFailure(BuildFailedCommit(
                        preparedPatch,
                        transactions,
                        restored ? LuaPatchCommitStatus.CachePolicyFailed :
                            LuaPatchCommitStatus.PublicationFailed,
                        exception.Message,
                        sideEffectsMayHaveOccurred: true,
                        started));
                }
            }

            if (TryStopBeforePublication(
                preparedPatch,
                updateWindow,
                options,
                started,
                transactions,
                out var beforePublish,
                cancellationToken))
            {
                return PatchCommitSessionPreparation.FromFailure(beforePublish!);
            }

            var session = new PatchCommitSession(
                this,
                preparedPatch,
                updateWindow,
                options,
                started,
                transactions);
            transferred = true;
            return PatchCommitSessionPreparation.FromSession(session);
        }
        catch (Exception exception) when (IsRecoverablePatchException(exception))
        {
            var hadExecution = transactions.Any(static transaction =>
                transaction.Execution is not null);
            var restored = RollbackTransactions(transactions);
            return PatchCommitSessionPreparation.FromFailure(BuildFailedCommit(
                preparedPatch,
                transactions,
                LuaPatchCommitStatus.PublicationFailed,
                restored
                    ? exception.Message
                    : exception.Message + " Atomic rollback was incomplete.",
                sideEffectsMayHaveOccurred: hadExecution,
                started));
        }
        finally
        {
            if (!transferred)
            {
                foreach (var transaction in transactions)
                {
                    transaction.Dispose();
                }
            }
        }
    }

    internal void ClosePatchUpdateWindow(LuaPatchUpdateWindow window)
    {
        if (Environment.CurrentManagedThreadId != window.OwnerThreadId ||
            !Monitor.IsEntered(_executionGate) ||
            !ReferenceEquals(_activePatchUpdateWindow, window))
        {
            throw new InvalidOperationException("The patch update window is not active on this thread.");
        }

        _activePatchUpdateWindow = null;
        Monitor.Exit(_executionGate);
    }

    private LuaPatchPrepareResult BindPreparedPatch(
        LuaPatchPreflightResult preflight,
        LuaPatchMigrationSchema? migrationSchema,
        LuaPatchPrepareOptions options,
        CancellationToken cancellationToken)
    {
        if (!preflight.Succeeded)
        {
            var failed = preflight.Modules.Select(module => new LuaPatchModulePrepareResult(
                module.ModuleName,
                module.Succeeded ? LuaPatchPrepareStatus.Ready :
                    LuaPatchPrepareStatus.PreflightFailed,
                null,
                module,
                module.Message)).ToImmutableArray();
            return new LuaPatchPrepareResult(
                LuaPatchPrepareStatus.PreflightFailed,
                null,
                preflight,
                failed,
                "At least one patch module failed isolated preflight.");
        }

        lock (_executionGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (preflight.Manifest.LanguageVersion != State.LanguageVersion)
            {
                var mismatches = preflight.Modules.Select(module =>
                    new LuaPatchModulePrepareResult(
                        module.ModuleName,
                        LuaPatchPrepareStatus.LanguageVersionMismatch,
                        null,
                        module,
                        "The patch language version does not match the live state."))
                    .ToImmutableArray();
                return new LuaPatchPrepareResult(
                    LuaPatchPrepareStatus.LanguageVersionMismatch,
                    null,
                    preflight,
                    mismatches,
                    "The patch language version does not match the live state.");
            }

            var knownModules = preflight.Modules.Select(static module => module.ModuleName)
                .ToHashSet(StringComparer.Ordinal);
            if (options.ModuleOptions is not null)
            {
                foreach (var configuredName in options.ModuleOptions.Keys)
                {
                    if (!knownModules.Contains(configuredName))
                    {
                        throw new ArgumentException(
                            $"Cache policy was configured for unknown patch module '{configuredName}'.",
                            nameof(options));
                    }
                }
            }

            var results = ImmutableArray.CreateBuilder<LuaPatchModulePrepareResult>(
                preflight.Modules.Length);
            var prepared = ImmutableArray.CreateBuilder<LuaPreparedPatchModule>(
                preflight.Modules.Length);
            var overall = LuaPatchPrepareStatus.Ready;
            string? expectedStateSchemaVersion = null;
            if (migrationSchema is not null &&
                (!_patchStateSchemaVersions.TryGetValue(
                    migrationSchema.SchemaId,
                    out expectedStateSchemaVersion) ||
                    !string.Equals(
                        expectedStateSchemaVersion,
                        migrationSchema.BaseVersion,
                        StringComparison.Ordinal)))
            {
                var mismatches = preflight.Modules.Select(module =>
                    new LuaPatchModulePrepareResult(
                        module.ModuleName,
                        LuaPatchPrepareStatus.StateSchemaVersionMismatch,
                        null,
                        module,
                        "The live state schema version does not match the migration base."))
                    .ToImmutableArray();
                return new LuaPatchPrepareResult(
                    LuaPatchPrepareStatus.StateSchemaVersionMismatch,
                    null,
                    preflight,
                    mismatches,
                    "The live state schema version does not match the migration base.");
            }

            var migrationsByModule = migrationSchema?.Modules.ToDictionary(
                static module => module.ModuleName,
                StringComparer.Ordinal) ??
                new Dictionary<string, LuaPatchModuleMigrationSchema>(StringComparer.Ordinal);
            foreach (var module in preflight.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var moduleMigration = migrationsByModule.GetValueOrDefault(module.ModuleName);
                var missingAdapter = moduleMigration?.State
                    .Where(static rule => rule.Kind == LuaPatchStateRuleKind.HostAdapter)
                    .Select(static rule => rule.AdapterId!)
                    .FirstOrDefault(adapterId => options.StateMigrationAdapters is null ||
                        !options.StateMigrationAdapters.ContainsKey(adapterId));
                missingAdapter ??= moduleMigration?.Resources
                    .Where(static rule => !string.IsNullOrWhiteSpace(rule.AdapterId))
                    .Select(static rule => rule.AdapterId!)
                    .FirstOrDefault(adapterId => options.ResourceMigrationAdapters is null ||
                        !options.ResourceMigrationAdapters.ContainsKey(adapterId));
                var unsupportedCoroutine = moduleMigration?.Resources.FirstOrDefault(static rule =>
                    rule.Kind == LuaPatchResourceKind.Coroutine &&
                    string.IsNullOrWhiteSpace(rule.AdapterId) &&
                    rule.Disposition is not (LuaPatchResourceDisposition.Continue or
                        LuaPatchResourceDisposition.RejectIfActive));
                if (missingAdapter is not null || unsupportedCoroutine is not null)
                {
                    overall = LuaPatchPrepareStatus.MigrationAdapterMissing;
                    results.Add(new LuaPatchModulePrepareResult(
                        module.ModuleName,
                        LuaPatchPrepareStatus.MigrationAdapterMissing,
                        null,
                        module,
                        missingAdapter is not null
                            ? $"Migration adapter '{missingAdapter}' is not registered."
                            : $"Coroutine resource '{unsupportedCoroutine!.ResourceId}' requires " +
                                "a reversible host adapter for its disposition."));
                    continue;
                }

                var reloadOptions = options.ModuleOptions is not null &&
                    options.ModuleOptions.TryGetValue(module.ModuleName, out var configured)
                    ? configured ?? throw new ArgumentException(
                        $"Cache policy for '{module.ModuleName}' is null.",
                        nameof(options))
                    : LuaModuleReloadOptions.Default;
                ValidateReloadOptions(reloadOptions);
                if (reloadOptions.SourcePath is not null ||
                    reloadOptions.CachePolicy == LuaModuleReloadCachePolicy.Custom)
                {
                    overall = LuaPatchPrepareStatus.UnsupportedCachePolicy;
                    results.Add(new LuaPatchModulePrepareResult(
                        module.ModuleName,
                        LuaPatchPrepareStatus.UnsupportedCachePolicy,
                        null,
                        module,
                        reloadOptions.SourcePath is not null
                            ? "Bundle modules cannot use a source-path override."
                            : "Opaque custom cache callbacks are not rollback-safe for atomic patches."));
                    continue;
                }

                if (!State.TryGetModule(module.ModuleName, out var record) || record is null)
                {
                    if (overall == LuaPatchPrepareStatus.Ready)
                    {
                        overall = LuaPatchPrepareStatus.ModuleNotLoaded;
                    }

                    results.Add(new LuaPatchModulePrepareResult(
                        module.ModuleName,
                        LuaPatchPrepareStatus.ModuleNotLoaded,
                        null,
                        module,
                        "The patch target is not currently loaded."));
                    continue;
                }

                results.Add(new LuaPatchModulePrepareResult(
                    module.ModuleName,
                    LuaPatchPrepareStatus.Ready,
                    record.Revision,
                    module,
                    null));
                prepared.Add(new LuaPreparedPatchModule(
                    module.ModuleName,
                    module.Kind,
                    record.Revision,
                    module.Module!,
                    module.Compilation,
                    reloadOptions,
                    moduleMigration));
            }

            var moduleResults = results.ToImmutable();
            if (overall != LuaPatchPrepareStatus.Ready)
            {
                return new LuaPatchPrepareResult(
                    overall,
                    null,
                    preflight,
                    moduleResults,
                    "The patch could not be bound to every live module.");
            }

            var patch = new LuaPreparedPatch(
                this,
                preflight.Manifest,
                preflight.DependencyPlan,
                prepared.ToImmutable(),
                migrationSchema,
                expectedStateSchemaVersion,
                options.StateMigrationAdapters ??
                    new Dictionary<string, ILuaPatchStateMigrationAdapter>(StringComparer.Ordinal),
                options.ResourceMigrationAdapters ??
                    new Dictionary<string, ILuaPatchResourceMigrationAdapter>(StringComparer.Ordinal));
            return new LuaPatchPrepareResult(
                LuaPatchPrepareStatus.Ready,
                patch,
                preflight,
                moduleResults,
                null);
        }
    }

    private static LuaPatchPrepareOptions SnapshotPrepareOptions(LuaPatchPrepareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options with
        {
            ModuleOptions = options.ModuleOptions?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.Ordinal),
            StateMigrationAdapters = SnapshotAdapters(
                options.StateMigrationAdapters,
                static adapter => adapter.AdapterId,
                nameof(options.StateMigrationAdapters)),
            ResourceMigrationAdapters = SnapshotAdapters(
                options.ResourceMigrationAdapters,
                static adapter => adapter.AdapterId,
                nameof(options.ResourceMigrationAdapters)),
        };
    }

    private static Dictionary<string, TAdapter>? SnapshotAdapters<TAdapter>(
        IReadOnlyDictionary<string, TAdapter>? adapters,
        Func<TAdapter, string> getId,
        string parameterName)
        where TAdapter : class
    {
        if (adapters is null)
        {
            return null;
        }

        var snapshot = new Dictionary<string, TAdapter>(StringComparer.Ordinal);
        foreach (var pair in adapters)
        {
            ArgumentNullException.ThrowIfNull(pair.Value);
            var adapterId = getId(pair.Value);
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                !string.Equals(pair.Key, adapterId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "A migration adapter key must match its non-empty adapter id.",
                    parameterName);
            }

            snapshot.Add(pair.Key, pair.Value);
        }

        return snapshot;
    }

    private void ValidateActiveWindow(LuaPatchUpdateWindow window)
    {
        if (Environment.CurrentManagedThreadId != window.OwnerThreadId ||
            !Monitor.IsEntered(_executionGate) ||
            !ReferenceEquals(window.Owner, this) ||
            !ReferenceEquals(_activePatchUpdateWindow, window))
        {
            throw new InvalidOperationException(
                "Patch commit requires an active update window opened by this host on this thread.");
        }
    }

    private static void ValidateDuration(
        TimeSpan value,
        bool allowInfinite,
        string parameterName)
    {
        if (value < TimeSpan.Zero && (!allowInfinite || value != Timeout.InfiniteTimeSpan))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The duration is invalid.");
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, long started)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }

        var remaining = timeout - Stopwatch.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static bool BudgetExceeded(
        LuaPatchUpdateWindow window,
        TimeSpan commitBudget,
        long commitStarted) =>
        window.MaximumDuration != Timeout.InfiniteTimeSpan &&
        window.Elapsed >= window.MaximumDuration ||
        commitBudget != Timeout.InfiniteTimeSpan &&
        Stopwatch.GetElapsedTime(commitStarted) >= commitBudget;

    private static bool IsRecoverablePatchException(Exception exception) =>
        exception is not OperationCanceledException and
        not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;

    private void NormalizeTargetModules(
        List<PatchModuleTransaction> transactions,
        int stagedCount)
    {
        for (var index = 0; index < transactions.Count; index++)
        {
            var transaction = transactions[index];
            State.SetLoadedModuleCacheValue(
                transaction.Module.ModuleName,
                index < stagedCount && transaction.HasCandidate
                    ? transaction.CandidateValue
                    : transaction.PreviousCache);
            if (index >= stagedCount || !transaction.HasCandidate)
            {
                State.RestoreLoadedModule(transaction.RootedPreviousRecord);
            }
        }
    }

    private static bool TryStopBeforePublication(
        LuaPreparedPatch preparedPatch,
        LuaPatchUpdateWindow window,
        LuaPatchCommitOptions options,
        long started,
        IReadOnlyList<PatchModuleTransaction> transactions,
        out LuaPatchCommitResult? result,
        CancellationToken cancellationToken)
    {
        var cancelled = cancellationToken.IsCancellationRequested;
        var deferred = BudgetExceeded(window, options.MaximumPauseDuration, started);
        if (!cancelled && !deferred)
        {
            result = null;
            return false;
        }

        var hadExecution = transactions.Any(static transaction => transaction.Execution is not null);
        var restored = RollbackTransactions(transactions);
        result = BuildFailedCommit(
            preparedPatch,
            transactions,
            restored
                ? cancelled ? LuaPatchCommitStatus.Cancelled : LuaPatchCommitStatus.Deferred
                : LuaPatchCommitStatus.PublicationFailed,
            cancelled
                ? "Patch commit was cancelled; target-module state was restored."
                : "The pause budget elapsed; target-module state was restored.",
            sideEffectsMayHaveOccurred: hadExecution,
            started);
        return true;
    }

    private static bool RollbackTransactions(IReadOnlyList<PatchModuleTransaction> transactions)
    {
        var restored = true;
        for (var index = transactions.Count - 1; index >= 0; index--)
        {
            restored &= transactions[index].Rollback();
        }

        return restored;
    }

    private static LuaPatchCommitResult BuildFailedCommit(
        LuaPreparedPatch patch,
        IReadOnlyList<PatchModuleTransaction> transactions,
        LuaPatchCommitStatus status,
        string? message,
        bool sideEffectsMayHaveOccurred,
        long started)
    {
        var modules = transactions.Select(transaction =>
        {
            var moduleStatus = transaction.FailureStatus ??
                (transaction.Execution is null
                    ? LuaPatchModuleCommitStatus.NotExecuted
                    : LuaPatchModuleCommitStatus.RolledBack);
            return transaction.ToResult(moduleStatus);
        }).ToImmutableArray();
        return new LuaPatchCommitResult(
            patch.Manifest.PatchId,
            status,
            modules,
            message,
            sideEffectsMayHaveOccurred,
            Stopwatch.GetElapsedTime(started));
    }

    private static LuaPatchCommitResult EmptyCommitResult(
        LuaPreparedPatch patch,
        LuaPatchCommitStatus status,
        string message,
        long started) => new(
        patch.Manifest.PatchId,
        status,
        patch.Modules.Select(static module => new LuaPatchModuleCommitResult(
            module.ModuleName,
            LuaPatchModuleCommitStatus.NotExecuted,
            module.ExpectedRevision,
            null,
            null,
            null,
            null,
            null,
            0,
            0,
            0,
            0)).ToImmutableArray(),
        message,
        SideEffectsMayHaveOccurred: false,
        Stopwatch.GetElapsedTime(started));

    internal sealed record PatchCommitSessionPreparation(
        PatchCommitSession? Session,
        LuaPatchCommitResult? Failure)
    {
        public static PatchCommitSessionPreparation FromSession(PatchCommitSession session) =>
            new(session, null);

        public static PatchCommitSessionPreparation FromFailure(LuaPatchCommitResult failure) =>
            new(null, failure);
    }

    internal sealed class PatchCommitSession : IDisposable
    {
        private readonly LuaHost _host;
        private readonly LuaPreparedPatch _patch;
        private readonly LuaPatchUpdateWindow _window;
        private readonly LuaPatchCommitOptions _options;
        private readonly long _started;
        private readonly List<PatchModuleTransaction> _transactions;
        private bool _schemaPublished;
        private bool _disposed;

        internal PatchCommitSession(
            LuaHost host,
            LuaPreparedPatch patch,
            LuaPatchUpdateWindow window,
            LuaPatchCommitOptions options,
            long started,
            List<PatchModuleTransaction> transactions)
        {
            _host = host;
            _patch = patch;
            _window = window;
            _options = options;
            _started = started;
            _transactions = transactions;
        }

        public LuaHost Host => _host;

        public LuaPreparedPatch Patch => _patch;

        public LuaPatchCommitResult? Publish(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (cancellationToken.IsCancellationRequested ||
                BudgetExceeded(_window, _options.MaximumPauseDuration, _started))
            {
                return Rollback(
                    cancellationToken.IsCancellationRequested
                        ? LuaPatchCommitStatus.Cancelled
                        : LuaPatchCommitStatus.Deferred,
                    cancellationToken.IsCancellationRequested
                        ? "Patch commit was cancelled before publication."
                        : "The pause budget elapsed before publication.",
                    sideEffectsMayHaveOccurred: true);
            }

            try
            {
                foreach (var transaction in _transactions)
                {
                    transaction.Publish();
                    if (transaction.FunctionMigrations.Any(static migration =>
                        migration.Status == LuaFunctionMigrationStatus.ConcurrentUpdate))
                    {
                        throw new InvalidOperationException(
                            $"Module '{transaction.Module.ModuleName}' shares a closure slot " +
                            "that changed during atomic publication.");
                    }

                    if (cancellationToken.IsCancellationRequested ||
                        BudgetExceeded(_window, _options.MaximumPauseDuration, _started))
                    {
                        return Rollback(
                            cancellationToken.IsCancellationRequested
                                ? LuaPatchCommitStatus.Cancelled
                                : LuaPatchCommitStatus.Deferred,
                            cancellationToken.IsCancellationRequested
                                ? "Patch commit was cancelled and all target modules were rolled back."
                                : "The pause budget elapsed and all target modules were rolled back.",
                            sideEffectsMayHaveOccurred: true);
                    }
                }

                if (_patch.MigrationSchema is { } migrationSchema)
                {
                    _host._patchStateSchemaVersions[migrationSchema.SchemaId] =
                        migrationSchema.TargetVersion;
                    _schemaPublished = true;
                }

                return null;
            }
            catch (Exception exception) when (IsRecoverablePatchException(exception))
            {
                return Rollback(
                    LuaPatchCommitStatus.PublicationFailed,
                    exception.Message,
                    sideEffectsMayHaveOccurred: true);
            }
        }

        public void FinalizePublication()
        {
            ThrowIfDisposed();
            foreach (var transaction in _transactions)
            {
                if (transaction.PreviousRecord.Module is not null)
                {
                    _host._jitExecutor?.Invalidate(transaction.PreviousRecord.Module);
                }
            }
        }

        public LuaPatchCommitResult BuildCommittedResult()
        {
            ThrowIfDisposed();
            var committed = _transactions.Select(static transaction =>
                transaction.ToResult(LuaPatchModuleCommitStatus.Committed)).ToImmutableArray();
            return new LuaPatchCommitResult(
                _patch.Manifest.PatchId,
                LuaPatchCommitStatus.Committed,
                committed,
                null,
                SideEffectsMayHaveOccurred: false,
                Stopwatch.GetElapsedTime(_started));
        }

        public LuaPatchCommitResult Rollback(
            LuaPatchCommitStatus status,
            string? message,
            bool sideEffectsMayHaveOccurred)
        {
            ThrowIfDisposed();
            var restored = RollbackTransactions(_transactions);
            if (_schemaPublished && _patch.MigrationSchema is { } migrationSchema)
            {
                _host._patchStateSchemaVersions[migrationSchema.SchemaId] =
                    _patch.ExpectedStateSchemaVersion!;
                _schemaPublished = false;
            }

            return BuildFailedCommit(
                _patch,
                _transactions,
                restored ? status : LuaPatchCommitStatus.PublicationFailed,
                restored ? message : message + " Atomic rollback was incomplete.",
                sideEffectsMayHaveOccurred,
                _started);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var transaction in _transactions)
            {
                transaction.Dispose();
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed class PatchModuleTransaction : IDisposable
    {
        private readonly LuaHost _host;
        private readonly LuaHandle _previousLoader;
        private readonly LuaHandle _previousLoaderData;
        private readonly LuaHandle _previousCache;
        private readonly LuaHandle _candidateLoader;
        private readonly List<LuaHandle> _upvalueRoots;
        private readonly List<StatePathMutation> _statePathMutations = [];
        private readonly List<ILuaPatchStateMigrationOperation> _stateMigrationOperations = [];
        private readonly List<ILuaPatchResourceMigrationOperation> _resourceMigrationOperations = [];
        private LuaHandle? _candidateValue;
        private LuaHandle? _committedValue;
        private LuaTablePatch? _tablePatch;
        private LuaFunctionMigrationPlan? _migrationPlan;
        private LuaFunctionMigrationPlan.LuaFunctionMigrationPublication? _migrationPublication;
        private UpvalueReuseResult _upvalues;
        private bool _published;
        private bool _rolledBack;
        private bool _disposed;

        public PatchModuleTransaction(
            LuaHost host,
            LuaPreparedPatchModule module,
            LuaModuleRecord previousRecord)
        {
            _host = host;
            Module = module;
            PreviousRecord = previousRecord;
            _previousLoader = host.State.CreateHandle(previousRecord.Loader);
            try
            {
                _previousLoaderData = host.State.CreateHandle(previousRecord.LoaderData);
                try
                {
                    _previousCache = host.State.CreateHandle(previousRecord.CachedValue);
                    try
                    {
                        var closure = host.State.CreateMainClosure(module.Module);
                        _candidateLoader = host.State.CreateHandle(LuaValue.FromFunction(closure));
                    }
                    catch
                    {
                        _previousCache.Dispose();
                        throw;
                    }
                }
                catch
                {
                    _previousLoaderData.Dispose();
                    throw;
                }
            }
            catch
            {
                _previousLoader.Dispose();
                throw;
            }

            _upvalues = new UpvalueReuseResult([], 0);
            _upvalueRoots = [];
            try
            {
                _upvalues = ReuseCompatibleLoaderUpvalues(PreviousLoader, CandidateLoader);
                foreach (var snapshot in _upvalues.Snapshots)
                {
                    _upvalueRoots.Add(host.State.CreateHandle(snapshot.Value));
                }
            }
            catch
            {
                foreach (var root in _upvalueRoots)
                {
                    root.Dispose();
                }

                _candidateLoader.Dispose();
                _previousCache.Dispose();
                _previousLoaderData.Dispose();
                _previousLoader.Dispose();
                throw;
            }
        }

        public LuaPreparedPatchModule Module { get; }

        public LuaModuleRecord PreviousRecord { get; }

        public LuaModuleRecord? CurrentRecord { get; private set; }

        public LuaExecutionResult? Execution { get; private set; }

        public LuaPatchModuleCommitStatus? FailureStatus { get; set; }

        public string? Message { get; set; }

        public ImmutableArray<LuaFunctionMigrationResult> FunctionMigrations { get; private set; }

        public int PatchedExportCount => _tablePatch?.PatchedExportCount ?? 0;

        public int RemovedExportCount => _tablePatch?.RemovedExportCount ?? 0;

        public bool HasCandidate => _candidateValue is not null;

        public LuaValue PreviousLoader => _previousLoader.Value;

        public LuaValue PreviousLoaderData => _previousLoaderData.Value;

        public LuaValue PreviousCache => _previousCache.Value;

        public LuaValue CandidateLoader => _candidateLoader.Value;

        public LuaValue CandidateValue => _candidateValue?.Value ??
            throw new InvalidOperationException("The patch candidate has not executed.");

        public LuaValue CommittedValue => _committedValue?.Value ??
            throw new InvalidOperationException("The patch publication is not prepared.");

        public LuaModuleRecord RootedPreviousRecord => PreviousRecord with
        {
            Loader = PreviousLoader,
            LoaderData = PreviousLoaderData,
            CachedValue = PreviousCache,
        };

        public void ExecuteCandidate()
        {
            Execution = _host.ExecuteLoader(Module.ModuleName, CandidateLoader, PreviousLoaderData);
            var cacheAfterExecution = _host.State.GetLoadedModuleCacheValue(Module.ModuleName);
            var candidate = Execution.Values.IsEmpty ? LuaValue.Nil : Execution.Values[0];
            if (candidate.IsNil)
            {
                candidate = cacheAfterExecution != PreviousCache && !cacheAfterExecution.IsNil
                    ? cacheAfterExecution
                    : LuaValue.FromBoolean(true);
            }

            _candidateValue = _host.State.CreateHandle(candidate);
        }

        public void PreparePublication()
        {
            LuaValue committed;
            switch (Module.ReloadOptions.CachePolicy)
            {
                case LuaModuleReloadCachePolicy.ReplaceCache:
                    committed = CandidateValue;
                    break;
                case LuaModuleReloadCachePolicy.PatchExistingTable:
                    if (PreviousCache.Kind != LuaValueKind.Table ||
                        CandidateValue.Kind != LuaValueKind.Table)
                    {
                        throw new InvalidOperationException(
                            "PatchExistingTable requires both cache values to be tables.");
                    }

                    committed = PreviousCache;
                    break;
                default:
                    throw new InvalidOperationException(
                        "The prepared cache policy is not transaction-safe.");
            }

            _committedValue = _host.State.CreateHandle(committed);
            _migrationPlan = LuaFunctionMigrationPlan.Create(
                PreviousCache,
                PreviousRecord.Module,
                CandidateValue,
                Module.Module);
        }

        public void ApplyMigrations(
            IReadOnlyDictionary<string, ILuaPatchStateMigrationAdapter> stateAdapters,
            IReadOnlyDictionary<string, ILuaPatchResourceMigrationAdapter> resourceAdapters)
        {
            if (Module.MigrationSchema is null)
            {
                return;
            }

            foreach (var rule in Module.MigrationSchema.State)
            {
                var sourcePath = LuaPatchStatePath.Parse(rule.SourcePath ?? rule.TargetPath);
                var targetPath = LuaPatchStatePath.Parse(rule.TargetPath);
                var sourceFound = TryResolvePath(PreviousCache, sourcePath, out var previousValue);
                var candidateFound = TryResolvePath(CandidateValue, targetPath, out var candidateValue);
                if (rule.Required && (!sourceFound && rule.Kind != LuaPatchStateRuleKind.Drop ||
                    !candidateFound && targetPath.Segments.Length != 0))
                {
                    throw new LuaPatchMigrationSchemaException(
                        LuaPatchMigrationSchemaErrorCode.StatePathNotFound,
                        $"Required state path for module '{Module.ModuleName}' was not found: " +
                        $"'{rule.TargetPath}'.");
                }

                switch (rule.Kind)
                {
                    case LuaPatchStateRuleKind.Preserve:
                        if (sourceFound)
                        {
                            SetCandidatePath(targetPath, previousValue);
                        }

                        break;
                    case LuaPatchStateRuleKind.Drop:
                        if (candidateFound || targetPath.Segments.Length == 0)
                        {
                            SetCandidatePath(targetPath, LuaValue.Nil);
                        }

                        break;
                    case LuaPatchStateRuleKind.HostAdapter:
                        if (!stateAdapters.TryGetValue(rule.AdapterId!, out var adapter))
                        {
                            throw new LuaPatchMigrationSchemaException(
                                LuaPatchMigrationSchemaErrorCode.AdapterNotRegistered,
                                $"State migration adapter '{rule.AdapterId}' is not registered.");
                        }

                        var operation = adapter.Prepare(new LuaPatchStateMigrationContext(
                            Module.ModuleName,
                            rule,
                            sourceFound ? previousValue : LuaValue.Nil,
                            candidateFound ? candidateValue : LuaValue.Nil)) ??
                            throw new LuaPatchMigrationSchemaException(
                                LuaPatchMigrationSchemaErrorCode.AdapterFailed,
                                $"State migration adapter '{rule.AdapterId}' returned no operation.");
                        _stateMigrationOperations.Add(operation);
                        operation.Apply();
                        SetCandidatePath(targetPath, operation.ResultValue);
                        break;
                    default:
                        throw new InvalidOperationException("The state migration rule is invalid.");
                }
            }

            foreach (var rule in Module.MigrationSchema.Resources)
            {
                if (!string.IsNullOrWhiteSpace(rule.AdapterId))
                {
                    if (!resourceAdapters.TryGetValue(rule.AdapterId, out var adapter))
                    {
                        throw new LuaPatchMigrationSchemaException(
                            LuaPatchMigrationSchemaErrorCode.AdapterNotRegistered,
                            $"Resource migration adapter '{rule.AdapterId}' is not registered.");
                    }

                    var operation = adapter.Prepare(new LuaPatchResourceMigrationContext(
                        Module.ModuleName,
                        rule,
                        PreviousCache,
                        CandidateValue)) ??
                        throw new LuaPatchMigrationSchemaException(
                            LuaPatchMigrationSchemaErrorCode.AdapterFailed,
                            $"Resource migration adapter '{rule.AdapterId}' returned no operation.");
                    if (rule.Disposition == LuaPatchResourceDisposition.RejectIfActive &&
                        operation.IsActive)
                    {
                        operation.Dispose();
                        throw new LuaPatchMigrationSchemaException(
                            LuaPatchMigrationSchemaErrorCode.ResourceActive,
                            $"Resource '{rule.ResourceId}' is active.");
                    }

                    _resourceMigrationOperations.Add(operation);
                    operation.Apply(rule.Disposition);
                    continue;
                }

                if (rule.Kind != LuaPatchResourceKind.Coroutine)
                {
                    if (rule.Disposition != LuaPatchResourceDisposition.Continue)
                    {
                        throw new LuaPatchMigrationSchemaException(
                            LuaPatchMigrationSchemaErrorCode.AdapterRequired,
                            $"Resource '{rule.ResourceId}' requires a migration adapter.");
                    }

                    continue;
                }

                if (rule.Disposition is not (LuaPatchResourceDisposition.Continue or
                    LuaPatchResourceDisposition.RejectIfActive))
                {
                    throw new LuaPatchMigrationSchemaException(
                        LuaPatchMigrationSchemaErrorCode.AdapterRequired,
                        $"Resource '{rule.ResourceId}' requires a migration adapter.");
                }

                var path = LuaPatchStatePath.Parse(rule.StatePath!);
                if (!TryResolvePath(PreviousCache, path, out var value) ||
                    value.Kind != LuaValueKind.Thread)
                {
                    throw new LuaPatchMigrationSchemaException(
                        LuaPatchMigrationSchemaErrorCode.StateKindMismatch,
                        $"Coroutine resource '{rule.ResourceId}' does not resolve to a thread.");
                }

                if (rule.Disposition == LuaPatchResourceDisposition.RejectIfActive &&
                    value.AsThread().Status is not (LuaThreadStatus.Dead or LuaThreadStatus.Error))
                {
                    throw new LuaPatchMigrationSchemaException(
                        LuaPatchMigrationSchemaErrorCode.ResourceActive,
                        $"Coroutine resource '{rule.ResourceId}' is still active.");
                }
            }

            if (CandidateValue.IsNil)
            {
                throw new LuaPatchMigrationSchemaException(
                    LuaPatchMigrationSchemaErrorCode.StateKindMismatch,
                    $"State migration for module '{Module.ModuleName}' produced a nil cache root.");
            }
        }

        public void Publish()
        {
            if (Module.ReloadOptions.CachePolicy == LuaModuleReloadCachePolicy.PatchExistingTable)
            {
                _tablePatch = LuaTablePatch.Apply(
                    PreviousCache.AsTable(),
                    CandidateValue.AsTable());
            }

            _host.State.SetLoadedModuleCacheValue(Module.ModuleName, CommittedValue);
            CurrentRecord = _host.State.RegisterLoadedModule(
                Module.ModuleName,
                PreviousRecord.LoaderKind,
                CandidateLoader,
                PreviousLoaderData,
                CommittedValue,
                Module.Module);
            _published = true;
            _migrationPublication = _migrationPlan!.Publish();
            FunctionMigrations = _migrationPublication.Results;
        }

        public bool Rollback()
        {
            if (_rolledBack)
            {
                return true;
            }

            var restored = true;
            void Attempt(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception exception) when (IsRecoverablePatchException(exception))
                {
                    restored = false;
                }
            }

            Attempt(() => restored &= _migrationPublication?.Rollback() ?? true);
            Attempt(() => _host.State.SetLoadedModuleCacheValue(
                Module.ModuleName,
                PreviousCache));
            if (_tablePatch is not null)
            {
                Attempt(_tablePatch.Rollback);
            }

            Attempt(() => _host.State.RestoreLoadedModule(RootedPreviousRecord));
            for (var index = 0; index < _upvalues.Snapshots.Count; index++)
            {
                var snapshotIndex = index;
                Attempt(() => _upvalues.Snapshots[snapshotIndex].Cell.Value =
                    _upvalueRoots[snapshotIndex].Value);
            }

            for (var index = _resourceMigrationOperations.Count - 1; index >= 0; index--)
            {
                var operation = _resourceMigrationOperations[index];
                Attempt(operation.Rollback);
            }

            for (var index = _statePathMutations.Count - 1; index >= 0; index--)
            {
                var mutation = _statePathMutations[index];
                Attempt(mutation.Rollback);
            }

            for (var index = _stateMigrationOperations.Count - 1; index >= 0; index--)
            {
                var operation = _stateMigrationOperations[index];
                Attempt(operation.Rollback);
            }

            _rolledBack = true;
            if (_published && FailureStatus is null)
            {
                FailureStatus = LuaPatchModuleCommitStatus.RolledBack;
            }

            return restored;
        }

        public LuaPatchModuleCommitResult ToResult(LuaPatchModuleCommitStatus status) => new(
            Module.ModuleName,
            status,
            Module.ExpectedRevision,
            status == LuaPatchModuleCommitStatus.Committed
                ? CurrentRecord?.Revision
                : PreviousRecord.Revision,
            PreviousRecord,
            status == LuaPatchModuleCommitStatus.Committed ? CurrentRecord : null,
            Execution,
            Message,
            _upvalues.Snapshots.Count,
            _upvalues.MismatchCount,
            PatchedExportCount,
            RemovedExportCount,
            FunctionMigrations);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            static void Attempt(Action action)
            {
                try
                {
                    action();
                }
                catch (Exception exception) when (IsRecoverablePatchException(exception))
                {
                    // Disposal releases journal roots only. Transaction state was already committed
                    // or rolled back, so continue releasing the remaining roots.
                }
            }

            foreach (var operation in _resourceMigrationOperations)
            {
                Attempt(operation.Dispose);
            }

            foreach (var operation in _stateMigrationOperations)
            {
                Attempt(operation.Dispose);
            }

            foreach (var mutation in _statePathMutations)
            {
                Attempt(mutation.Dispose);
            }

            if (_committedValue is not null)
            {
                Attempt(_committedValue.Dispose);
            }

            if (_candidateValue is not null)
            {
                Attempt(_candidateValue.Dispose);
            }

            foreach (var root in _upvalueRoots)
            {
                Attempt(root.Dispose);
            }

            Attempt(_candidateLoader.Dispose);
            Attempt(_previousCache.Dispose);
            Attempt(_previousLoaderData.Dispose);
            Attempt(_previousLoader.Dispose);
        }

        private bool TryResolvePath(
            LuaValue root,
            LuaPatchStatePath path,
            out LuaValue value)
        {
            value = root;
            foreach (var segment in path.Segments)
            {
                if (value.Kind != LuaValueKind.Table)
                {
                    value = LuaValue.Nil;
                    return false;
                }

                value = value.AsTable().Get(PathKey(segment));
                if (value.IsNil)
                {
                    return false;
                }
            }

            return !value.IsNil;
        }

        private void SetCandidatePath(LuaPatchStatePath path, LuaValue value)
        {
            if (path.Segments.Length == 0)
            {
                var previous = _host.State.CreateHandle(CandidateValue);
                _statePathMutations.Add(new StatePathMutation(_candidateValue!, previous));
                _candidateValue!.Value = value;
                return;
            }

            var parentPath = new LuaPatchStatePath(path.Segments.RemoveAt(
                path.Segments.Length - 1));
            if (!TryResolvePath(CandidateValue, parentPath, out var parent) ||
                parent.Kind != LuaValueKind.Table)
            {
                throw new LuaPatchMigrationSchemaException(
                    LuaPatchMigrationSchemaErrorCode.StatePathNotFound,
                    "A migration target parent is missing or is not a table.");
            }

            var table = parent.AsTable();
            var key = PathKey(path.Segments[^1]);
            var previousValue = _host.State.CreateHandle(table.Get(key));
            _statePathMutations.Add(new StatePathMutation(table, key, previousValue));
            table.Set(key, value);
        }

        private LuaValue PathKey(string segment) => LuaValue.FromString(
            _host.State.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(segment)));

        private sealed class StatePathMutation : IDisposable
        {
            private readonly LuaHandle? _root;
            private readonly LuaTable? _table;
            private readonly LuaValue _key;
            private readonly LuaHandle _previous;

            public StatePathMutation(LuaHandle root, LuaHandle previous)
            {
                _root = root;
                _previous = previous;
            }

            public StatePathMutation(LuaTable table, LuaValue key, LuaHandle previous)
            {
                _table = table;
                _key = key;
                _previous = previous;
            }

            public void Rollback()
            {
                if (_root is not null)
                {
                    _root.Value = _previous.Value;
                }
                else
                {
                    _table!.Set(_key, _previous.Value);
                }
            }

            public void Dispose() => _previous.Dispose();
        }
    }
}
