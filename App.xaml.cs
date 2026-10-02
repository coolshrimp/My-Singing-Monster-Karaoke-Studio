using System.Windows;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--install-ffmpeg", StringComparer.OrdinalIgnoreCase))
        {
            if (e.Args.Contains("--quiet", StringComparer.OrdinalIgnoreCase))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await new FfmpegService().EnsureInstalledAsync(); Shutdown(0); }
                catch { Shutdown(1); }
                return;
            }
            new FfmpegSetupWindow().Show();
        }
        else new MainWindow().Show();
    }
}
