using Orion.Domain;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.CurseForge;
using Orion.Infrastructure.Games;
using Orion.Infrastructure.Storage;

// Opt-in network validation. All imported content stays in one disposable fixture.
// Never executes downloaded code, launches a browser, or reads real launcher instances.
internal static class LiveValidation
{
    private sealed class Progress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }

    private sealed class AuditHandler(bool api) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(api ? request.RequestUri?.Host == "api.curseforge.com" : CurseForgeClient.IsDownloadUri(request.RequestUri!), "Unexpected HTTP destination");
            Check(request.Headers.Contains("x-api-key") == api, "Credential header isolation failed");
            Check(request.Headers.Authorization is null, "Unexpected authorization header");
            Requests++;
            return base.SendAsync(request, ct);
        }
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); }
    private static string Safe(string value) => new(value.Where(c => !char.IsControl(c)).Take(240).ToArray());

    public static async Task<int> RunAsync(string[] args)
    {
        CurseForgeCredential.CaptureDeveloperOverride();
        var key = CurseForgeCredential.Read();
        if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("No CurseForge credential configured."); return 1; }
        using var api = new AuditHandler(true);
        using var cdn = new AuditHandler(false);
        using var client = new CurseForgeClient(api, cdn, () => key);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        var ct = deadline.Token;
        var fixture = Directory.CreateTempSubdirectory("orion-cf-validation-");
        var failures = 0; var passed = 0; var downloads = 0; var manual = 0; var paginatedFiles = false;
        var tested = new HashSet<int>();
        async Task<bool> Run(string name, Func<Task> work)
        {
            try { ct.ThrowIfCancellationRequested(); await work(); passed++; Console.WriteLine($"PASS {name}"); return true; }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {Safe(error.Message.Replace(key, "[redacted]", StringComparison.Ordinal))}");
                return false;
            }
        }
        try
        {
            CfCategory[] categories = [];
            if (!await Run("Live authorization and Bedrock categories", async () =>
            {
                categories = await client.CategoriesAsync(ct);
                Check(categories.Length > 0 && categories.All(c => c.IsClass && c.Id > 0), "Invalid classes");
                Check(categories.Select(c => c.Id).Order().SequenceEqual(new[] { 4984, 6913, 6929 }), "Unexpected content classes");
                Console.WriteLine("Classes: " + string.Join(", ", categories.Select(c => $"{c.Id} {Safe(c.Name)}")));
            })) return 1;

            CfPage<CfProject>? first = null;
            await Run("Popular search and project pagination", async () =>
            {
                first = await client.SearchAsync("", CurseForgeClient.AddonsClassId, 0, ct); Validate(first);
                Check(first.Data.Length > 0, "No popular projects");
                var second = await client.SearchAsync("", CurseForgeClient.AddonsClassId, CurseForgeClient.PageSize, ct); Validate(second);
                Check(second.Pagination.Index == 20 && second.Data.Length > 0, "Page two missing");
                Check(!first.Data.Select(p => p.Id).Intersect(second.Data.Select(p => p.Id)).Any(), "Repeated search page");
                Console.WriteLine($"Projects: total {first.Pagination.TotalCount}; pages {first.Data.Length} + {second.Data.Length}");
            });
            foreach (var query in new[] { "furniture", "shader", "survival", "ação & gameId=432", "orion-no-result-" + Guid.NewGuid().ToString("N") })
                await Run("Text search: " + Safe(query), async () =>
                {
                    var result = await client.SearchAsync(query, CurseForgeClient.AddonsClassId, 0, ct); Validate(result);
                    Console.WriteLine($"Matches: {result.Pagination.TotalCount}");
                    if (query.StartsWith("orion-no-result-", StringComparison.Ordinal)) Check(result.Data.Length == 0, "Expected empty result");
                });

            // One small package from each class, bounded to 20 MiB each; metadata scan is bounded too.
            var filter = args.FirstOrDefault(a => a.StartsWith("--category=", StringComparison.Ordinal));
            var categoryId = filter is null ? 0 : int.Parse(filter.Split('=')[1]);
            foreach (var category in categories.Where(c => categoryId == 0 || c.Id == categoryId).Take(6))
            {
                CfPage<CfProject>? results = null;
                await Run("Category filter: " + Safe(category.Name), async () =>
                { results = await client.SearchAsync("", category.Id, 0, ct); Validate(results); });
                if (results is null) continue;
                var downloadedClass = false;
                foreach (var project in results.Data.Take(10))
                {
                    CfPage<CfFile>? versions = null;
                    if (!await Run($"File versions: {project.Id} {Safe(project.Name)}", async () =>
                    {
                        versions = await client.FilesAsync(project.Id, 0, ct);
                        Check(versions.Data.All(f => f.ModId == project.Id && f.GameId == CurseForgeClient.BedrockGameId), "Wrong file identity");
                        Console.WriteLine($"Files: {versions.Data.Length}/{versions.Pagination.TotalCount}; versions: {Safe(string.Join(", ", versions.Data.FirstOrDefault()?.GameVersions ?? []))}");
                        if (!paginatedFiles && versions.Pagination.TotalCount > 20)
                        {
                            var next = await client.FilesAsync(project.Id, 20, ct);
                            Check(next.Pagination.Index == 20 && next.Data.Length > 0, "Missing file page");
                            Check(!versions.Data.Select(f => f.Id).Intersect(next.Data.Select(f => f.Id)).Any(), "Repeated file page");
                            paginatedFiles = true;
                        }
                    })) continue;
                    var eligible = versions!.Data.Where(f => f.IsAvailable && f.FileLength is > 0 and <= 20 * 1024 * 1024
                        && Path.GetExtension(f.FileName).ToLowerInvariant() is ".mcpack" or ".mcaddon" or ".mcworld").ToArray();
                    var restricted = eligible.FirstOrDefault(f => project.AllowModDistribution == false || string.IsNullOrEmpty(f.DownloadUrl));
                    if (restricted is not null && manual == 0)
                        await Run("Live author restriction handoff", async () =>
                        {
                            var before = cdn.Requests;
                            try { await client.DownloadAsync(project.Id, restricted.Id, Path.Combine(fixture.FullName, "restricted"), null, ct); throw new InvalidDataException("Author restriction bypassed"); }
                            catch (ManualDownloadRequiredException error)
                            {
                                Check(error.File.Id == restricted.Id && CurseForgeClient.IsProjectUri(error.Page), "Invalid manual page");
                                Check(cdn.Requests == before, "Restricted CDN was contacted"); manual++;
                                Console.WriteLine("Manual page: " + error.Page.AbsoluteUri);
                            }
                        });
                    if (project.AllowModDistribution == false) continue;
                    // A maps project can include optional textures: insist on an actual world for this class.
                    var file = eligible.Where(f => category.Id != 6913 || Path.GetExtension(f.FileName).Equals(".mcworld", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f.FileLength).FirstOrDefault(f => !string.IsNullOrEmpty(f.DownloadUrl) && !tested.Contains(f.Id));
                    if (file is null) continue;
                    tested.Add(file.Id);
                    var folder = Directory.CreateDirectory(Path.Combine(fixture.FullName, file.Id.ToString())).FullName;
                    var target = Path.Combine(folder, "download" + Path.GetExtension(file.FileName));
                    var downloaded = await Run($"Real CDN download + SHA-1: {project.Id}/{file.Id}", async () =>
                    {
                        double progress = 0;
                        await client.DownloadAsync(project.Id, file.Id, target, new Progress(v => { Check(v >= progress && v <= 1, "Invalid progress"); progress = v; }), ct);
                        Check(progress == 1 && new FileInfo(target).Length == file.FileLength, "Incomplete download");
                        downloads++;
                        Console.WriteLine($"Package: {Safe(file.FileName)}; {file.FileLength} bytes; SHA-1 verified; required dependencies: {file.Dependencies.Count(d => d.RelationType == 3)}");
                    });
                    if (!downloaded) continue;
                    await Run("Import, metadata, export, archive, restore and shared distribution: " + file.Id,
                        () => ValidateContentAsync(folder, target, ct));
                    if (downloads == 1)
                    {
                        await Run("Cancel real download removes partial file", async () =>
                        {
                            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var partial = Path.Combine(folder, "cancelled.download"); var observed = false;
                            try
                            {
                                await client.DownloadAsync(project.Id, file.Id, partial, new Progress(_ => { observed = true; cancel.Cancel(); }), cancel.Token);
                                throw new InvalidDataException("Cancellation was ignored");
                            }
                            catch (OperationCanceledException) { Check(observed && !File.Exists(partial), "Partial download remains"); }
                        });
                        await Run("Retry after cancellation", async () =>
                        { await client.DownloadAsync(project.Id, file.Id, Path.Combine(folder, "retry.download"), null, ct); });
                        await Run("Browser folder workflow with real verified package (simulated browser)", async () =>
                        {
                            var browser = Directory.CreateDirectory(Path.Combine(folder, "Downloads")).FullName;
                            var name = Path.GetFileName(file.FileName);
                            Check(name == file.FileName, "Unexpected file name");
                            var final = Path.Combine(browser, name); var partial = final + ".crdownload";
                            File.Copy(target, partial);
                            var snapshot = Path.Combine(folder, "browser-copy");
                            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); stop.CancelAfter(TimeSpan.FromSeconds(10));
                            var waiting = new BrowserDownloadMonitor(TimeSpan.FromMilliseconds(50)).WaitAsync(file, () => browser, snapshot, null, stop.Token);
                            await Task.Delay(200, ct); Check(!waiting.IsCompleted && !File.Exists(snapshot), "Partial browser file accepted");
                            File.Move(partial, final); await waiting;
                            Check(File.Exists(final) && new FileInfo(snapshot).Length == file.FileLength, "Browser original lost");
                        });
                    }
                    downloadedClass = true; break;
                }
                if (!downloadedClass) Console.WriteLine("NOT COVERED live package for class: " + Safe(category.Name));
            }
            if (!paginatedFiles) Console.WriteLine("NOT COVERED live file pagination: no selected project had >20 files");
            if (manual == 0) Console.WriteLine("NOT COVERED live restricted file: use deterministic author-restriction tests; no browser automation or bypass attempted");
            if (downloads == 0) { failures++; Console.WriteLine("FAIL no real download completed"); }
        }
        finally
        {
            // Only this invocation's generated fixture is removed, never user downloads or instances.
            fixture.Delete(recursive: true);
            Console.WriteLine($"SUMMARY passed={passed} failed={failures} downloads={downloads} manual={manual} api_requests={api.Requests} cdn_requests={cdn.Requests} fixture_removed={!fixture.Exists}");
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Validate(CfPage<CfProject> page)
    {
        Check(page.Data.Length <= CurseForgeClient.PageSize && page.Pagination.ResultCount == page.Data.Length, "Invalid page size");
        Check(page.Data.All(p => p.GameId == CurseForgeClient.BedrockGameId && p.Id > 0), "Non-Bedrock project returned");
        Check(page.Data.Select(p => p.Id).Distinct().Count() == page.Data.Length, "Duplicate results");
    }

    private static async Task ValidateContentAsync(string folder, string download, CancellationToken ct)
    {
        var paths = new AppPaths(Path.Combine(folder, "data"), Path.Combine(folder, "config"), Path.Combine(folder, "cache"), Path.Combine(folder, "run"), Path.Combine(folder, "apps"));
        var activity = new InstanceActivity(); var local = new InstanceContentService(paths, activity);
        var library = new ContentLibraryService(paths, activity, local);
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            var users = Path.Combine(paths.Prefix(id), "drive_c/users/fixture/AppData/Roaming/Minecraft Bedrock/Users");
            Directory.CreateDirectory(Path.Combine(users, "Shared/games/com.mojang"));
            Directory.CreateDirectory(Path.Combine(users, "12345/games/com.mojang"));
        }
        var profile = (await local.ListAsync(ids[0], ct)).Profiles.Single().Id;
        await local.ImportAsync(ids[0], download, profile, ct);
        var entries = (await local.ListAsync(ids[0], ct)).Entries;
        Check(entries.Count > 0 && entries.All(e => !string.IsNullOrWhiteSpace(e.Name)), "Missing imported metadata");
        foreach (var entry in entries)
            Console.WriteLine($"Content: {entry.Kind}; {Safe(entry.Name)}; version={Safe(entry.Version)}; min_engine={Safe(entry.MinimumEngineVersion)}; icon={entry.IconPath is not null}");
        var first = entries[0]; var exported = Path.Combine(folder, first.Kind == ContentKind.World ? "export.mcworld" : "export.mcpack");
        await local.ExportAsync(ids[0], first.Id, exported, ct);
        await local.ImportAsync(ids[1], exported, (await local.ListAsync(ids[1], ct)).Profiles.Single().Id, ct);
        Check((await local.ListAsync(ids[1], ct)).Entries.Single().Name == first.Name, "Export/import changed content identity");
        await local.ArchiveAsync(ids[0], first.Id, ct);
        var archived = (await local.ListAsync(ids[0], ct)).Entries.Single(e => e.Archived);
        await local.RestoreAsync(ids[0], archived.Id, ct);
        Check((await local.ListAsync(ids[0], ct)).Entries.Count == entries.Count, "Restore lost content");

        var shared = await library.ImportAsync(download, ct);
        var targets = new List<ContentTarget>();
        foreach (var id in ids.Skip(2)) targets.Add(new(id, (await local.ListAsync(id, ct)).Profiles.Single().Id));
        await library.DistributeAsync(shared.Id, targets, ct);
        foreach (var target in targets)
        {
            var installed = (await local.ListAsync(target.InstanceId, ct)).Entries;
            Check(installed.Count == entries.Count, "Distribution lost content");
            foreach (var entry in installed)
            {
                var linked = new DirectoryInfo(Path.Combine(paths.Instance(target.InstanceId), entry.Id)).LinkTarget is not null;
                Check(linked == (entry.Kind != ContentKind.World), "World/pack link policy violated");
            }
        }
        if (entries.Any(e => e.Kind == ContentKind.World))
        {
            var a = (await local.ListAsync(ids[2], ct)).Entries.Single(); var b = (await local.ListAsync(ids[3], ct)).Entries.Single();
            var firstFolder = local.GetFolder(ids[2], a.Id); var secondFolder = local.GetFolder(ids[3], b.Id);
            await File.WriteAllTextAsync(Path.Combine(firstFolder, "orion-fixture-marker"), "independent copy", ct);
            Check(!File.Exists(Path.Combine(secondFolder, "orion-fixture-marker")), "World copies are not independent");
        }
    }
}
