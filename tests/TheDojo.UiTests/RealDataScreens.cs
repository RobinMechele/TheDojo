using System.Windows;
using TheDojo.Core;
using TheDojo.ViewModels;
using Page = TheDojo.ViewModels.Page;

namespace TheDojo.UiTests;

/// <summary>
/// Local only (<c>DOJO_REAL_DATA=1</c>): the real window on this machine's real agent logs and live limits, rendered
/// off-screen to <c>%TEMP%\dojo-real-screens</c> to look at. No baselines: real data changes every day.
/// </summary>
public sealed class RealDataScreens
{
    [UiFact]
    public void RenderEveryPage() => UiThread.Run(() =>
    {
        if (Environment.GetEnvironmentVariable("DOJO_REAL_DATA") is not ("1" or "true"))
        {
            return;
        }

        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dojo-real-screens")).FullName;
        var vm = new MainViewModel(DojoService.CreateDefault(cacheFolder: Path.Combine(folder, "cache")), new RecordingUi(folder));
        var window = new MainWindow
        {
            DataContext = vm,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 1440,
            Height = 920,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            vm.RangeIndex = 4; // all time
            UiThread.Wait(vm.LoadAsync(), timeoutSeconds: 120);
            foreach (var page in Enum.GetValues<Page>())
            {
                vm.Navigate(page);
                UiThread.DoEvents();
                if (page == Page.Sessions)
                {
                    vm.Sessions.Selected = vm.Sessions.Rows.OrderByDescending(r => r.Cost).FirstOrDefault();
                    UiThread.DoEvents();
                }

                Snapshot.Save(Snapshot.Render(window), Path.Combine(folder, page.ToString().ToLowerInvariant() + ".png"));
            }
        }
        finally
        {
            window.Close();
        }
    });
}
