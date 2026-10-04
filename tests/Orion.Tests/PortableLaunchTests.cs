using System.Reflection;

namespace Orion.Tests;

// Changes process-wide environment; do not run alongside other test collections.
[Collection("Avalonia UI")]
public sealed class PortableLaunchTests
{
    [Fact]
    public void AppImageShortcutsRemainPersistentButSupervisorsReuseTheRunningApphost()
    {
        var previous = Environment.GetEnvironmentVariable("APPIMAGE");
        try
        {
            const string image = "/tmp/Orion launcher.AppImage";
            Environment.SetEnvironmentVariable("APPIMAGE", image);
            var program = typeof(Orion.Desktop.App).Assembly.GetType("Orion.Desktop.Program")!;
            IReadOnlyList<string> Command(string name) => (IReadOnlyList<string>)program
                .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            Assert.Equal(new[] { image }, Command("ExecutableCommand"));
            var supervisor = Command("SupervisorCommand");
            Assert.Equal(Environment.ProcessPath, supervisor[0]);
            Assert.DoesNotContain(image, supervisor);
            if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                Assert.Equal(typeof(Orion.Desktop.App).Assembly.Location, supervisor[1]);
        }
        finally { Environment.SetEnvironmentVariable("APPIMAGE", previous); }
    }
}
