using Orion.Desktop.ViewModels;
using Orion.Domain;
using Orion.Infrastructure.Runtime;
using Orion.Infrastructure.Storage;

namespace Orion.Tests;

public sealed class SessionAccountTests
{
    [Fact]
    public async Task AccountChoicePersistsAndNeverFallsBackToAnotherIdentity()
    {
        using var directory = new TestDirectory();
        var first = new XboxAccount(new string('a', 64), "Alex", true);
        var second = new XboxAccount(new string('b', 64), "Steve", false);
        var instance = GameInstance.Create("Pinned account", "1", "Release");
        var editor = new LaunchOptionsViewModel(instance, accounts: [first, second]);
        Assert.Null(editor.Build().AccountId);
        Assert.Equal(first.Id, AccountService.ResolveAccount([first, second], null));
        Assert.Equal(second.Id, AccountService.ResolveAccount([first with { Active = false }, second with { Active = true }], null));
        editor.SelectedAccount = editor.AccountChoices.Single(a => a.Id == second.Id);
        editor.LaunchCommand = "prime-run %command%";
        editor.ArgumentLines = "--flag";
        editor.EnvironmentLines = "CUSTOM=value";
        var repository = new InstanceRepository(directory.Paths);
        await repository.SaveAsync(instance with { LaunchOptions = editor.Build() });
        var saved = await repository.GetAsync(instance.Id);
        Assert.Equal(second.Id, saved.LaunchOptions.AccountId);
        Assert.Equal("prime-run %command%", saved.LaunchOptions.LaunchCommand);
        Assert.Equal(["--flag"], saved.LaunchOptions.Arguments);
        Assert.Equal("value", saved.LaunchOptions.Environment["CUSTOM"]);
        Assert.Equal(second.Id, AccountService.ResolveAccount([first, second], saved.LaunchOptions.AccountId));
        Assert.Throws<InvalidOperationException>(() => AccountService.ResolveAccount([first], second.Id));
        editor.SetAccounts([first], second.Id);
        Assert.Equal(second.Id, editor.Build().AccountId);
        editor.ResetCommand.Execute(null);
        Assert.Null(editor.Build().AccountId);
    }

    [Fact]
    public void GameSocketsAndIdentitiesAreDistinctButThePrivateProfileIsShared()
    {
        using var directory = new TestDirectory();
        var first = new XodusEnvironment(directory.Paths, Guid.NewGuid(), new string('a', 64)).Create();
        var second = new XodusEnvironment(directory.Paths, Guid.NewGuid(), new string('b', 64)).Create();
        var management = new XodusEnvironment(directory.Paths).Create();
        Assert.NotEqual(first["XODUS_SOCKET"], second["XODUS_SOCKET"]);
        Assert.NotEqual(first["XODUS_SOCKET"], management["XODUS_SOCKET"]);
        Assert.StartsWith("orion.xodus-", first["XODUS_SOCK_NAME"]);
        Assert.Equal(first["XODUS_CONFIG_DIR"], second["XODUS_CONFIG_DIR"]);
        Assert.Equal(first["XODUS_CONFIG_DIR"], management["XODUS_CONFIG_DIR"]);
        Assert.Null(management["XODUS_ACCOUNT_ID"]);
        Assert.Equal(new string('a', 64), first["XODUS_ACCOUNT_ID"]);
        Assert.Throws<ArgumentException>(() => new InstanceLaunchOptions { AccountId = "bad-id" }.Validate());
        Assert.Throws<ArgumentException>(() => new InstanceLaunchOptions { Environment = new() { ["XODUS_ACCOUNT_ID"] = new string('a', 64) } }.Validate());
    }
}
