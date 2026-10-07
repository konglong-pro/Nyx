using System.Windows;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Nyx.App;

public partial class App : System.Windows.Application, IDisposable
{
    private SingleInstanceCoordinator? _singleInstance;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Drawing.Icon? _trayIconImage;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!IsSupportedOperatingSystem())
        {
            MessageBox.Show(
                "Nyx requires Windows 11 24H2 or later on an x64 PC.",
                "Nyx cannot start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var paths = NormalizeInputPaths(e.Args);
        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimary)
        {
            if (!SingleInstanceCoordinator.ForwardToPrimary(paths))
            {
                MessageBox.Show(
                    "Nyx is already running, but it could not receive these paths. Bring the existing window forward and try again.",
                    "Nyx is already running",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            Shutdown();
            return;
        }

        var window = new MainWindow(paths);
        MainWindow = window;
        CreateTrayIcon();
        _singleInstance.StartListening(message =>
            window.ReceiveForwardedPaths(message.Paths));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        GC.SuppressFinalize(this);
    }

    internal void ShowTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
        }
    }

    internal void HideTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void CreateTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Open Nyx", null, (_, _) => RestoreMainWindow());
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add("Exit", null, (_, _) => RequestExit());

        _trayIconImage = CreateTrayIconImage();
        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = _trayMenu,
            Icon = _trayIconImage,
            Text = "Nyx - File Shredder",
            Visible = false,
        };
        _trayIcon.MouseDoubleClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == Forms.MouseButtons.Left)
            {
                RestoreMainWindow();
            }
        };
    }

    private void RestoreMainWindow()
    {
        if (MainWindow is MainWindow window)
        {
            window.RestoreFromTray();
        }
    }

    private void RequestExit()
    {
        if (MainWindow is MainWindow window)
        {
            window.RequestExitFromTray();
            return;
        }

        Shutdown();
    }

    private static Drawing.Icon CreateTrayIconImage()
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            using var executableIcon = Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (executableIcon is not null)
            {
                return (Drawing.Icon)executableIcon.Clone();
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static string[] NormalizeInputPaths(IEnumerable<string> arguments)
    {
        return arguments
            .Select(PathInput.NormalizeOrOriginal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsSupportedOperatingSystem()
    {
        return OperatingSystem.IsWindows()
            && RuntimeInformation.OSArchitecture == Architecture.X64
            && Environment.OSVersion.Version.Build >= 26100;
    }
}
