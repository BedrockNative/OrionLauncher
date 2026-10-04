using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Orion.Desktop.I18n;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Content;
using SkiaSharp;

namespace Orion.Tests;

[Collection("Avalonia UI")]
public sealed class WorldMetadataTests
{
    [Fact]
    public async Task WorldCardsUseLevelNameLatestSaveTimeAndSafeImageFallback()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(UiTests));
        await session.Dispatch<bool>(async () =>
        {
            using var dir = new TestDirectory(); var id = Guid.NewGuid(); var users = ContentTests.Setup(dir, id);
            var world = Path.Combine(users, "12345/games/com.mojang/minecraftWorlds/opaque-folder-id"); Directory.CreateDirectory(Path.Combine(world, "db"));
            File.WriteAllText(Path.Combine(world, "level.dat"), "metadata"); File.WriteAllText(Path.Combine(world, "levelname.txt"), "  §bMinha §daventura  \n");
            var save = Path.Combine(world, "db/save.ldb"); File.WriteAllText(save, "save");
            var date = DateTime.UtcNow.AddMinutes(3); File.SetLastWriteTimeUtc(save, date);
            var assets = Path.Combine(dir.Paths.Game(id), "data/gui/dist/hbui/assets"); Directory.CreateDirectory(assets);
            var fallback = Path.Combine(assets, "world-preview-default-fixture.jpg");
            using (var bitmap = new SKBitmap(64, 36))
            {
                bitmap.Erase(SKColors.Red); using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
                using var stream = File.Create(fallback); data.SaveTo(stream);
            }
            var content = new InstanceContentService(dir.Paths, new());
            var entry = Assert.Single((await content.ListAsync(id)).Entries);
            Assert.Equal("Minha aventura", entry.Name); Assert.NotNull(entry.ModifiedAt);
            Assert.Contains("§bMinha", File.ReadAllText(Path.Combine(world, "levelname.txt")));
            Assert.True(Math.Abs((entry.ModifiedAt.Value.UtcDateTime - date).TotalSeconds) < 1);
            Assert.Null(entry.IconPath); Assert.Equal(fallback, entry.FallbackIconPath);
            using (var row = new LibraryPackRow(entry))
            {
                Assert.NotNull(row.Icon); using var pixels = new MemoryStream(); row.Icon.Save(pixels); pixels.Position = 0;
                using var decoded = SKBitmap.Decode(pixels); var color = decoded.GetPixel(0, 0);
                Assert.Equal(color.Red, color.Green); Assert.Equal(color.Green, color.Blue);
            }
            File.WriteAllText(Path.Combine(world, "world_icon.jpeg"), "broken image");
            using (var row = new LibraryPackRow(Assert.Single((await content.ListAsync(id)).Entries))) Assert.NotNull(row.Icon);
            File.Copy(fallback, Path.Combine(world, "world_icon.jpeg"), true);
            using (var row = new LibraryPackRow(Assert.Single((await content.ListAsync(id)).Entries))) Assert.NotNull(row.Icon);
            var model = new ContentManagementViewModel(content, id, "Sobrevivência", new Localizer()); await model.RefreshAsync();
            var window = new ContentWindow { DataContext = model }; window.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var frame = window.CaptureRenderedFrame())
            {
                Assert.NotNull(frame);
                if (Environment.GetEnvironmentVariable("ORION_SCREENSHOT_DIR") is { } output) { Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "world-metadata.png")); }
            }
            await model.StopAsync(); window.Close(); return true;
        }, CancellationToken.None);
    }
}
