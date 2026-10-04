using System.Net;
using System.Text.Json;
using Avalonia.Headless;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Infrastructure.Content;
using Orion.Infrastructure.CurseForge;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class CurseForgeWorkflowTests
{
    private sealed class Api : HttpMessageHandler
    {
        public bool FailCategoriesOnce { get; set; }
        public bool FailSearchOnce { get; set; }
        public bool CancelSearchOnce { get; set; }
        public bool EmptyCategoriesOnce { get; set; }
        public int CategoryCalls { get; private set; }
        public List<int> SearchOffsets { get; } = [];
        public Dictionary<string, string> LastQuery { get; private set; } = [];
        public Func<Task>? BeforeSearch { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.curseforge.com", request.RequestUri!.Host);
            var query = request.RequestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
            object body;
            if (request.RequestUri.AbsolutePath.EndsWith("/categories"))
            {
                CategoryCalls++;
                if (FailCategoriesOnce) { FailCategoriesOnce = false; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                var categories = new[] { new CfCategory(4984, "Addons", true), new(6913, "Maps", true), new(6929, "Textures", true), new(6940, "Scripts", true), new(6925, "Skins", true) };
                body = new { data = EmptyCategoriesOnce ? [] : categories }; EmptyCategoriesOnce = false;
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/files"))
            {
                var offset = int.Parse(query["index"]);
                body = new CfPage<CfFile>(Enumerable.Range(offset, 20).Select(i =>
                    new CfFile(100 + i, 1, 78022, "Version " + i, "example.mcaddon", 100, true, null, [], ["1.21"], [new(88, 3)])).ToArray(), new(offset, 20, 40));
            }
            else
            {
                LastQuery = query; var offset = int.Parse(query["index"]); SearchOffsets.Add(offset);
                if (BeforeSearch is { } wait) await wait();
                if (FailSearchOnce) { FailSearchOnce = false; return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
                if (CancelSearchOnce) { CancelSearchOnce = false; throw new OperationCanceledException(); }
                var count = query["searchFilter"] == "empty" ? 0 : 20;
                body = new CfPage<CfProject>(Enumerable.Range(offset, count).Select(i =>
                    new CfProject(i + 1, 78022, "Project " + i + " · " + query["classId"], "Description", [new("Author")], new("https://www.curseforge.com/minecraft-bedrock/addons/example"), true, 100)).ToArray(), new(offset, count, count == 0 ? 0 : 60));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
        }
    }

    private static async Task Run(Func<CurseForgeViewModel, Api, Task> check)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var directory = new TestDirectory(); var id = Guid.NewGuid(); ContentTests.Setup(directory, id);
            var api = new Api(); using var client = new CurseForgeClient(api, new Api(), () => "fixture-key");
            var model = ContentUiTests.Browser(directory, id, client, new Localizer());
            try { await check(model, api); }
            finally { await model.StopAsync(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public Task SearchFiltersPaginationFilesDependenciesAndEmptyResults() => Run(async (model, api) =>
    {
        await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(3, model.Categories.Count); Assert.Equal(20, model.Projects.Count);
        Assert.Equal("1–20 / 60", model.PageLabel);
        await model.NextProjectsCommand.ExecuteAsync(null); Assert.Equal(21, model.Projects[0].Id);
        await model.PreviousProjectsCommand.ExecuteAsync(null); Assert.Equal(1, model.Projects[0].Id);
        Assert.Equal(new[] { 4984, 6913, 6929 }, model.Categories.Select(c => c.Id));
        model.Category = model.Categories[0]; model.Query = "  building & decoration  ";
        await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Equal("4984", api.LastQuery["classId"]); Assert.Equal("building & decoration", api.LastQuery["searchFilter"]);
        model.Project = model.Projects[0]; await model.LoadFilesCommand.ExecuteAsync(null);
        Assert.Equal(20, model.Files.Count); Assert.Contains("88", model.DependencyHint);
        await model.MoreFilesCommand.ExecuteAsync(null); Assert.Equal(40, model.Files.Count);
        await model.MoreFilesCommand.ExecuteAsync(null); Assert.Equal(40, model.Files.Count);
        model.Query = "empty"; await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Empty(model.Projects); Assert.Empty(model.Files); Assert.Null(model.Project); Assert.Null(model.SelectedFile);
        Assert.Equal("0", model.PageLabel); Assert.False(model.IsBusy); Assert.Null(model.Error);
    });

    [Fact]
    public Task CategoriesAreAvailableWithoutNetworkBeforeFirstSearch() => Run(async (model, api) =>
    {
        Assert.Equal(3, model.Categories.Count);
        api.FailCategoriesOnce = true;
        model.Category = model.Categories[1];
        await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(0, api.CategoryCalls);
        Assert.Equal("6913", api.LastQuery["classId"]);
    });

    [Fact]
    public Task FailedNextPageCanRetryWithoutSkippingProjects() => Run(async (model, api) =>
    {
        await model.SearchProjectsCommand.ExecuteAsync(null);
        api.FailSearchOnce = true; await model.NextProjectsCommand.ExecuteAsync(null);
        Assert.NotNull(model.Error); Assert.Equal(1, model.Projects[0].Id);
        Assert.Equal("1–20 / 60", model.PageLabel);
        await model.NextProjectsCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(new[] { 0, 20, 20 }, api.SearchOffsets);
        Assert.Equal(21, model.Projects[0].Id);
    });

    [Fact]
    public Task FailedPreviousPagePreservesCursorAndCanRetry() => Run(async (model, api) =>
    {
        await model.SearchProjectsCommand.ExecuteAsync(null);
        await model.NextProjectsCommand.ExecuteAsync(null);
        api.FailSearchOnce = true; await model.PreviousProjectsCommand.ExecuteAsync(null);
        Assert.NotNull(model.Error); Assert.Equal("21–40 / 60", model.PageLabel);
        await model.PreviousProjectsCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.Equal(new[] { 0, 20, 0, 0 }, api.SearchOffsets);
        Assert.Equal("1–20 / 60", model.PageLabel);
    });

    [Fact]
    public Task FailedNewSearchKeepsTheLastSuccessfulQueryAndCategoryForPagination() => Run(async (model, api) =>
    {
        model.Query = "original"; await model.SearchProjectsCommand.ExecuteAsync(null);
        await model.NextProjectsCommand.ExecuteAsync(null);
        model.Query = "replacement"; model.Category = model.Categories[1];
        api.FailSearchOnce = true; await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.NotNull(model.Error); Assert.Equal("21–40 / 60", model.PageLabel);
        await model.NextProjectsCommand.ExecuteAsync(null);
        Assert.Equal("original", api.LastQuery["searchFilter"]); Assert.Equal("4984", api.LastQuery["classId"]);
        Assert.Equal(41, model.Projects[0].Id);
        await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Equal("replacement", api.LastQuery["searchFilter"]); Assert.Equal("6913", api.LastQuery["classId"]);
        Assert.Equal("1–20 / 60", model.PageLabel);
    });

    [Fact]
    public Task CancellationDoesNotAdvancePage() => Run(async (model, api) =>
    {
        await model.SearchProjectsCommand.ExecuteAsync(null);
        api.CancelSearchOnce = true; await model.NextProjectsCommand.ExecuteAsync(null);
        Assert.Null(model.Error); Assert.False(model.IsBusy); Assert.Equal("1–20 / 60", model.PageLabel);
        await model.NextProjectsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { 0, 20, 20 }, api.SearchOffsets); Assert.Equal(21, model.Projects[0].Id);
    });

    [Fact]
    public Task OpeningLoadsPopularProjectsOnceAndHomeClearsSearch() => Run(async (model, api) =>
    {
        await model.OpenAsync();
        Assert.True(model.HasSearched); Assert.Equal(20, model.Projects.Count);
        Assert.Equal("", api.LastQuery["searchFilter"]); Assert.Equal("4984", api.LastQuery["classId"]);
        await model.OpenAsync(); Assert.Single(api.SearchOffsets);
        model.Query = "furniture"; await model.SearchProjectsCommand.ExecuteAsync(null);
        Assert.Equal(model.Text["CfSearchResults"], model.ResultsTitle);
        await model.OpenAsync(); Assert.Equal(2, api.SearchOffsets.Count);
        await model.HomeCommand.ExecuteAsync(null);
        Assert.Equal("", model.Query); Assert.Equal("", api.LastQuery["searchFilter"]);
        Assert.Equal(model.Text["CfPopular"], model.ResultsTitle);
    });

    [Fact]
    public Task ChangingHomeCategoryLoadsItsPopularPageAndClearsDetailsAndPagination() => Run(async (model, api) =>
    {
        await model.OpenAsync(); await model.NextProjectsCommand.ExecuteAsync(null);
        model.Project = model.Projects[0]; await model.LoadFilesCommand.ExecuteAsync(null);
        model.Category = model.Categories[1]; await model.WaitForIdleAsync();
        Assert.Equal("6913", api.LastQuery["classId"]); Assert.Equal("", api.LastQuery["searchFilter"]);
        Assert.Equal("1–20 / 60", model.PageLabel); Assert.Null(model.Project); Assert.Empty(model.Files); Assert.Null(model.SelectedFile);
        Assert.Equal("Maps", model.ResultsCategory); Assert.Equal(model.Text["CfPopular"], model.ResultsTitle);
        await model.NextProjectsCommand.ExecuteAsync(null); Assert.Equal("6913", api.LastQuery["classId"]);
        model.Category = model.Categories[2]; await model.WaitForIdleAsync();
        Assert.Equal("6929", api.LastQuery["classId"]); Assert.False(model.CanPrevious);
        var requests = api.SearchOffsets.Count; model.Text.SetLanguage("pt-BR"); model.RefreshLabels();
        await model.WaitForIdleAsync(); Assert.Equal(requests, api.SearchOffsets.Count); Assert.Equal("Texturas", model.ResultsCategory);
    });

    [Fact]
    public Task FailedCategorySwitchNeverShowsOldHomeAndCanRetry() => Run(async (model, api) =>
    {
        await model.OpenAsync(); api.FailSearchOnce = true;
        model.Category = model.Categories[1]; await model.WaitForIdleAsync();
        Assert.NotNull(model.Error); Assert.Empty(model.Projects); Assert.False(model.CanNext); Assert.False(model.CanPrevious);
        Assert.Equal("Maps", model.ResultsCategory);
        model.Category = model.Categories[2]; await model.WaitForIdleAsync();
        Assert.Null(model.Error); Assert.Equal("6929", api.LastQuery["classId"]); Assert.Equal(20, model.Projects.Count);
    });

    [Fact]
    public Task RapidCategoryChangesIgnoreStaleHomeResponse() => Run(async (model, api) =>
    {
        await model.OpenAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeSearch = async () => { started.TrySetResult(); await release.Task; };
        model.Category = model.Categories[1]; await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(model.Projects);
        var published = new List<string>();
        model.Projects.CollectionChanged += (_, e) => { if (e.NewItems is { } items) foreach (CfProject item in items) published.Add(item.Name); };
        model.Category = model.Categories[2]; api.BeforeSearch = null; release.SetResult();
        await model.WaitForIdleAsync();
        Assert.Equal("6929", api.LastQuery["classId"]); Assert.Equal("Textures", model.ResultsCategory); Assert.Equal(20, model.Projects.Count);
        Assert.Null(model.Error); Assert.False(model.IsBusy);
        Assert.All(published, name => Assert.Contains("6929", name));
    });

    [Fact]
    public Task InitialFailureCanRetryAndClosedSessionCannotReload() => Run(async (model, api) =>
    {
        api.FailSearchOnce = true; await model.OpenAsync();
        Assert.NotNull(model.Error); Assert.False(model.HasSearched);
        await model.OpenAsync(); Assert.Null(model.Error); Assert.True(model.HasSearched);
        await model.StopAsync(); await model.HomeCommand.ExecuteAsync(null);
        Assert.Equal(2, api.SearchOffsets.Count);
    });

    [Fact]
    public Task LanguageRefreshPreservesSelectedContentType() => Run((model, api) =>
    {
        model.Category = model.Categories[1];
        model.Text.SetLanguage("pt-BR"); model.RefreshLabels();
        Assert.Equal("Mapas", model.Category!.Name);
        Assert.Equal(6913, model.Category.Id); Assert.Equal(0, api.CategoryCalls);
        return Task.CompletedTask;
    });
}
