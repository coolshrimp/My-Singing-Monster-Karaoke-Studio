using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MySingingMonsterKaraokeStudio;

public sealed class SongNameDialog : Window
{
    private readonly TextBox _name;
    public string SongName => _name.Text.Trim();

    public SongNameDialog(Window owner, string name)
    {
        Owner = owner; Title = "Rename Song";
        Width = 440; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(25, 34, 49));
        Foreground = Brushes.White; ShowInTaskbar = false;
        Resources[typeof(Button)] = owner.FindResource(typeof(Button));
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Song name", FontSize = 16, Margin = new Thickness(0, 0, 0, 8) });
        _name = new TextBox
        {
            Text = name, FontSize = 14, Padding = new Thickness(9), MaxLength = 150,
            Background = new SolidColorBrush(Color.FromRgb(37, 49, 71)), Foreground = Brushes.White
        };
        panel.Children.Add(_name);
        panel.Children.Add(new TextBlock
        {
            Text = "Used for the song title and the audio and chart filenames in the ZIP.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0, 10, 0, 18)
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 85, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "Rename", IsDefault = true, MinWidth = 85, IsEnabled = !string.IsNullOrWhiteSpace(name) };
        save.Click += (_, _) => { if (SongName.Length > 0) DialogResult = true; };
        _name.TextChanged += (_, _) => save.IsEnabled = SongName.Length > 0;
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }
}
