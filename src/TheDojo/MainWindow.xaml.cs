using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TheDojo;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Windows 10 2004+ / 11: a dark caption to match the app.</summary>
    private void UseDarkTitleBar()
    {
        const int immersiveDarkMode = 20;
        var on = 1;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            _ = DwmSetWindowAttribute(handle, immersiveDarkMode, ref on, sizeof(int));
        }
    }
}
