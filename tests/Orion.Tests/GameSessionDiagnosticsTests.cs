using Orion.Domain;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Processes;

namespace Orion.Tests;

public sealed class GameSessionDiagnosticsTests
{
    private const string Edition = "prefix/drive_c/users/player/AppData/Roaming/Minecraft Bedrock";
    private static string Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text); return path;
    }

    [Fact]
    public async Task IncludesOnlyCurrentSessionContentAndReportReferences()
    {
        using var dir = new TestDirectory(); var log = Path.Combine(dir.Root, "journal.log");
        var content = Write(dir.Root, Edition + "/logs/ContentLog.txt", "old session\n");
        var report = Write(dir.Root, Edition + "/bootstrapStorage/crash/old.crashedsession", "private-device-token");
        await using var diagnostics = new GameSessionDiagnostics(dir.Root, log);
        diagnostics.Collect(); Assert.False(File.Exists(log));
        File.AppendAllText(content, "new content error\n"); diagnostics.Collect(); diagnostics.Collect();
        Write(dir.Root, Edition + "/logs/new.log", "new file\n");
        Write(dir.Root, Edition + "/bootstrapStorage/crash/new.crashedsession", "private-account-token");
        Write(dir.Root, Edition + "/bootstrapStorage/crash/memory.dmp", "private-memory-token");
        diagnostics.Collect();
        var text = File.ReadAllText(log);
        Assert.DoesNotContain("old session", text); Assert.DoesNotContain("old.crashedsession", text);
        Assert.Contains("new content error", text); Assert.Equal(1, text.Split("new content error").Length - 1);
        Assert.Contains("new file", text); Assert.Contains("new.crashedsession", text); Assert.Contains("memory.dmp", text);
        Assert.DoesNotContain("private-", text); Assert.Equal("private-device-token", File.ReadAllText(report));
        Assert.False(diagnostics.HasUnhandledException); // A marker alone is not proof of an exception.
    }

    [Fact]
    public async Task TailHandlesTruncationUtf8AndFinalFlush()
    {
        using var dir = new TestDirectory(); var log = Path.Combine(dir.Root, "journal.log");
        var file = Write(dir.Root, Edition + "/logs/ContentLog.txt", "very long old session\n");
        var diagnostics = new GameSessionDiagnostics(dir.Root, log);
        File.WriteAllText(file, "ação\n"); diagnostics.Collect();
        File.AppendAllText(file, "última mensagem\n");
        await diagnostics.DisposeAsync(); await diagnostics.DisposeAsync();
        var text = File.ReadAllText(log); Assert.Contains("ação", text); Assert.Contains("última mensagem", text);
        Assert.DoesNotContain("old session", text); Assert.DoesNotContain("�", text);
    }

    [Fact]
    public async Task EnablesOnlyFileLoggingAndPreservesOtherPreferences()
    {
        using var dir = new TestDirectory(); var log = Path.Combine(dir.Root, "journal.log");
        var file = Write(dir.Root, Edition + "/Users/123/games/com.mojang/minecraftpe/options.txt",
            "gfx_vsync:1\r\ncontent_log_file:0\r\ncontent_log_gui:0\r\ncustom:keep\r\n");
        await using var diagnostics = new GameSessionDiagnostics(dir.Root, log);
        await diagnostics.EnableContentLogsAsync(default);
        var text = File.ReadAllText(file);
        Assert.Contains("content_log_file:1\r\n", text); Assert.Contains("content_log_gui:0\r\n", text);
        Assert.Contains("gfx_vsync:1\r\n", text); Assert.Contains("custom:keep\r\n", text);
        await diagnostics.EnableContentLogsAsync(default); Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public async Task RejectsFileAndDirectoryLinksOutsideInstance()
    {
        using var dir = new TestDirectory(); using var outside = new TestDirectory();
        var secret = Write(outside.Root, "private.txt", "never collect me");
        var logs = Path.Combine(dir.Root, Edition, "logs"); Directory.CreateDirectory(logs);
        File.CreateSymbolicLink(Path.Combine(logs, "linked.txt"), secret);
        var users = Path.Combine(dir.Root, Edition, "Users"); Directory.CreateDirectory(users);
        Directory.CreateSymbolicLink(Path.Combine(users, "linked"), outside.Root);
        var log = Path.Combine(dir.Root, "journal.log");
        await using var diagnostics = new GameSessionDiagnostics(dir.Root, log);
        File.AppendAllText(secret, "more secret"); diagnostics.Collect();
        await diagnostics.EnableContentLogsAsync(default);
        Assert.DoesNotContain("secret", File.ReadAllText(log));
        Assert.Equal("never collect memore secret", File.ReadAllText(secret));
    }

    [Fact]
    public async Task RecognizesUnhandledFaultEvenWhenWrapperReturnsZeroAndIgnoresRoutineWarnings()
    {
        using var dir = new TestDirectory(); var log = Path.Combine(dir.Root, "journal.log");
        await using var diagnostics = new GameSessionDiagnostics(dir.Root, log);
        diagnostics.ObserveOutput("fixme: Not supported\n"); Assert.False(diagnostics.HasUnhandledException);
        await new ProcessRunner().RunAsync(new("/bin/sh", ["-c", "printf 'wine: Unhandled page'; sleep .05; printf ' fault on read access to 0000000000000000\\n' >&1"], dir.Root),
            log, default, diagnostics.ObserveOutput);
        Assert.True(diagnostics.HasUnhandledException);
        Assert.Contains("[sh] Exited with code 0", File.ReadAllText(log));
    }

    [Fact]
    public void GameDiagnosticsDefaultsDoNotOverrideExplicitUserSettings()
    {
        var defaults = new Dictionary<string, string?> { ["WINEDEBUG"] = "-all" };
        GameSessionDiagnostics.ConfigureEnvironment(defaults);
        Assert.Equal("-all,err+all", defaults["WINEDEBUG"]); Assert.Null(defaults["VKD3D_LOG_FILE"]);
        Assert.Equal("none", defaults["DXVK_LOG_PATH"]);
        var result = InstanceLaunchPlan.Environment(new() { Environment = new Dictionary<string, string> { ["WINEDEBUG"] = "+seh", ["VKD3D_LOG_FILE"] = "/tmp/custom.log" } }, defaults);
        Assert.Equal("+seh", result["WINEDEBUG"]); Assert.Equal("/tmp/custom.log", result["VKD3D_LOG_FILE"]);
    }
}
