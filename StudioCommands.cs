using System.Windows.Input;

namespace MySingingMonsterKaraokeStudio;

public static class StudioCommands
{
    private static RoutedUICommand Make(string name, Key key = Key.None, ModifierKeys modifiers = ModifierKeys.None) =>
        new(name, name, typeof(StudioCommands), key == Key.None ? [] : new InputGestureCollection { new KeyGesture(key, modifiers) });
    public static RoutedUICommand New { get; } = Make("New", Key.N, ModifierKeys.Control);
    public static RoutedUICommand Open { get; } = Make("Open", Key.O, ModifierKeys.Control);
    public static RoutedUICommand OpenFolder { get; } = Make("OpenFolder");
    public static RoutedUICommand OpenChart { get; } = Make("OpenChart");
    public static RoutedUICommand SaveChart { get; } = Make("SaveChart", Key.S, ModifierKeys.Control);
    public static RoutedUICommand SaveMidiTmb { get; } = Make("SaveMidiTmb");
    public static RoutedUICommand ChartInfo { get; } = Make("ChartInfo");
    public static RoutedUICommand Library { get; } = Make("Library");
    public static RoutedUICommand SaveProject { get; } = Make("SaveProject", Key.S, ModifierKeys.Control | ModifierKeys.Alt);
    public static RoutedUICommand SaveProjectAs { get; } = Make("SaveProjectAs", Key.S, ModifierKeys.Control | ModifierKeys.Shift);
    public static RoutedUICommand Import { get; } = Make("Import");
    public static RoutedUICommand Convert { get; } = Make("Convert");
    public static RoutedUICommand SaveOggAs { get; } = Make("SaveOggAs");
    public static RoutedUICommand Generate { get; } = Make("Generate");
    public static RoutedUICommand Trim { get; } = Make("Trim");
    public static RoutedUICommand Build { get; } = Make("Build");
    public static RoutedUICommand Export { get; } = Make("Export");
    public static RoutedUICommand ImportGame { get; } = Make("ImportGame");
    public static RoutedUICommand RemoveGame { get; } = Make("RemoveGame");
    public static RoutedUICommand GameSongs { get; } = Make("GameSongs");
    public static RoutedUICommand GameFolder { get; } = Make("GameFolder");
    public static RoutedUICommand ChangeGameFolder { get; } = Make("ChangeGameFolder");
    public static RoutedUICommand RefreshGame { get; } = Make("RefreshGame");
    public static RoutedUICommand WorkingFolder { get; } = Make("WorkingFolder");
    public static RoutedUICommand ProjectFolder { get; } = Make("ProjectFolder");
    public static RoutedUICommand StudioFolder { get; } = Make("StudioFolder");
    public static RoutedUICommand ChangeWorkingFolder { get; } = Make("ChangeWorkingFolder");
    public static RoutedUICommand Undo { get; } = Make("Undo", Key.Z, ModifierKeys.Control);
    public static RoutedUICommand Redo { get; } = Make("Redo", Key.Y, ModifierKeys.Control);
    public static RoutedUICommand Delete { get; } = Make("Delete", Key.Delete);
    public static RoutedUICommand SelectAll { get; } = Make("SelectAll", Key.A, ModifierKeys.Control);
    public static RoutedUICommand ClearSelection { get; } = Make("ClearSelection", Key.Escape);
    public static RoutedUICommand Play { get; } = Make("Play");
    public static RoutedUICommand Stop { get; } = Make("Stop");
    public static RoutedUICommand Setup { get; } = Make("Setup");
    public static RoutedUICommand Exit { get; } = Make("Exit");
}
