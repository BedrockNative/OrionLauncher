using AsmResolver;
using AsmResolver.PE.File;
using AsmResolver.PE.Imports;
using AsmResolver.PE.Imports.Builder;
using AsmResolver.IO;

namespace Orion.Infrastructure.Games;

/// <summary>Uses AsmResolver to extend only a copy of the decrypted executable's imports.</summary>
public static class OptimizationImport
{
    public static byte[] Add(byte[] source, string dll)
    {
        if (dll.Any(c => c < 32 || c > 126)) throw new InvalidDataException("Optimization DLL path must be ASCII.");
        var file = PEFile.FromBytes(source);
        if (file.FileHeader.Machine != MachineType.Amd64 || file.OptionalHeader.Magic != OptionalHeaderMagic.PE32Plus)
            throw new InvalidDataException("Optimizations require an x64 PE image.");
        var originalRvas = file.Sections.Select(s => s.Rva).ToArray();
        var directory = file.OptionalHeader.GetDataDirectory(DataDirectoryIndex.ImportDirectory);
        using var descriptors = new MemoryStream();
        if (directory.VirtualAddress != 0)
        {
            var reader = file.CreateReaderAtRva(directory.VirtualAddress, directory.Size);
            var terminated = false;
            for (var i = 0; i < Math.Min(directory.Size / 20, 4096); i++)
            {
                var descriptor = reader.ReadBytes(20);
                if (descriptor.All(b => b == 0)) { terminated = true; break; }
                descriptors.Write(descriptor);
            }
            if (!terminated) throw new InvalidDataException("Unterminated import directory.");
        }
        // Retain existing descriptors and IAT RVAs verbatim. Rebuilding every native
        // import would require trampolines, including special treatment of data imports.
        var previous = new DataSegment(descriptors.ToArray());
        var imports = new ImportDirectoryBuffer(is32Bit: false);
        var module = new ImportedModule(dll);
        module.Symbols.Add(new ImportedSymbol(0, "OrionOptimizationInitialize"));
        imports.AddModule(module);
        var contents = new SegmentBuilder();
        contents.Add(previous);
        contents.Add(imports);
        contents.Add(imports.ImportAddressDirectory, 8);
        file.Sections.Add(new PESection(".orion", SectionFlags.ContentInitializedData | SectionFlags.MemoryRead | SectionFlags.MemoryWrite, contents));
        file.UpdateHeaders();
        if (!file.Sections.Take(originalRvas.Length).Select(s => s.Rva).SequenceEqual(originalRvas))
            throw new InvalidDataException("Adding optimization imports would move native game code.");
        file.OptionalHeader.SetDataDirectory(DataDirectoryIndex.ImportDirectory,
            new DataDirectory(previous.Rva, checked((uint)descriptors.Length + 40)));
        file.OptionalHeader.SetDataDirectory(DataDirectoryIndex.CertificateDirectory, default(DataDirectory));
        file.OptionalHeader.SetDataDirectory(DataDirectoryIndex.BoundImportDirectory, default(DataDirectory));
        file.OptionalHeader.CheckSum = 0;
        using var output = new MemoryStream();
        file.Write(new BinaryStreamWriter(output));
        return output.ToArray();
    }
}
