using Orion.Infrastructure.CurseForge;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.Storage;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

if (args.Contains("--comprehensive")) return await LiveValidation.RunAsync(args);

// Explicit, opt-in live smoke check. No actual user instance is read or modified.
using var client = new CurseForgeClient();
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
try
{
    if (!client.IsConfigured) { Console.Error.WriteLine("Build has no CurseForge credential."); return 1; }
    var categories = await client.CategoriesAsync(timeout.Token);
    Console.WriteLine($"Bedrock categories: {string.Join(", ", categories.Select(c => c.Name))}");
    var projects = await client.SearchAsync("", CurseForgeClient.AddonsClassId, 0, timeout.Token);
    Console.WriteLine($"Search: {projects.Data.Length} projects; {projects.Pagination.TotalCount} total.");
    if (args.Contains("--covers"))
    {
        using var pictures = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
        var checkedImages = 0;
        foreach (var project in projects.Data.Where(p => p.CoverUrl is not null).Take(6))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, project.CoverUrl);
            if (request.Headers.Contains("x-api-key")) throw new InvalidOperationException("Unexpected image credential.");
            using var response = await pictures.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType is not ("image/jpeg" or "image/png" or "image/webp"))
                throw new InvalidDataException("Unsupported project image format.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[16384]; var length = 0; int count;
            while ((count = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                if ((length += count) > 2 * 1024 * 1024) throw new InvalidDataException("Project image exceeds the download limit.");
            if (length == 0) throw new InvalidDataException("Empty project image.");
            checkedImages++; Console.WriteLine($"Project {project.Id}: cover verified ({length} bytes; no API credential).");
        }
        if (checkedImages == 0) throw new InvalidDataException("No project artwork was provided.");
        Console.WriteLine($"Cover checks passed: {checkedImages}."); return 0;
    }
    var explicitProject = args.FirstOrDefault(a => a.StartsWith("--project="));
    var candidates = explicitProject is null ? projects.Data.Take(8)
        : new[] { new CfProject(int.Parse(explicitProject.Split('=')[1]), 78022, "", "", [], new(""), null, 0) };
    foreach (var project in candidates)
    {
        var files = await client.FilesAsync(project.Id, 0, timeout.Token);
        Console.WriteLine($"Project {project.Id}: {files.Data.Length} files retrieved.");
        if (!args.Contains("--download")) return 0;
        var file = files.Data.FirstOrDefault(f => f.IsAvailable && f.FileLength is > 0 and < 2 * 1024 * 1024
            && Path.GetExtension(f.FileName).ToLowerInvariant() is ".mcpack" or ".mcaddon" && f.DownloadUrl is not null);
        if (file is null || project.AllowModDistribution == false) continue;
        var directory = Directory.CreateTempSubdirectory("orion-cf-smoke-");
        try
        {
            var download = Path.Combine(directory.FullName, "download" + Path.GetExtension(file.FileName));
            await client.DownloadAsync(project.Id, file.Id, download, null, timeout.Token);
            Console.WriteLine($"Download integrity verified: {file.FileLength} bytes.");
            if (args.Contains("--inspect"))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(download);
                foreach (var entry in zip.Entries.Take(30))
                    Console.WriteLine($"ZIP entry: {entry.FullName} (mode {((entry.ExternalAttributes >> 16) & 0xf000):x})");
            }
            var paths = new AppPaths(Path.Combine(directory.FullName, "data"), Path.Combine(directory.FullName, "config"),
                Path.Combine(directory.FullName, "cache"), Path.Combine(directory.FullName, "run"), Path.Combine(directory.FullName, "apps"));
            var id = Guid.NewGuid();
            Directory.CreateDirectory(Path.Combine(paths.Prefix(id), "drive_c/users/fixture/AppData/Roaming/Minecraft Bedrock/Users/Shared/games/com.mojang"));
            var content = new InstanceContentService(paths, new());
            await content.ImportAsync(id, download, null, timeout.Token);
            Console.WriteLine($"Temporary fixture import verified: {(await content.ListAsync(id, timeout.Token)).Entries.Count} packs.");
            return 0;
        }
        finally { Directory.Delete(directory.FullName, true); }
    }
    Console.WriteLine("Metadata checks passed; no small eligible package found for download smoke check.");
    return 0;
}
catch (Exception ex)
{
    // Client errors deliberately exclude response bodies and credential headers.
    Console.Error.WriteLine(ex.Message); return 1;
}
