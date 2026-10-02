using System.Windows;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public partial class FfmpegSetupWindow : Window
{
    private bool _working, _ready;
    public FfmpegSetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InstallAsync();
        Closing += (_, e) => { if (_working) e.Cancel = true; };
    }

    private async Task InstallAsync()
    {
        if (_working) return;
        _working = true;
        FinishButton.IsEnabled = false;
        FinishButton.Content = "Installing…";
        SetupMessage.Text = "Downloading and verifying FFmpeg if needed. First setup requires an internet connection.";
        try
        {
            await new FfmpegService().EnsureInstalledAsync(new Progress<double>(value => { if (_working) SetupProgress.Value = value; }));
            _ready = true;
            SetupMessage.Text = "FFmpeg is ready. My Singing Monster Karaoke Studio can now convert audio and export song packages.";
            FinishButton.Content = "Done";
        }
        catch (Exception ex) { SetupMessage.Text = "Setup failed: " + ex.Message; FinishButton.Content = "Retry"; }
        finally { _working = false; FinishButton.IsEnabled = true; }
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (_ready) Close();
        else await InstallAsync();
    }
}
