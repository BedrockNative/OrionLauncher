using System.Reflection.PortableExecutable;

namespace Orion.Tests;

public sealed class NativeBuildTests
{
    [Fact]
    public void BuildDependencyProducesAWindowsX64DllNextToLauncher()
    {
        var path = Path.Combine(Path.GetDirectoryName(typeof(Orion.Desktop.App).Assembly.Location)!, "Orion.Native.dll");
        using var file = File.OpenRead(path);
        using var pe = new PEReader(file);
        Assert.Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        Assert.True(pe.PEHeaders.CoffHeader.Characteristics.HasFlag(Characteristics.Dll));
        Assert.False(pe.HasMetadata);
        Assert.True(pe.PEHeaders.PEHeader!.ExportTableDirectory.Size > 0);
    }
}
