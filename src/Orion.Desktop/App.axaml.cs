using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Orion.Desktop.ViewModels;
using Orion.Desktop.Views;
using Orion.Infrastructure.Linux;

namespace Orion.Desktop;

public partial class App : Avalonia.Application
{
    private MainWindow window = null!;
    private MainViewModel model = null!;
    private TrayIcon? tray;
    private bool quitting;
    private bool quitDialog;
    private Task ready = Task.CompletedTask;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Theming.ThemeManager.Apply("dark", "mint");
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window = new MainWindow();
            model = new(Program.Services, window);
            window.DataContext = model;
            desktop.MainWindow = window;
            using var iconStream = AssetLoader.Open(new Uri("avares://OrionLauncher/Assets/orion.png"));
            var icon = new WindowIcon(iconStream); window.Icon = icon;
            var menu = new NativeMenu();
            var show = new NativeMenuItem(model.Text["Show"]);
            show.Click += (_, _) => Reveal();
            var exit = new NativeMenuItem(model.Text["Quit"]);
            exit.Click += async (_, _) => await QuitAsync(desktop);
            menu.Items.Add(show); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(exit);
            tray = new TrayIcon { Icon = icon, ToolTipText = "Orion Launcher", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => Reveal();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            model.Text.PropertyChanged += (_, _) => { show.Header = model.Text["Show"]; exit.Header = model.Text["Quit"]; };
            model.AttentionRequested += Reveal;
            window.Closing += async (_, e) =>
            {
                if (quitting) return;
                e.Cancel = true;
                if (model.KeepInBackground) window.Hide();
                else await QuitAsync(desktop);
            };
            desktop.ShutdownRequested += async (_, e) =>
            {
                if (quitting) return;
                e.Cancel = true;
                await QuitAsync(desktop);
            };
            ready = model.InitializeAsync();
            Program.Instance.Listen(request => Dispatcher.UIThread.Post(async () => await HandleAsync(request)));
            var opened = false;
            window.Opened += async (_, _) =>
            {
                if (opened) return;
                opened = true;
                if (Program.Request.Background) window.Hide();
                await ready;
                model.StartXodusOnStartup();
                _ = model.RuntimeUpdates.CheckOnStartupAsync(model.CheckRuntimeUpdatesOnStartup);
                await HandleAsync(Program.Request);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task HandleAsync(LaunchRequest request)
    {
        await ready;
        if (!request.Background) Reveal();
        if (request.Instance is { } id) await model.PlayByIdAsync(id);
        if (request.RtxLink is not null || request.RtxFile is not null)
        {
            Reveal(); model.Page = "rtx";
            await model.Rtx.ReceiveAsync(request.RtxLink, request.RtxFile);
        }
    }

    private void Reveal() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }

    private async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (quitting || quitDialog) return;
        quitDialog = true;
        try
        {
            if (model.HasActivity)
            {
                Reveal();
                if (!await window.ConfirmAsync(model.Text["ExitTitle"], model.Text["ExitText"], model.Text["Quit"], model.Text["Cancel"])) return;
            }
            quitting = true;
            await model.StopAllAsync();
            // Finish asynchronous cleanup while the dispatcher can still process
            // continuations. Program's finalizers safely await these same tasks.
            await Program.Services.DisposeAsync();
            await Program.Instance.DisposeAsync();
            tray?.Dispose(); desktop.Shutdown();
        }
        finally { quitDialog = false; }
    }
}
