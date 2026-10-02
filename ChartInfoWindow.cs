using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MySingingMonsterKaraokeStudio.Models;
using MySingingMonsterKaraokeStudio.Services;

namespace MySingingMonsterKaraokeStudio;

public sealed class ChartInfoWindow : Window
{
    private readonly Dictionary<string, TextBox> _fields = [];
    public SongProject Info { get; }
    public ChartInfoWindow(Window owner, SongProject project)
    {
        Info = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(project))!;
        Owner = owner; Title = "Chart Info"; Width = 560; Height = 730;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(25, 34, 49)); Foreground = Brushes.White;
        Resources[typeof(Button)] = owner.FindResource(typeof(Button));
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Song details · " + project.Difficulty + " level", FontSize = 20, Margin = new Thickness(0,0,0,14) });
        Add(panel, "Song name", project.Name);
        Add(panel, "Short name", string.IsNullOrWhiteSpace(project.ShortName) ? project.Name[..Math.Min(20, project.Name.Length)] : project.ShortName);
        Add(panel, "Artist", project.Author); Add(panel, "Release year", project.Year.ToString());
        Add(panel, "Genre", project.Genre); Add(panel, "Description", project.Description);
        Add(panel, "BPM", project.Bpm.ToString(CultureInfo.InvariantCulture));
        Add(panel, "Beats per bar", project.TimeSignature.ToString());
        Add(panel, "Difficulty rating (1–10)", TmbService.Rating(project).ToString());
        Add(panel, "Note spacing", project.TmbMetadata.TryGetValue("savednotespacing", out var spacing) ? spacing.ToString() : "180");
        Add(panel, "Note start color (#RRGGBB)", GetColor(project, "note_color_start", "#6B8CFF"));
        Add(panel, "Note end color (#RRGGBB)", GetColor(project, "note_color_end", "#D5E5FF"));
        panel.Children.Add(new TextBlock { Text = "Details are saved to the project and exported chart. Other difficulty levels keep their own rating. Updating an installed song requires Update in Game.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSteelBlue, Margin = new Thickness(0,12,0,12) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0,0,8,0) };
        var save = new Button { Content = "Save Info", IsDefault = true };
        save.Click += (_,_) =>
        {
            try
            {
                Info.Name = Text("Song name"); if (Info.Name.Length == 0) throw new InvalidDataException("Enter a song name.");
                Info.ShortName = Text("Short name"); Info.Author = Text("Artist"); Info.Genre = Text("Genre"); Info.Description = Text("Description");
                Info.Year = int.Parse(Text("Release year")); if (Info.Year is < 0 or > 9999) throw new InvalidDataException("Use a valid release year.");
                Info.Bpm = double.Parse(Text("BPM"), CultureInfo.InvariantCulture); Info.TimeSignature = int.Parse(Text("Beats per bar"));
                Info.LevelRatings[Info.Difficulty] = int.Parse(Text("Difficulty rating (1–10)"));
                var value = double.Parse(Text("Note spacing"), CultureInfo.InvariantCulture);
                if (!double.IsFinite(value) || value is < 1 or > 2000) throw new InvalidDataException("Use note spacing from 1 to 2000.");
                Info.TmbMetadata["savednotespacing"] = JsonSerializer.SerializeToElement(value);
                SetColor(Info, "note_color_start", Text("Note start color (#RRGGBB)"));
                SetColor(Info, "note_color_end", Text("Note end color (#RRGGBB)"));
                ChartValidation.Validate(Info); DialogResult = true;
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException or ArgumentException)
            { MessageBox.Show(this, ex.Message, "Chart Info", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        buttons.Margin = new Thickness(22,10,22,16);
        var layout = new DockPanel(); DockPanel.SetDock(buttons,Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
    }
    private string Text(string key) => _fields[key].Text.Trim();
    private void Add(Panel panel, string label, string value)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,8,0,4) });
        var box = new TextBox { Text = value, Padding = new Thickness(8), Background = new SolidColorBrush(Color.FromRgb(37,49,71)), Foreground = Brushes.White, MaxLength = label == "Description" ? 4000 : 300 };
        _fields[label] = box; panel.Children.Add(box);
    }
    private static string GetColor(SongProject p, string key, string fallback)
    {
        if (!p.TmbMetadata.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < 3) return fallback;
        try { return "#" + string.Concat(value.EnumerateArray().Take(3).Select(x => ((byte)Math.Clamp(Math.Round(x.GetDouble()*255),0,255)).ToString("X2"))); }
        catch (InvalidOperationException) { return fallback; }
    }
    private static void SetColor(SongProject p, string key, string text)
    {
        if (text.Length != 7 || text[0] != '#' || !text[1..].All(Uri.IsHexDigit)) throw new InvalidDataException("Use a color such as #6B8CFF.");
        var color = (Color)ColorConverter.ConvertFromString(text);
        p.TmbMetadata[key] = JsonSerializer.SerializeToElement(new[] { color.R/255d, color.G/255d, color.B/255d });
    }
}
