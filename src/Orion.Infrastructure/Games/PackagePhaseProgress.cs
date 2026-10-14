using System.Diagnostics;
using Orion.Domain;

namespace Orion.Infrastructure.Games;

/// <summary>Keep high-frequency block callbacks off the UI/log path except every 250 ms.</summary>
public sealed class PackagePhaseProgress(string label, long total, IProgress<OperationProgress>? progress)
{
    private readonly Stopwatch timer = Stopwatch.StartNew();
    private long lastReport = -250;
    public void Report(long completed) => Emit(Math.Clamp(completed, 0, Math.Max(0, total - 1)), false);
    public void Complete() => Emit(total, true);
    private void Emit(long completed, bool force)
    {
        if (!force && timer.ElapsedMilliseconds - lastReport < 250) return;
        lastReport = timer.ElapsedMilliseconds;
        var fraction = total == 0 ? (force ? 1d : 0d) : (double)completed / total;
        progress?.Report(new($"{label} · {completed:N0} / {total:N0} · {fraction:P1}", fraction));
    }
}
