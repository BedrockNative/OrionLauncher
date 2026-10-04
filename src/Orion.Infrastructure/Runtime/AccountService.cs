using Orion.Domain;
using Orion.Infrastructure.Processes;
using Orion.Infrastructure.Releases;
using Orion.Infrastructure.Storage;
using System.Text.Json;

namespace Orion.Infrastructure.Runtime;

public sealed record XboxAccount(string Id, string Username, bool Active);

public sealed class AccountService(AppPaths paths, RuntimeManager runtimes, ProcessRunner runner,
    XodusEnvironment environment, XodusService service)
{
    private readonly SemaphoreSlim profileRequests = new(1, 1);
    private string ProfilePath(string id)
    {
        if (id.Length != 64 || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid account ID.");
        return Path.Combine(paths.Cache, "xbox-profiles", id + ".json");
    }

    public async Task<XboxProfile?> ReadCachedProfileAsync(string id, CancellationToken ct)
    {
        var path = ProfilePath(id);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8192) return null;
            await using var stream = File.OpenRead(path);
            var profile = await JsonSerializer.DeserializeAsync<XboxProfile>(stream, AtomicFile.Json, ct);
            return profile?.IsValidFor(id) == true ? profile : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public async Task<XboxProfile?> ReadProfileAsync(string id, CancellationToken ct)
    {
        _ = ProfilePath(id);
        await profileRequests.WaitAsync(ct);
        try { return await FetchProfileAsync(id, ct); }
        finally { profileRequests.Release(); }
    }

    private async Task<XboxProfile?> FetchProfileAsync(string id, CancellationToken ct)
    {
        var path = ProfilePath(id);
        var installed = await runtimes.GetInstalledAsync(RuntimeDefinition.Xodus, ct);
        if (installed is null || !Version.TryParse(installed.Tag.TrimStart('v'), out var version)
            || version < new Version(0, 6, 0)) return null;
        // A separate read-only query: never changes the current account or prompts on startup.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var json = await runner.CaptureAsync(new(RuntimeManager.FindExecutable(installed.Directory, "xodus-cli"),
            ["accounts", "profile", id], paths.XodusProfile, environment.Create()), timeout.Token,
            "Xbox profile lookup is unavailable. Try refreshing accounts later.");
        var profile = JsonSerializer.Deserialize<XboxProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (profile?.IsValidFor(id) != true) throw new InvalidDataException("Invalid Xbox profile metadata.");
        try { await AtomicFile.WriteJsonAsync(path, profile, ct); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A read-only cache must not hide the live profile. */ }
        return profile;
    }

    public async Task<IReadOnlyList<XboxAccount>?> ReadInstalledAsync(CancellationToken ct)
    {
        var installed = await runtimes.GetInstalledAsync(RuntimeDefinition.Xodus, ct);
        if (installed is null) return null;
        if (!SupportsAccounts(installed)) return null;
        return await ReadAsync(installed, ["accounts", "list"], ct);
    }

    public async Task<IReadOnlyList<XboxAccount>> RefreshAsync(IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var installed = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, ct);
        return await ReadAsync(installed, ["accounts", "--unlock", "list"], ct);
    }

    public async Task<IReadOnlyList<XboxAccount>> ChangeAsync(string action, string id, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        if (action is not ("select" or "remove") || id.Length != 64 || !id.All(char.IsAsciiHexDigit))
            throw new ArgumentException("Invalid account operation.");
        var installed = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, ct);
        await service.ResetAsync(ct);
        return await ReadAsync(installed, ["accounts", "--unlock", action, id], ct);
    }

    public static bool SupportsAccounts(RuntimeInstallation installation) =>
        Version.TryParse(installation.Tag.TrimStart('v'), out var version) && version >= new Version(0, 4, 0);

    public async Task<string?> ResolveForLaunchAsync(RuntimeInstallation installation, string? selectedId, CancellationToken ct)
    {
        if (!Version.TryParse(installation.Tag.TrimStart('v'), out var version) || version < new Version(0, 5, 0))
            throw new InvalidOperationException("Per-instance account sessions require Xodus 0.5.0 or newer. Update runtimes first.");
        var accounts = await ReadAsync(installation, ["accounts", "--unlock", "list"], ct);
        return ResolveAccount(accounts, selectedId);
    }

    public static string? ResolveAccount(IReadOnlyList<XboxAccount> accounts, string? selectedId)
    {
        if (selectedId is null) return accounts.SingleOrDefault(a => a.Active)?.Id;
        return accounts.FirstOrDefault(a => a.Id == selectedId)?.Id
            ?? throw new InvalidOperationException("This instance's account was removed. Choose another account in Launch options; no other identity was used.");
    }

    private async Task<IReadOnlyList<XboxAccount>> ReadAsync(RuntimeInstallation installation, string[] arguments, CancellationToken ct)
    {
        if (!SupportsAccounts(installation)) throw new InvalidOperationException("Account management requires Xodus 0.4.0 or newer. Update runtimes and refresh accounts.");
        var json = await runner.CaptureAsync(new(RuntimeManager.FindExecutable(installation.Directory, "xodus-cli"), arguments,
            paths.XodusProfile, environment.Create()), ct);
        var accounts = JsonSerializer.Deserialize<XboxAccount[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Xodus returned an invalid account list.");
        if (accounts.Any(a => a is null || a.Id is null || a.Id.Length != 64 || !a.Id.All(char.IsAsciiHexDigit)
                || string.IsNullOrWhiteSpace(a.Username) || a.Username.Length > 512 || a.Username.Any(char.IsControl))
            || accounts.Select(a => a.Id).Distinct().Count() != accounts.Length || accounts.Count(a => a.Active) > 1)
            throw new InvalidDataException("Xodus returned invalid account metadata.");
        return accounts;
    }

    public Task LoginAsync(IProgress<OperationProgress>? progress, CancellationToken ct) => RunAsync("login", progress, ct);
    public Task LogoutAsync(IProgress<OperationProgress>? progress, CancellationToken ct) => RunAsync("logout", progress, ct);

    private async Task RunAsync(string action, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        var xodus = await runtimes.EnsureAsync(RuntimeDefinition.Xodus, progress, ct);
        // The service keeps tokens in memory; end our service before changing the profile's account.
        await service.ResetAsync(ct);
        progress?.Report(new(action == "login" ? "Complete any desktop keyring prompt, then sign in through the Xodus window" : "Signing out of the Orion profile"));
        await runner.RunAsync(new(RuntimeManager.FindExecutable(xodus.Directory, "xodus-cli"), [action],
            paths.XodusProfile, environment.Create()), Path.Combine(paths.Logs, "account.log"), ct);
    }
}
