using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Orion.Infrastructure.CurseForge;

namespace Orion.Desktop.Theming;

/// <summary>Best-effort EWMH opacity request for one Orion XID, never global compositor configuration.</summary>
public sealed class X11WindowOpacity : IDisposable
{
    private const string Property = "_NET_WM_WINDOW_OPACITY";
    private readonly string xid;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<string>> execute;
    private readonly CancellationTokenSource lifetime = new();
    private Task pending = Task.CompletedTask;
    private bool? requested;
    private bool applied;
    private uint? original;

    public X11WindowOpacity(nint handle, Func<IReadOnlyList<string>, CancellationToken, Task<string>>? execute = null)
    {
        if (handle == 0) throw new ArgumentException("A native window is required.", nameof(handle));
        xid = "0x" + handle.ToString("x", CultureInfo.InvariantCulture);
        this.execute = execute ?? ExecuteAsync;
    }

    // Called on the UI thread. Serialize rapid toggles without blocking rendering.
    public Task SetEnabledAsync(bool enabled)
    {
        if (requested == enabled || lifetime.IsCancellationRequested) return pending;
        requested = enabled;
        return pending = ApplyAfterAsync(pending, enabled, lifetime.Token);
    }

    private async Task ApplyAfterAsync(Task previous, bool enabled, CancellationToken cancellationToken)
    {
        await previous;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (enabled)
            {
                original = ParseValue(await execute(["-id", xid, Property], cancellationToken));
                await execute(["-id", xid, "-f", Property, "32c", "-set", Property, uint.MaxValue.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                applied = true;
            }
            else if (applied)
            {
                // Do not overwrite a newer request made by another tool or the user.
                var current = ParseValue(await execute(["-id", xid, Property], cancellationToken));
                if (current == uint.MaxValue)
                {
                    if (original is { } value)
                        await execute(["-id", xid, "-f", Property, "32c", "-set", Property, value.ToString(CultureInfo.InvariantCulture)], cancellationToken);
                    else await execute(["-id", xid, "-remove", Property], cancellationToken);
                }
                applied = false;
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            // xprop is optional, the X server may disappear, or the window may already be closed.
            // The native opaque surface still works; forced compositor rules require the UI guide.
        }
    }

    public static uint? ParseValue(string output)
    {
        var parts = output.Trim().Split('=', 2);
        return parts.Length == 2 && parts[0].Trim() == Property + "(CARDINAL)" &&
            uint.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static async Task<string> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("xprop") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Remove(CurseForgeCredential.EnvironmentKey);
        info.Environment.Remove(CurseForgeCredential.EnvironmentFile);
        using var process = Process.Start(info) ?? throw new IOException("Could not request window opacity.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            if (process.ExitCode != 0) throw new IOException("Window opacity request was not accepted.");
            return await output;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); }
}
