using Avalonia.Controls;
using Avalonia.Headless;
using Orion.Desktop.Views;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class WindowAttentionTests
{
    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    [InlineData(WindowState.Normal)]
    public async Task AttentionPreservesWindowStateAndSize(WindowState state)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch(() =>
        {
            var window = new MainWindow { Width = 1050, Height = 720 };
            window.Show(); window.WindowState = state;
            window.Reveal();
            Assert.Equal(state, window.WindowState);
            Assert.Equal(1050, window.Width); Assert.Equal(720, window.Height);
            window.Close();
        }, CancellationToken.None);
    }
}
