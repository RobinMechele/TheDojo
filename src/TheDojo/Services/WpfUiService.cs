using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace TheDojo.Services;

public sealed class WpfUiService : IUiService
{
    public string? AskSavePath(string title, string suggestedName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            FileName = suggestedName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // another app holds the clipboard; the user can try again
        }
    }

    public void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    public void Restart(string exePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return;
        }

        Application.Current?.Shutdown();
    }

    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
