using Lunil.LanguageServer;
using Lunil.Workspace;

namespace Lunil.LanguageServer.Tests;

/// <summary>
/// Coverage for corpus-scan progress reporting: every indexing phase must surface
/// real completed/total file counts so the editor status bar can show n/total.
/// </summary>
public sealed class WorkspaceProgressTests
{
    [Fact]
    public async Task CorpusScansReportRealFileCounts()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-progress-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Cross the 64-file reporting interval more than twice.
            const int files = 140;
            for (var index = 0; index < files; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(root, $"mod{index}.lua"),
                    $"local value{index} = {index}\nreturn {{ value = value{index} }}\n");
            }

            var events = new System.Collections.Concurrent.ConcurrentQueue<LuaWorkspaceProgress>();
            using var workspace = new LanguageServerWorkspace();
            workspace.ProgressReported += progress =>
            {
                events.Enqueue(progress);
                return Task.CompletedTask;
            };

            workspace.Initialize([new Uri(root + Path.DirectorySeparatorChar)]);
            await WaitForAsync(() => workspace.GetDocuments().Length == files);
            await WaitForAsync(() => workspace.GetSnapshot() is not null);

            var loading = events.Where(static progress =>
                progress.Phase == LuaWorkspaceProgressPhase.Loading).ToList();
            Assert.Contains(loading, static progress => progress.TotalWorkItems == files);
            Assert.Contains(loading, static progress => progress.CompletedWorkItems == files);
            // Intermediate reports stay within the real bounds.
            Assert.All(loading, static progress =>
            {
                Assert.InRange(progress.CompletedWorkItems, 0, files);
                Assert.Equal(files, progress.TotalWorkItems);
            });

            var declarations = events.Where(static progress =>
                progress.Phase == LuaWorkspaceProgressPhase.Declarations).ToList();
            Assert.Contains(declarations, static progress =>
                progress.TotalWorkItems == files && progress.CompletedWorkItems == files);
            Assert.All(declarations, static progress =>
            {
                Assert.InRange(progress.CompletedWorkItems, 0, files);
                Assert.Equal(files, progress.TotalWorkItems);
            });

            // The analysis pipeline runs to completion and reports it.
            Assert.Contains(events, static progress =>
                progress.Phase == LuaWorkspaceProgressPhase.Completed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }
}
