using System.Reflection;

namespace MySingingMonsterKaraokeStudio.Services;

public static class WebEditorAssets
{
    public static string EnsureExtracted()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1";
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MSM Song Studio", "WebEditor", version);

        Directory.CreateDirectory(root);
        Extract("index.html", root);
        Extract("style.css", root);
        Extract("editor.js", root);
        Extract("trim.js", root);
        Extract("preview.js", root);
        return root;
    }

    private static void Extract(string fileName, string destinationFolder)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var suffix = $"WebEditor.{fileName}";
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded editor resource not found: {fileName}");

        var destination = Path.Combine(destinationFolder, fileName);
        using var input = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Could not open embedded resource: {resourceName}");
        using var output = File.Create(destination);
        input.CopyTo(output);
    }
}
