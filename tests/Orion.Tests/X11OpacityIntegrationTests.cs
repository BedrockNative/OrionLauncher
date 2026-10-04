using System.Diagnostics;
using System.Runtime.InteropServices;
using Orion.Desktop.Theming;

namespace Orion.Tests;

public sealed class X11OpacityIntegrationTests
{
    // Explicit opt-in: an unmapped, disposable X window, never an existing user window.
    [Fact]
    public async Task NativeOpacityPropertyIsSetAndRemovedOnAnOwnedWindow()
    {
        if (Environment.GetEnvironmentVariable("ORION_TEST_X11_OPACITY") != "1") return;
        var display = XOpenDisplay(0);
        Assert.NotEqual(0, display);
        var window = XCreateSimpleWindow(display, XDefaultRootWindow(display), 0, 0, 32, 32, 0, 0, 0);
        try
        {
            Assert.NotEqual(0, window);
            XSync(display, false);
            using var request = new X11WindowOpacity(window);
            await request.SetEnabledAsync(true);
            Assert.Equal(uint.MaxValue, X11WindowOpacity.ParseValue(await ReadAsync()));
            await request.SetEnabledAsync(false);
            Assert.Null(X11WindowOpacity.ParseValue(await ReadAsync()));
        }
        finally { if (window != 0) XDestroyWindow(display, window); XCloseDisplay(display); }

        async Task<string> ReadAsync()
        {
            using var process = Process.Start(new ProcessStartInfo("xprop")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                ArgumentList = { "-id", "0x" + window.ToString("x"), "_NET_WM_WINDOW_OPACITY" }
            })!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
                return output;
            }
            finally { if (!process.HasExited) process.Kill(); }
        }
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern int XDestroyWindow(nint display, nint window);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
}
