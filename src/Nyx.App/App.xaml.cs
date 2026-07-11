using System.Windows;
using System.Runtime.InteropServices;

namespace Nyx.App;

public partial class App : Application, IDisposable
{
    private SingleInstanceCoordinator? _singleInstance;

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
        _singleInstance?.Dispose();
        _singleInstance = null;
        GC.SuppressFinalize(this);
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
