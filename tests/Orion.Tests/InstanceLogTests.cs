using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Runtime;
using Orion.Desktop.ViewModels;
using Orion.Domain;

namespace Orion.Tests;

public sealed class InstanceLogTests
{
    [Fact]
    public void ClearTruncatesJournalsAndLoggingCanContinueWithoutTouchingOtherFiles()
    {
        using var dir = new TestDirectory(); var log = dir.Paths.InstanceLog(Guid.NewGuid());
        ProcessLog.Append(log, "old output"); ProcessLog.Clear(log);
        Assert.True(File.Exists(log)); Assert.Equal("", File.ReadAllText(log));
        ProcessLog.Append(log, "new output"); Assert.Equal("new output", File.ReadAllText(log));
        var service = Path.Combine(dir.Paths.Logs, "xodus.log"); ProcessLog.Append(service, "service output");
        var dump = Path.Combine(dir.Paths.Logs, "crash.dmp"); File.WriteAllText(dump, "keep");
        var nested = Path.Combine(dir.Paths.Logs, "nested/keep.log"); ProcessLog.Append(nested, "keep");
        Assert.Equal(2, ProcessLog.ClearJournals(dir.Paths.Logs));
        Assert.Empty(File.ReadAllText(log)); Assert.Empty(File.ReadAllText(service));
        Assert.Equal("keep", File.ReadAllText(dump)); Assert.Equal("keep", File.ReadAllText(nested));
    }

    [Fact]
    public void ClearRejectsSymlinksAndDoesNotCreateMissingFiles()
    {
        using var dir = new TestDirectory(); var outside = Path.Combine(dir.Root, "keep.txt"); File.WriteAllText(outside, "keep");
        var linked = Path.Combine(dir.Paths.Logs, "linked.log"); File.CreateSymbolicLink(linked, outside);
        Assert.Throws<InvalidDataException>(() => ProcessLog.Clear(linked)); Assert.Equal("keep", File.ReadAllText(outside));
        var missing = dir.Paths.InstanceLog(Guid.NewGuid()); ProcessLog.Clear(missing); Assert.False(File.Exists(missing));
    }
    [Fact]
    public async Task ConcurrentProcessesShareACompleteJournal()
    {
        using var directory = new TestDirectory();
        var log = directory.Paths.InstanceLog(Guid.NewGuid());
        var runner = new ProcessRunner();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => runner.RunAsync(new("/bin/sh",
            ["-c", "printf '%s\\n' \"$1\"; printf '%s\\n' \"$2\" >&2", "test", $"output-{i}", $"error-{i}"], directory.Root), log, CancellationToken.None)));
        var content = await File.ReadAllTextAsync(log);
        for (var i = 0; i < 6; i++) { Assert.Contains($"output-{i}", content); Assert.Contains($"error-{i}", content); }
        Assert.DoesNotContain("Exited with code 0", content);
    }

    [Fact]
    public async Task ViewerBoundsHistoryAndRemovesTerminalEscapeSequences()
    {
        using var directory = new TestDirectory();
        var log = directory.Paths.InstanceLog(Guid.NewGuid());
        ProcessLog.Append(log, new string('x', LogTail.MaximumBytes * 2) + "\n\u001b[31mrecent\u001b[0m\n");
        var content = await LogTail.ReadAsync(log);
        Assert.True(content.Length <= LogTail.MaximumBytes + 2);
        Assert.Contains("recent", content);
        Assert.DoesNotContain('\u001b', content);
        Assert.Equal("", await LogTail.ReadAsync(log + ".missing"));
    }

    [Fact]
    public async Task StructuredCaptureDoesNotMixDiagnosticsWithJson()
    {
        using var directory = new TestDirectory();
        var json = await new ProcessRunner().CaptureAsync(new("/bin/sh",
            ["-c", "printf '[{\"active\":true}]'; printf 'diagnostic' >&2"], directory.Root), CancellationToken.None);
        Assert.Equal("[{\"active\":true}]", json);
        Assert.Empty(Directory.EnumerateFiles(directory.Paths.Logs));
    }

    [Fact]
    public async Task StructuredCaptureHonorsCancellation()
    {
        using var directory = new TestDirectory();
        using var cancellation = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProcessRunner().CaptureAsync(
            new("/bin/sh", ["-c", "sleep 60"], directory.Root), cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void QuietPrefixAndLogPreferenceDoNotAlterLaunchArguments()
    {
        using var directory = new TestDirectory();
        Assert.Equal("1", new XodusEnvironment(directory.Paths).Create()["WINEBOOT_HIDE_DIALOG"]);
        var editor = new LaunchOptionsViewModel(GameInstance.Create("Logs", "1", "Release")) { ShowLogOnLaunch = true };
        Assert.True(editor.Build().ShowLogOnLaunch);
        editor.ResetCommand.Execute(null);
        Assert.False(editor.Build().ShowLogOnLaunch);
    }

    [Fact]
    public void MeasuredProgressRewritesOneLineWithElapsedTime()
    {
        using var dir = new TestDirectory();
        var id = Guid.NewGuid(); var log = dir.Paths.InstanceLog(id);
        using (var journal = new InstanceJournal(dir.Paths, id, null, null))
        using (var nested = new InstanceJournal(dir.Paths, id, null, journal))
        {
            nested.Report(new("Verifying a Minecraft purchase"));
            for (var i = 0; i <= 4; i++) nested.Report(new($"Downloading package · {i}.0 / 4.0 MiB", i / 4d));
            nested.Report(new("Package downloaded"));
            nested.Report(new("Decrypting blocks · 1 / 2 · 50.0%", 0.5));
            nested.Report(new("Decrypting blocks · 2 / 2 · 100.0%", 1));
            ProcessLog.Event(log, "Xodus service", "interleaved");
            nested.Report(new("Decrypting blocks · 2 / 2 · 100.0%", 1));
            nested.Error(new InvalidDataException("broken"));
            journal.Error(new InvalidDataException("broken"));
        }
        var lines = File.ReadAllLines(log).Where(l => l.Length > 0).Select(l => l[11..]).ToArray();
        Assert.Equal(7, lines.Length);
        Assert.Equal("[Orion] Verifying a Minecraft purchase", lines[0]);
        Assert.Matches(@"^\[Orion\] Downloading package · 4\.0 / 4\.0 MiB · 0:0\d$", lines[1]);
        Assert.Equal("[Orion] Package downloaded", lines[2]);
        Assert.StartsWith("[Orion] Decrypting blocks · 2 / 2 · 100.0% · 0:0", lines[3]);
        Assert.Equal("[Xodus service] interleaved", lines[4]);
        Assert.StartsWith("[Orion] Decrypting blocks · 2 / 2", lines[5]);
        Assert.Equal("[Orion] broken", lines[6]);
        Assert.Equal("1:02:03", InstanceJournal.FormatElapsed(new TimeSpan(1, 2, 3)));
        Assert.Equal("4:05", InstanceJournal.FormatElapsed(new TimeSpan(0, 4, 5)));
    }
}
