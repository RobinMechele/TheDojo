using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using TheDojo.Core;
using TheDojo.Core.Updates;
using TheDojo.Services;
using TheDojo.ViewModels;

namespace TheDojo;

public partial class App : Application
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var offline = e.Args.Contains("--offline", StringComparer.OrdinalIgnoreCase);
        var ui = new WpfUiService();
        var update = CreateUpdater(offline, ui);
        var viewModel = new MainViewModel(DojoService.CreateDefault(offline: offline), ui, update: update);
        var window = new MainWindow { DataContext = viewModel };
        MainWindow = window;
        window.Show();
        _ = viewModel.LoadAsync();
        _ = update.CheckAsync();
    }

    /// <summary>Self-update only applies to the published single-file exe, never to <c>dotnet run</c> or a dev build.</summary>
    private static UpdateViewModel CreateUpdater(bool offline, IUiService ui)
    {
        var current = UpdateService.CurrentVersion();
        var exe = Environment.ProcessPath;
        var isInstalledExe = string.IsNullOrEmpty(Assembly.GetEntryAssembly()?.Location)
            && exe is not null
            && string.Equals(Path.GetFileName(exe), AppIdentity.FileName + ".exe", StringComparison.OrdinalIgnoreCase);
        if (isInstalledExe)
        {
            UpdateService.CleanUp(exe!);
        }

        if (offline || !isInstalledExe || Environment.GetEnvironmentVariable("DOJO_NO_UPDATE") is { Length: > 0 })
        {
            return new UpdateViewModel(null, null, current, ui);
        }

        var asset = UpdateService.AssetNameFor(AppIdentity.FileName, RuntimeInformation.ProcessArchitecture);
        return new UpdateViewModel(new UpdateService(Http, UpdateService.DefaultRepository, asset), exe, current, ui);
    }
}
