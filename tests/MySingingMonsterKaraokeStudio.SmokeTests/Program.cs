using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using MySingingMonsterKaraokeStudio;
using MySingingMonsterKaraokeStudio.Models;
using MySingingMonsterKaraokeStudio.Services;

internal static class Program
{
    private static MainWindow Window = null!;
    private static WebView2 Editor = null!;
    private static readonly List<string> Passed = [];
    private static readonly string TestRoot = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("MSM_STUDIO_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "MSMStudioTests"), Guid.NewGuid().ToString("N")));
    private static readonly ProjectService Projects = new(Path.Combine(TestRoot, "library"), Path.Combine(TestRoot, "studio-settings.json"));
    private static readonly GameSongService Game = new(Path.Combine(TestRoot, "game-data"));

    [STAThread]
    private static void Main(string[] args)
    {
        var app = new Application();
        app.Startup += async (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(TestRoot);
                Window = new MainWindow(Projects, Game) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                Editor = (WebView2)typeof(MainWindow).GetField("EditorWebView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
                Window.Show();
                await WaitUntil(() => Task.FromResult((bool)Field("_editorReady")!), "Editor readiness");
                await WaitUntil(() => Task.FromResult(((System.Windows.Controls.Button)Field("InstallFfmpegButton")!).IsEnabled), "Studio commands ready");
                if (args.Contains("--erase-only")) await RunRightDragChecks();
                else if (args.Contains("--length-only")) await RunNoteLengthChecks();
                else if (args.Contains("--curves-only")) { RunCurveFormatChecks(); await RunCurveEditorChecks(); }
                else if (args.Contains("--auto-curves-only")) await RunAutomaticCurveChecks();
                else if (args.Contains("--files-menu-only")) await RunFilesAndRecentChecks();
                else if (args.Contains("--game-only")) await RunGameIntegrationChecks();
                else if (args.Contains("--installer-only")) { WriteWave(Path.Combine(TestRoot,"source.wav")); await RunInstallerChecks(); }
                else if (Array.IndexOf(args,"--package-check") is int packageIndex && packageIndex >= 0 && packageIndex + 1 < args.Length) await RunPackageMetadataChecks(args[packageIndex+1]);
                else if (args.Contains("--library-preview-only")) await RunLibraryPreviewChecks();
                else if (Array.IndexOf(args, "--inspect-audio") is int audioIndex && audioIndex >= 0 && audioIndex + 1 < args.Length) await InspectAudio(args[audioIndex + 1]);
                else await Run();
                Console.WriteLine("PASS: " + Passed.Count + " checks");
                foreach (var check in Passed) Console.WriteLine("  " + check);
                File.WriteAllText(Path.Combine(TestRoot, "results.json"), JsonSerializer.Serialize(Passed));
                Console.WriteLine("Results: " + TestRoot);
                app.Shutdown(0);
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); app.Shutdown(1); }
        };
        Environment.ExitCode = app.Run();
    }

    private static object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window);
    private static object? Invoke(string name, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var defaults = method.GetParameters().Skip(args.Length).Select(p => p.DefaultValue);
        return method.Invoke(Window, args.Concat(defaults).ToArray());
    }
    private static Task InvokeAsync(string name, params object?[] args) => (Task)Invoke(name, args)!;
    private static async Task<JsonElement> Js(string script)
    {
        var value = await Editor.CoreWebView2.ExecuteScriptAsync(script);
        return JsonDocument.Parse(value).RootElement.Clone();
    }
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Passed.Add(name); }
    private static async Task WaitUntil(Func<Task<bool>> check, string name)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline) { if (await check()) return; await Task.Delay(80); }
        throw new TimeoutException(name);
    }
    private static async Task<JsonElement> Snapshot() => await Js("window.msmEditor.getSnapshot()");

    private static async Task Run()
    {
        Check(((System.Windows.Controls.Button)Field("InstallFfmpegButton")!).IsEnabled, "One-click FFmpeg setup is available without opening a song");
        Check(((System.Windows.Controls.TextBlock)Field("FormatHint")!).Text.Contains("Karaoke"), "The built studio displays the current TMB export target");
        Check(!StudioCommands.SaveChart.CanExecute(null, Window) && !StudioCommands.ProjectFolder.CanExecute(null, Window) && StudioCommands.WorkingFolder.CanExecute(null, Window), "Studio commands reflect project readiness");
        RunTmbFormatChecks();
        RunCurveFormatChecks();
        await RunAutomaticCurveChecks();
        await RunMp3PipelineChecks();
        var project = new SongProject { Name = "Smoke Test", ProjectFolder = TestRoot, SourceAudioFile = "source.wav" };
        WriteWave(Path.Combine(TestRoot, "source.wav"));
        Invoke("OpenProject", project);
        await WaitUntil(async () => (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Audio metadata");
        Check(Math.Abs((await Js("document.getElementById('audio').duration")).GetDouble() - 5) < .1, "Actual audio duration loads");
        await WaitUntil(() => Task.FromResult(((System.Windows.Controls.TextBlock)Field("StatusText")!).Text.StartsWith("Audio ready")), "Restored audio status");
        Check(true, "Loaded audio replaces the temporary loading status with ready");
        await WaitUntil(async () => (await Js("(() => {const c=document.getElementById('wave'),d=c.getContext('2d').getImageData(0,Math.floor(c.height/2),c.width,1).data;let count=0;for(let i=0;i<d.length;i+=4)if(d[i]>40&&d[i+2]>90)count++;return count>c.width*.2;})()")).GetBoolean(), "Decoded waveform");
        Check(true, "Waveform is decoded from actual audio");
        Check(!(await Js("document.getElementById('play').disabled")).GetBoolean(), "Play is enabled after audio loads");
        await WaitUntil(() => Task.FromResult(StudioCommands.Play.CanExecute(null, Window)), "Native playback menu ready");
        await InvokeAsync("ExecuteEditorCommandAsync", "Play");
        await WaitUntil(async () => (await Js("!document.getElementById('audio').paused")).GetBoolean(), "Native playback command");
        await InvokeAsync("ExecuteEditorCommandAsync", "Stop");
        Check((await Js("document.getElementById('audio').paused && document.getElementById('audio').currentTime === 0")).GetBoolean(), "Native Playback menu controls the editor audio");
        await Js("document.getElementById('play').click()");
        await WaitUntil(async () => (await Js("document.getElementById('audio').currentTime > .2 && !document.getElementById('audio').paused")).GetBoolean(), "Playback advancement");
        Check(true, "Playback advances in real WebView2");
        await Js("document.getElementById('play').click()");
        Check((await Js("document.getElementById('audio').paused")).GetBoolean(), "Pause works");
        await Js("document.getElementById('stop').click()");
        Check((await Js("document.getElementById('audio').currentTime")).GetDouble() < .05, "Stop resets position");
        await Js("(() => { const w=document.getElementById('wave'), r=w.getBoundingClientRect(); w.dispatchEvent(new PointerEvent('pointerdown',{clientX:r.left+r.width*.5,clientY:r.top+20,bubbles:true})); })()");
        Check(Math.Abs((await Js("document.getElementById('audio').currentTime")).GetDouble() - 2.5) < .1, "Waveform seeking works");

        await Js("(() => {const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;const c=document.getElementById('chart'),r=c.getBoundingClientRect();for(const type of ['pointerdown','pointerup'])c.dispatchEvent(new PointerEvent(type,{button:0,clientX:r.left+240,clientY:r.top+290,bubbles:true}));})()");
        var snap = await Snapshot();
        Check(snap.GetProperty("notes").GetArrayLength() == 1 && Math.Abs(snap.GetProperty("notes")[0].GetProperty("time").GetDouble() - 3) < .01, "Adding notes after scrolling uses correct time");
        await Js("document.getElementById('undo').click()");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "Undo removes added note");
        await Js("document.getElementById('redo').click()");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "Redo restores note");
        await Js("(() => {const c=document.getElementById('chart'),r=c.getBoundingClientRect();c.dispatchEvent(new MouseEvent('contextmenu',{clientX:r.left+245,clientY:r.top+290,bubbles:true,cancelable:true}));})()");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "Right-click deletion works after scrolling");
        await Js("document.getElementById('undo').click()");
        await Js("(() => {const b=document.getElementById('bpm');b.focus();b.value=144;b.dispatchEvent(new Event('change'));const o=document.getElementById('offset');o.focus();o.value=.5;o.dispatchEvent(new Event('change'));})()");
        await InvokeAsync("SaveChartFromEditorAsync");
        var saved = Projects.Load(TestRoot);
        Check(saved.Notes.Count == 1 && saved.Bpm == 144 && saved.OffsetSeconds == .5, "Host saves current chart and timing settings");
        Check(File.Exists(project.ChartPath), "Draft chart is written");
        var before = project.Notes.Count;
        try { Invoke("SaveSnapshot", "{\"projectKey\":\"stale\"}"); throw new Exception("Stale snapshot accepted"); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        Check(project.Notes.Count == before, "Stale snapshots cannot overwrite current project");
        Invoke("OpenProject", saved);
        await WaitUntil(async () => (await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "Reload chart");
        Check((await Snapshot()).GetProperty("bpm").GetDouble() == 144, "Reopening restores notes and BPM");

        var file = Path.Combine(TestRoot, "range.bin");
        File.WriteAllBytes(file, Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        foreach (var (range, status, first, length) in new[] { ("bytes=10-19", 206, 10, 10), ("bytes=-5", 206, 95, 5), ("bytes=95-", 206, 95, 5), ("bytes=100-", 416, 0, 0), ("bytes=abc-def", 416, 0, 0) })
        {
            var response = LocalResourceService.Open(file, range);
            using var content = response.Content;
            Check(response.Status == status && content.Length == length && (length == 0 || content.ReadByte() == first), "Audio range " + range);
        }
        var ffmpeg = await new FfmpegService().EnsureInstalledAsync();
        if (!File.Exists(ffmpeg)) throw new FileNotFoundException("Test requires bundled FFmpeg", ffmpeg);
        var converter = new FfmpegService(ffmpeg);
        await converter.ConvertToOggAsync(Path.Combine(TestRoot, "source.wav"), Path.Combine(TestRoot, "song.ogg"));
        Check(File.ReadAllBytes(Path.Combine(TestRoot, "song.ogg")).Take(4).SequenceEqual("OggS"u8.ToArray()), "Real FFmpeg produces OGG audio");
        var goodAudio = File.ReadAllBytes(Path.Combine(TestRoot, "song.ogg"));
        File.WriteAllText(Path.Combine(TestRoot, "bad.mp3"), "invalid audio");
        try { await converter.ConvertToOggAsync(Path.Combine(TestRoot, "bad.mp3"), Path.Combine(TestRoot, "song.ogg")); throw new Exception("Bad audio conversion accepted"); }
        catch (InvalidOperationException) { }
        Check(File.ReadAllBytes(Path.Combine(TestRoot, "song.ogg")).SequenceEqual(goodAudio), "Failed conversion preserves existing audio");
        saved.OggFile = "song.ogg";
        new TmbService().WriteTmb(saved);
        var export = new ExportService();
        var zip1 = export.ExportZip(saved); var zip2 = export.ExportZip(saved);
        Check(zip1 != zip2, "Repeated exports use distinct ZIP paths");
        using var zip = ZipFile.OpenRead(zip1);
        Check(zip.Entries.Select(x => x.FullName).Order().SequenceEqual(new[] { "Smoke Test.ogg", "Smoke Test.tmb" }), "Export ZIP names both root files after the song title");
        await InvokeAsync("SaveChartFromEditorAsync");
        await RunSelectionChecks();
        await RunPaintingAndReopenChecks();
        await RunNoteLengthChecks();
        await RunRightDragChecks();
        await RunCurveEditorChecks();
        await RunInstallerChecks();
        await RunStudioChecks(saved);
        await RunFilesAndRecentChecks();
        await RunGameIntegrationChecks();
        await RunLibraryPreviewChecks();
    }

    private static void RunTmbFormatChecks()
    {
        var service = new TmbService();
        var path = Path.Combine(TestRoot, "game-chart.tmb");
        var project = new SongProject
        {
            Name = "Game Format Check", ProjectFolder = TestRoot, Bpm = 120, OffsetSeconds = .25,
            AudioDurationSeconds = 5, Author = "Test Artist", Difficulty = "Hard",
            Notes = [new() { Time = 2, Length = .5, Pitch = 60, EndPitch = 72 }]
        };
        service.WriteTmb(project, path);
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var root = json.RootElement;
            var row = root.GetProperty("notes")[0];
            Check(!root.TryGetProperty("schema", out _) && row.ValueKind == JsonValueKind.Array && row.GetArrayLength() == 5, "Game TMB uses five-number note rows rather than the old studio draft schema");
            Check(row[0].GetDouble() == 4.5 && row[1].GetDouble() == 1 && row[2].GetDouble() == 0 && row[3].GetDouble() == 165 && row[4].GetDouble() == 165, "TMB converts seconds and timing offset to beats and slide pitches to game units");
            Check(root.GetProperty("difficulty").GetInt32() == 8 && root.GetProperty("trackRef").GetString()!.EndsWith("_hard") && root.GetProperty("author").GetString() == "Test Artist", "TMB includes game metadata, a numeric difficulty and a per-level identifier");
            Check(root.GetProperty("endpoint").GetInt32() == 14 && root.GetProperty("lyrics").ValueKind == JsonValueKind.Array && root.GetProperty("savednotespacing").GetInt32() == 180, "TMB endpoint covers the full audio duration and includes required playback fields");
        }
        var restored = service.ReadTmb(path);
        Check(restored.OffsetSeconds == 0 && restored.Notes[0].Time == 2.25 && restored.Notes[0].Length == .5 && restored.Notes[0].EndPitch == 72, "Opening a game TMB restores its audio-aligned seconds and slide endpoints");
        service.WriteTmb(restored, Path.Combine(TestRoot, "reexported.tmb"));
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestRoot, "reexported.tmb"))))
            Check(json.RootElement.GetProperty("trackRef").GetString() == project.TrackRef + "_hard", "Reopening and exporting keeps a stable track identifier");
        var midiPath = Path.Combine(TestRoot, "aligned.mid");
        new MidiService().Write(project, midiPath);
        Check(Math.Abs(new MidiService().Read(midiPath)[0].Time - 2.25) < .002, "Standard MIDI note timing includes the audio offset");
        project.OffsetSeconds = -2.25;
        service.WriteTmb(project, path);
        var cropped = service.ReadTmb(path);
        Check(cropped.Notes[0].Time == 0 && cropped.Notes[0].Length == .25 && cropped.Notes[0].Pitch == 66, "Negative timing offsets crop notes at the audio start while preserving slide position");

        // Known note rows from TromboneCharter's test-Middle_C-asc.tmb:
        // https://github.com/towai/TromboneCharter/blob/master/test-Middle_C-asc.tmb
        var fixture = Path.Combine(TestRoot, "known-note-layout.tmb");
        File.WriteAllText(fixture, """{"name":"Known Note Layout","tempo":120,"timesig":2,"difficulty":5,"notes":[[0,0.25,0,0,0],[0.5,0.25,96.25,0,96.25],[1,0.25,165,0,165],[2,1.5,0,165,165]],"lyrics":[],"savednotespacing":120}""");
        var known = service.ReadTmb(fixture);
        Check(known.Notes.Count == 4 && known.Notes.Select(n => n.Pitch).SequenceEqual(new double[] { 60, 67, 72, 60 }) && known.Notes[3].Time == 1 && known.Notes[3].Length == .75 && known.Notes[3].EndPitch == 72, "An independently defined TromboneCharter fixture imports with correct timing, pitches and slide");
        service.WriteTmb(known, path);
        using (var json = JsonDocument.Parse(File.ReadAllText(path)))
            Check(json.RootElement.GetProperty("savednotespacing").GetInt32() == 120, "Imported chart display metadata survives saving");

        var legacy = Path.Combine(TestRoot, "legacy-chart.tmb");
        File.WriteAllText(legacy, """{"schema":"msm-song-studio-draft-1","title":"Old Studio Song","tempo":120,"timesig":4,"offset":0.5,"notes":[{"time":1,"length":0.5,"pitch":69,"endPitch":69}]}""");
        var migrated = service.ReadTmb(legacy);
        service.WriteTmb(migrated, path);
        Check(service.ReadTmb(path).Notes[0].Time == 1.5 && migrated.Notes[0].Pitch == 69, "Old studio draft charts open and migrate to game TMB without losing audio alignment");
        var original = File.ReadAllBytes(path);
        migrated.Notes[0].Pitch = 90;
        try { service.WriteTmb(migrated, path); throw new Exception("Out-of-range TMB notes accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(path).SequenceEqual(original), "Unplayable pitches are rejected without overwriting an existing game chart");
    }
    private static bool IsSingleLine(IEnumerable<ChartNote> notes)
    {
        var ordered = notes.OrderBy(n => n.Time).ToList();
        return ordered.All(n => n.Length > 0) && ordered.Zip(ordered.Skip(1)).All(pair => pair.First.Time + pair.First.Length <= pair.Second.Time + 1e-8);
    }

    private static void RunCurveFormatChecks()
    {
        var curve = new ChartNote { Time = 1, Length = 2, Pitch = 60.3, EndPitch = 61.1,
            CurvePoints = [new() { Position = 0, Pitch = 60.3 }, new() { Position = .25, Pitch = 68.2 }, new() { Position = .6, Pitch = 55.6 }, new() { Position = 1, Pitch = 61.1 }] };
        var project = new SongProject { Name = "Curved Song", ProjectFolder = Path.Combine(TestRoot, "curve-format"), OffsetSeconds = .25, ShowGrid = false, Notes = [curve] };
        project.Levels["Normal"] = [NotePath.Clone(curve)]; Projects.Save(project);
        var restored = Projects.Load(project.ProjectFolder);
        Check(!restored.ShowGrid && restored.Notes[0].Pitch == 60.3 && restored.Notes[0].CurvePoints.Count == 4 && restored.Levels["Normal"][0].CurvePoints[2].Pitch == 55.6, "Projects preserve grid-free placement, fractional pitches and each level's full curve");
        var cloned = NotePath.Clone(curve); cloned.CurvePoints[1].Pitch = 65;
        Check(curve.CurvePoints[1].Pitch == 68.2, "Cloning a level does not share mutable curve points");
        var tmb = new TmbService(); tmb.WriteTmb(project);
        var rows = tmb.ReadTmb(project.ChartPath).Notes;
        Check(rows.Count == 3 && IsSingleLine(rows) && Math.Abs(rows[0].Time - 1.25) < 1e-8 && Math.Abs(rows[^1].Time + rows[^1].Length - 3.25) < 1e-8, "TMB exports a curve as contiguous non-overlapping slide segments with its full duration and audio offset");
        Check(Math.Abs(rows[0].Pitch - 60.3) < 1e-8 && Math.Abs(rows[0].EndPitch - 68.2) < 1e-8 && Math.Abs(rows[1].EndPitch - 55.6) < 1e-8 && Math.Abs(rows[^1].EndPitch - 61.1) < 1e-8, "TMB import and export retain fractional mouse positions and all curve turns");
        using (var json = JsonDocument.Parse(File.ReadAllText(project.ChartPath)))
            Check(json.RootElement.GetProperty("notes").EnumerateArray().All(r => r.GetArrayLength() == 5 && Math.Abs(r[2].GetDouble() + r[3].GetDouble() - r[4].GetDouble()) < 1e-8), "Curve segments retain the standard five-number TMB slide layout");
        var cropped = AudioDraftService.CropNotes([curve], .25, 1.75, 2.75)[0];
        Check(cropped.Time == 0 && cropped.Length == 1 && cropped.CurvePoints.Count == 3 && Math.Abs(cropped.Pitch - 68.2) < 1e-8 && Math.Abs(cropped.EndPitch - NotePath.PitchAt(curve, .75)) < 1e-8, "Trimming a curve interpolates both cut positions and keeps its interior turns");
        Check(Enumerable.Range(0, 101).All(i => Math.Abs(NotePath.PitchAt(cropped, i / 100d) - NotePath.PitchAt(curve, .25 + i / 200d)) < 1e-8), "A trimmed curve follows exactly the original retained trajectory");
        var before = JsonSerializer.Serialize(curve);
        var copied = Projects.SaveCopyAs(project, Path.Combine(TestRoot, "Curved Copy.msmproj"));
        Check(JsonSerializer.Serialize(copied.Notes[0].CurvePoints) == JsonSerializer.Serialize(curve.CurvePoints) && !copied.ShowGrid, "Save Project As preserves curve data and free placement");
        var source = new List<ChartNote> { curve, new() { Time = 1, Length = 1, Pitch = 72, EndPitch = 72 }, new() { Time = 1.4, Length = 2, Pitch = 64, EndPitch = 64 }, new() { Time = 1.8, Length = 1, Pitch = 67, EndPitch = 67 }, new() { Time = 2.2, Length = 1, Pitch = 69, EndPitch = 69 } };
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard", "Expert" })
        {
            var level = AudioDraftService.MakeLevel(source, difficulty);
            Check(IsSingleLine(level) && level.Count > 1 && level.Select(n => n.Time).Distinct().Count() == level.Count, difficulty + " generation removes simultaneous notes and overlapping sustains for one mouse");
            if (difficulty == "Expert") Check(Math.Abs(level[0].Length - .4) < 1e-8 && Math.Abs(level[0].EndPitch - NotePath.PitchAt(curve, .2)) < 1e-8, "Removing an automatic overlap cuts a curve at its actual pitch rather than stretching its shape");
        }
        Check(JsonSerializer.Serialize(curve) == before && source.Count == 5, "Automatic difficulty generation leaves the source chart and curve unchanged");
        var midi = Path.Combine(project.ProjectFolder, "one-mouse.mid");
        new MidiService().Write(new SongProject { Notes = AudioDraftService.MakeLevel(source, "Expert") }, midi);
        Check(IsSingleLine(new MidiService().Read(midi)), "MIDI quantization retains the single-mouse timing after automatic overlap cleanup");
        var good = File.ReadAllBytes(project.ChartPath);
        foreach (var invalid in new[] {
            new List<CurvePoint> { new() { Position = 0, Pitch = 60.3 }, new() { Position = .8, Pitch = 65 }, new() { Position = .4, Pitch = 66 }, new() { Position = 1, Pitch = 61.1 } },
            new List<CurvePoint> { new() { Position = 0, Pitch = 60.3 }, new() { Position = 0, Pitch = 65 }, new() { Position = 1, Pitch = 61.1 } },
            new List<CurvePoint> { new() { Position = 0, Pitch = 60.3 }, new() { Position = .5, Pitch = double.NaN }, new() { Position = 1, Pitch = 61.1 } },
            new List<CurvePoint> { new() { Position = 0, Pitch = 60.3 }, new() { Position = .5, Pitch = 80 }, new() { Position = 1, Pitch = 61.1 } }
        })
        {
            var bad = NotePath.Clone(curve); bad.CurvePoints = invalid; project.Notes = [bad];
            try { tmb.WriteTmb(project); throw new Exception("Invalid curve accepted"); } catch (InvalidDataException) { }
            Check(File.ReadAllBytes(project.ChartPath).SequenceEqual(good), "Malformed or out-of-range curve is rejected while preserving the saved TMB");
        }
    }

    private static async Task RunCurveEditorChecks()
    {
        var song = Projects.CreateAtFile(Path.Combine(TestRoot, "Freehand Song.msmproj"));
        WriteWave(Path.Combine(song.ProjectFolder, "source.wav")); song.SourceAudioFile = "source.wav"; Projects.Save(song);
        Invoke("OpenProject", song);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")! && (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Freehand editor ready");
        await Js("(() => {document.getElementById('tool').value='curve';document.getElementById('grid').click();const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;v.scrollTop=44;})()");
        Check(!(await Snapshot()).GetProperty("showGrid").GetBoolean() && (await Js("document.getElementById('snap').disabled")).GetBoolean(), "Grid Off disables snapping and enables free mouse placement");
        await WaitUntil(async () => (await Js("(() => {const c=document.getElementById('chart'),r=devicePixelRatio||1,d=c.getContext('2d').getImageData(Math.floor(100*r),Math.floor(22*r),1,1).data;return d[0]===16&&d[1]===18&&d[2]===24;})()")).GetBoolean(), "Grid-free canvas rendered");
        Check(true, "Grid-free canvas has no pitch rows or beat lines");
        await Js("document.getElementById('chart').addEventListener('pointerdown',e=>window.curveTrusted=e.isTrusted,{once:true})");
        await BrowserMouse("mousePressed", 850, 292, true, "left");
        await BrowserMouse("mouseMoved", 910, 215, true, "left");
        await BrowserMouse("mouseMoved", 990, 305, true, "left");
        await BrowserMouse("mouseMoved", 1090, 248, true, "left");
        await Js("document.getElementById('chart').dispatchEvent(new PointerEvent('lostpointercapture',{buttons:1,pointerId:1,bubbles:true}))");
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean(), "A held curved stroke continues when WebView2 drops pointer capture");
        Check((await Js("window.curveTrusted && window.msmEditor.isInteracting()")).GetBoolean(), "Trusted mouse dragging records a live freehand curve");
        await BrowserMouse("mouseReleased", 1090, 248, false, "left");
        var snapshot = await Snapshot(); var note = snapshot.GetProperty("notes")[0];
        var original = note.Clone();
        Check(snapshot.GetProperty("notes").GetArrayLength() == 1 && note.GetProperty("curvePoints").GetArrayLength() > 4 && Math.Abs(note.GetProperty("time").GetDouble() - 850d / 240) < 1e-8 && Math.Abs(note.GetProperty("length").GetDouble() - 1) < 1e-8, "A curved stroke produces one smooth note with continuous unsnapped timing");
        Check(Math.Abs(note.GetProperty("pitch").GetDouble() - (73.5 - 292d / 22)) < 1e-8 && note.GetProperty("curvePoints").EnumerateArray().Max(p => p.GetProperty("pitch").GetDouble()) > 63, "The curve retains fractional mouse positions and its upward and downward turns");
        await Key("z", true); Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "One Undo removes the complete freehand curve");
        await Key("y", true); Check((await Snapshot()).GetProperty("notes")[0].GetProperty("curvePoints").GetRawText() == original.GetProperty("curvePoints").GetRawText(), "One Redo restores every point of the curved note");
        await Gesture(850, 292, 898, 281);
        note = (await Snapshot()).GetProperty("notes")[0];
        Check(Math.Abs(note.GetProperty("time").GetDouble() - original.GetProperty("time").GetDouble() - .2) < 1e-8 && Math.Abs(note.GetProperty("pitch").GetDouble() - original.GetProperty("pitch").GetDouble() - .5) < 1e-8 && note.GetProperty("curvePoints").GetArrayLength() == original.GetProperty("curvePoints").GetArrayLength(), "Moving a curve in free placement shifts every point by the same fractional pitch and time");
        await Key("z", true);
        await Gesture(1088, 248, 1160, 248);
        note = (await Snapshot()).GetProperty("notes")[0];
        Check(Math.Abs(note.GetProperty("length").GetDouble() - 1.3) < 1e-8 && note.GetProperty("curvePoints").GetRawText() == original.GetProperty("curvePoints").GetRawText(), "Dragging a curve's end stretches its duration while preserving its normalized shape");
        await Key("z", true);
        await Js("document.getElementById('noteLength').value=.8;document.getElementById('applyLength').click()");
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("length").GetDouble() == .8 && (await Snapshot()).GetProperty("notes")[0].GetProperty("curvePoints").GetRawText() == original.GetProperty("curvePoints").GetRawText(), "Set length preserves the complete curve shape");
        await Key("z", true); await Key("Escape");
        await Js("document.getElementById('tool').value='select'");
        await Gesture(900, 205, 925, 225);
        Check(await SelectedCount() == 1, "A selection box intersects the curve's raised turn rather than only its starting row");
        await Pointer("pointerdown", 1000, 215, button: 2); await Pointer("pointerup", 1000, 215, button: 2); await ContextMenu(1000, 215);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "Right-clicking empty space inside a curve's bounding box does not erase the note");
        await Pointer("pointerdown", 890, 215, button: 2); await Pointer("pointermove", 940, 215, button: 2); await Pointer("pointerup", 940, 215, button: 2); await ContextMenu(940, 215);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "Continuous right-drag erases the actual curve path away from its starting row");
        await Key("z", true);
        await Js("document.getElementById('tool').value='paint'"); await Gesture(1251, 320.5, 1251, 320.5);
        note = (await Snapshot()).GetProperty("notes")[1];
        Check(Math.Abs(note.GetProperty("time").GetDouble() - 1251d / 240) < 1e-8 && Math.Abs(note.GetProperty("pitch").GetDouble() - (73.5 - 320.5 / 22)) < 1e-8, "Grid-free Paint also places notes between pitch rows and beat divisions");
        await Key("z", true);
        await Js("document.getElementById('tool').value='curve'");
        await Pointer("pointerdown", 1450, 350); await Pointer("pointermove", 1350, 280); await Pointer("pointermove", 1250, 330); await Pointer("pointermove", 1300, 300); await Pointer("pointerup", 1300, 300);
        note = (await Snapshot()).GetProperty("notes")[1];
        var path = note.GetProperty("curvePoints").EnumerateArray().ToArray();
        Check(note.GetProperty("length").GetDouble() > 0 && Math.Abs(note.GetProperty("time").GetDouble() - 1300d / 240) < 1e-8 && path.Zip(path.Skip(1)).All(p => p.First.GetProperty("position").GetDouble() < p.Second.GetProperty("position").GetDouble()), "Reverse drawing and backtracking keep a single ordered mouse trajectory at every time");
        await Key("z", true); await Pointer("pointerdown", 1250, 350); await Pointer("pointermove", 1350, 280); await Pointer("pointercancel", 1350, 280);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "Cancelling a curve restores the previous chart without a partial stroke");
        await Pointer("pointerdown", 1250, 350); await Pointer("pointermove", 1350, 280);
        await Js("document.getElementById('chart').dispatchEvent(new PointerEvent('lostpointercapture',{buttons:0,pointerId:1,bubbles:true}))");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1 && !(await Js("window.msmEditor.isInteracting()")).GetBoolean(), "Losing capture without a held button discards an unfinished curve");
        await InvokeAsync("SaveChartFromEditorAsync");
        var saved = Projects.LoadFile(song.ProjectJsonPath);
        Check(saved.Notes[0].CurvePoints.Count > 4 && saved.Levels["Normal"][0].CurvePoints.Count == saved.Notes[0].CurvePoints.Count && !saved.ShowGrid, "Host saving preserves the full freehand curve in the active level and the Grid Off preference");
        Invoke("OpenProject", saved);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Curve project reopened");
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("curvePoints").GetRawText() == original.GetProperty("curvePoints").GetRawText() && !(await Snapshot()).GetProperty("showGrid").GetBoolean(), "Reopening restores the same editable curve and grid-free layout");
        await InvokeAsync("ChangeDifficultyAsync", "Hard"); await InvokeAsync("ChangeDifficultyAsync", "Normal");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Curve level restored");
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("curvePoints").GetRawText() == original.GetProperty("curvePoints").GetRawText(), "Switching difficulty preserves the curve when returning to the edited level");
        var overlap = new SongProject { Name = "Overlap Cleanup", ProjectFolder = Path.Combine(TestRoot, "overlap-cleanup"), ShowGrid = false,
            Notes = [new() { Time = 1, Length = 2, Pitch = 60, EndPitch = 64, CurvePoints = [new() { Position = 0, Pitch = 60 }, new() { Position = .5, Pitch = 70 }, new() { Position = 1, Pitch = 64 }] }, new() { Time = 2, Length = 2, Pitch = 62, EndPitch = 62 }, new() { Time = 2, Length = 1, Pitch = 72, EndPitch = 72 }, new() { Time = 2.5, Length = 1, Pitch = 67, EndPitch = 67 }] };
        Projects.Save(overlap); Invoke("OpenProject", overlap);
        await WaitUntil(async () => (await Snapshot()).GetProperty("notes").GetArrayLength() == 4, "Overlapping chart ready");
        Check(!(await Js("document.getElementById('singleLine').disabled")).GetBoolean(), "Existing overlapping charts offer the Remove overlaps action");
        await Js("document.getElementById('singleLine').click()");
        var cleaned = (await Snapshot()).GetProperty("notes").Deserialize<List<ChartNote>>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Check(cleaned.Count == 3 && IsSingleLine(cleaned) && cleaned[0].Length == 1 && cleaned[0].EndPitch == 70 && cleaned[1].Length == .5, "Remove overlaps trims sustains at their true curve position and removes simultaneous targets");
        await Key("z", true); Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 4 && (await Snapshot()).GetProperty("notes")[0].GetProperty("length").GetDouble() == 2, "One Undo restores the full original chart after overlap cleanup");
        await Key("y", true); await InvokeAsync("SaveChartFromEditorAsync");
        Check(IsSingleLine(Projects.Load(overlap.ProjectFolder).Notes) && IsSingleLine(new TmbService().ReadTmb(overlap.ChartPath).Notes), "Saving a cleaned curve keeps the project and exported game slides non-overlapping");
    }

    private static async Task InspectAudio(string original)
    {
        var local = Path.Combine(TestRoot, "inspection" + Path.GetExtension(original)); File.Copy(original, local);
        var ffmpegPath = await new FfmpegService().EnsureInstalledAsync();
        var draft = await new AudioDraftService().AnalyzeAsync(local, new FfmpegService(ffmpegPath));
        var levels = new Dictionary<string, object>();
        foreach (var difficulty in new[] { "Easy", "Normal", "Hard", "Expert" })
        {
            var notes = AudioDraftService.MakeAudioLevel(draft, difficulty);
            Check(IsSingleLine(notes), difficulty + " analysis of the selected audio stays playable with no overlapping notes");
            var chart = new SongProject { Name = "Audio inspection", ProjectFolder = TestRoot, Notes = notes, Difficulty = difficulty, AudioOnsets = draft.Onsets, DetectedBpm = draft.DetectedBpm };
            new TmbService().WriteTmb(chart, Path.Combine(TestRoot, difficulty + ".tmb"));
            Check(IsSingleLine(new TmbService().ReadTmb(Path.Combine(TestRoot, difficulty + ".tmb")).Notes), difficulty + " selected-audio curve export retains single-mouse timing");
            levels[difficulty] = new { Notes = notes.Count, Curves = notes.Count(n => n.CurvePoints.Count > 2), CurvePoints = notes.Sum(n => n.CurvePoints.Count) };
        }
        var summary = JsonSerializer.Serialize(new { Attacks = draft.Onsets.Count, DetectedBpm = draft.DetectedBpm, Levels = levels }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(TestRoot, "analysis-summary.json"), summary); Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(TestRoot, "detected-curves.json"), JsonSerializer.Serialize(draft.Notes));
        Check(draft.Notes.Any(n => n.CurvePoints.Count > 2), "The selected song produces actual curved melody paths");
    }

    private static async Task RunAutomaticCurveChecks()
    {
        var folder = Path.Combine(TestRoot, "automatic-curves"); Directory.CreateDirectory(folder);
        var ffmpegPath = await new FfmpegService().EnsureInstalledAsync();
        var converter = new FfmpegService(ffmpegPath); var service = new AudioDraftService();
        double Melody(double t) => 60 + 6 * Math.Pow(Math.Sin(Math.PI * t / 3), 2);
        double Pulse(double t) => .18 + .35 * Math.Exp(-(t % .5) / .04);
        var source = Path.Combine(folder, "Beat Melody.wav"); WriteSignal(source, 6, Melody, Pulse);
        var ogg = Path.Combine(folder, "Beat Melody.ogg"); await converter.ConvertToOggAsync(source, ogg);
        var draft = await service.AnalyzeAsync(ogg, converter);
        Check(draft.Notes.Count >= 6 && draft.Notes.Any(n => n.CurvePoints.Count > 2), "Real audio analysis creates curved phrases from a changing melody with rhythmic attacks");
        Check(IsSingleLine(draft.Notes) && IsSingleLine(draft.MidiNotes) && draft.Notes.All(n => n.Time >= 0 && n.Time + n.Length <= 6.04), "Detected curves and discrete MIDI notes remain ordered, non-overlapping and within the audio");
        var pitchErrors = draft.Notes.SelectMany(n => Enumerable.Range(1, 9).Select(i => Math.Abs(NotePath.PitchAt(n, i / 10d) - Melody(n.Time + n.Length * i / 10d)))).ToArray();
        Check(pitchErrors.Average() < .35 && pitchErrors.Max() < .9, "Automatic curve positions follow the known sung-pitch contour rather than decorative bends");
        Check(Enumerable.Range(1, 10).Count(i => draft.Onsets.Any(t => Math.Abs(t - i * .5) <= .08)) >= 9 && draft.DetectedBpm is double tempo && Math.Abs(tempo - 120) < 3, "Waveform attacks align with the known half-second beats and estimate their tempo");
        var snapshot = JsonSerializer.Serialize(draft.Notes);
        foreach (var level in new[] { "Easy", "Normal", "Hard", "Expert" })
        {
            var notes = AudioDraftService.MakeAudioLevel(draft, level);
            Check(IsSingleLine(notes) && notes.Any(n => n.CurvePoints.Count > 2) && notes.All(n => NotePath.Points(n).All(p => p.Pitch is >= 47 and <= 73)), level + " keeps playable melody curves and a single active mouse target");
        }
        Check(JsonSerializer.Serialize(draft.Notes) == snapshot, "Difficulty curve simplification leaves the detected melody unchanged");
        var normal = AudioDraftService.MakeAudioLevel(draft, "Normal"); var easy = AudioDraftService.MakeAudioLevel(draft, "Easy");
        Check(easy.Sum(n => n.CurvePoints.Count) <= normal.Sum(n => n.CurvePoints.Count), "Easy curves have no more path detail than Normal curves");
        var rests = Path.Combine(folder, "Melody with rests.wav"); WriteSignal(rests, 6, Melody, t => t is >= 2.5 and < 3.25 ? 0 : Pulse(t));
        var restDraft = await service.AnalyzeAsync(rests, converter);
        Check(restDraft.Notes.All(n => n.Time + n.Length <= 2.58 || n.Time >= 3.18), "Auto curves stop for an audible rest instead of bridging silence");
        var jump = Path.Combine(folder, "Pitch jump.wav"); WriteSignal(jump, 3, t => t < 1.5 ? 60 : 70, _ => .3);
        var jumpDraft = await service.AnalyzeAsync(jump, converter);
        Check(jumpDraft.Notes.Count >= 2 && jumpDraft.Notes.All(n => NotePath.Points(n).Max(p => p.Pitch) - NotePath.Points(n).Min(p => p.Pitch) < 3), "An abrupt large pitch jump becomes separate notes instead of an unrealistic connecting sweep");
        var constant = Path.Combine(folder, "Steady A4.wav"); WriteSignal(constant, 5, _ => 69, _ => .2);
        var steady = await service.AnalyzeAsync(constant, converter);
        Check(steady.Notes.Count == 1 && steady.Notes[0].Pitch == 69 && steady.Notes[0].CurvePoints.Count == 0 && steady.Notes[0].Length > 4.5, "A steady tone stays a steady note without invented curves or beat cuts");
        var silent = Path.Combine(folder, "Silence.wav"); WriteSignal(silent, 2, _ => 60, _ => 0);
        var silence = await service.AnalyzeAsync(silent, converter);
        Check(silence.Notes.Count == 0 && silence.MidiNotes.Count == 0 && silence.Onsets.Count == 0 && silence.DetectedBpm is null, "Silence produces no notes, curves or tempo guess");
        var noise = Path.Combine(folder, "Noise.wav"); WriteSignal(noise, 2, _ => 60, _ => .2, noise: true);
        Check((await service.AnalyzeAsync(noise, converter)).Notes.Count == 0, "Unpitched noise is not turned into arbitrary mouse curves");

        var mp3 = Path.Combine(folder, "Curved Beat Song.mp3");
        var info = new System.Diagnostics.ProcessStartInfo(ffmpegPath) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", source, "-c:a", "libmp3lame", mp3 }) info.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(info)!)
        {
            var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await errors);
        }
        await InvokeAsync("ImportAudioFileAsync", mp3, false);
        var project = (SongProject)Field("_project")!;
        await WaitUntil(async () => (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean() && (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Generated melody editor ready");
        Check(project.Levels.Count == 4 && project.Levels.Values.All(n => IsSingleLine(n) && n.Any(x => x.CurvePoints.Count > 2)), "Importing MP3 automatically creates curved audio-following charts for all four difficulties");
        Check(project.AudioOnsets.Count >= 10 && project.DetectedBpm is double detected && Math.Abs(project.Bpm - detected) < .001, "A new song saves detected attack times and uses a confident tempo estimate");
        Check((await Snapshot()).GetProperty("notes").EnumerateArray().Any(n => n.GetProperty("curvePoints").GetArrayLength() > 2), "The real editor receives the generated curves without losing them through MIDI");
        await WaitUntil(async () => (await Js("(() => {const c=document.getElementById('wave'),d=c.getContext('2d').getImageData(0,c.height-5,c.width,1).data;let gold=0;for(let i=0;i<d.length;i+=4)if(d[i]>100&&d[i+1]>80&&d[i+2]<d[i]*.8)gold++;return gold>=10;})()")).GetBoolean(), "Waveform attack markers");
        Check(true, "The waveform displays gold markers at the detected audio attacks");
        var saved = Projects.LoadFile(project.ProjectJsonPath);
        Check(saved.AudioOnsets.SequenceEqual(project.AudioOnsets) && saved.Notes.Any(n => n.CurvePoints.Count > 2) && saved.DetectedBpm == project.DetectedBpm, "Auto curves and their source-audio timing persist in the project");
        var otherLevels = JsonSerializer.Serialize(project.Levels.Where(kv => kv.Key != "Normal").ToDictionary(kv => kv.Key, kv => kv.Value));
        await Js("(() => {const b=document.getElementById('bpm'),o=document.getElementById('offset');b.value=137;b.dispatchEvent(new Event('change'));o.value=.2;o.dispatchEvent(new Event('change'));})()");
        await InvokeAsync("SaveChartFromEditorAsync"); await InvokeAsync("GenerateMidiChartAsync", false);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Curved refresh ready");
        Check(project.Bpm == 137 && project.OffsetSeconds == .2 && project.Notes.Any(n => n.CurvePoints.Count > 2) && JsonSerializer.Serialize(project.Levels.Where(kv => kv.Key != "Normal").ToDictionary(kv => kv.Key, kv => kv.Value)) == otherLevels, "Auto Chart refresh retains user BPM, timing offset and other edited levels while regenerating curves");
        Check(IsSingleLine(new TmbService().ReadTmb(project.ChartPath).Notes) && IsSingleLine(new MidiService().Read(Path.Combine(project.ProjectFolder, project.MidiFile!))), "Generated curve TMB segments and MIDI output have no simultaneous targets");
        await WaitUntil(() => Task.FromResult((double)Field("_audioDuration")! > 5.9), "Curved song duration for trimming");
        await InvokeAsync("TrimAudioAsync", 1d, 4d);
        Check(project.Notes.Any(n => n.CurvePoints.Count > 2) && project.AudioOnsets.All(t => t >= 0 && t < 3) && project.Levels.Values.All(IsSingleLine), "Audio trimming preserves generated curves and realigns attack markers for every difficulty");
        var zip = Path.Combine(folder, "curved-beat-song.zip"); await InvokeAsync("ExportZipToFileAsync", zip);
        using var package = ZipFile.OpenRead(zip);
        Check(package.Entries.Any(e => e.FullName == "Curved Beat Song.ogg") && package.Entries.Any(e => e.FullName == "Curved Beat Song.tmb") && project.Notes.Any(n => n.CurvePoints.Count > 2), "The named game package retains generated melody curves alongside the trimmed audio");
    }

    private static void WriteSignal(string path, double seconds, Func<double, double> pitch, Func<double, double> amplitude, bool noise = false)
    {
        const int rate = 22050; var samples = (int)(rate * seconds); double phase = 0;
        var random = new Random(7321);
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++)
        {
            var time = (double)i / rate; phase += 2 * Math.PI * 440 * Math.Pow(2, (pitch(time) - 69) / 12) / rate;
            var signal = noise ? random.NextDouble() * 2 - 1 : Math.Sin(phase);
            writer.Write((short)Math.Clamp(signal * amplitude(time) * 32767, short.MinValue, short.MaxValue));
        }
    }

    private static async Task RunMp3PipelineChecks()
    {
        Check(StudioCommands.Import.CanExecute(null, Window) && StudioCommands.Convert.CanExecute(null, Window) && StudioCommands.Build.CanExecute(null, Window) && StudioCommands.Export.CanExecute(null, Window), "MP3 import, conversion, build and export are available without creating a project first");
        var open = (Microsoft.Win32.OpenFileDialog)Invoke("CreateSongOpenDialog")!;
        Check(open.Filter.Contains("*.mp3") && open.Filter.Contains("*.msmproj"), "Open Song accepts MP3 audio as well as studio project files");
        var source = Path.Combine(TestRoot, "pipeline source.wav");
        var mp3 = Path.Combine(TestRoot, "Pipeline Audio.mp3");
        WriteWave(source);
        var ffmpeg = await new FfmpegService().EnsureInstalledAsync();
        var info = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", source, "-c:a", "libmp3lame", mp3 }) info.ArgumentList.Add(argument);
        using (var process = System.Diagnostics.Process.Start(info)!)
        {
            var errors = process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new Exception(await errors);
        }
        var original = File.ReadAllBytes(mp3);
        await InvokeAsync("ImportAudioFileAsync", mp3, false);
        var project = (SongProject)Field("_project")!;
        await WaitUntil(async () => (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Imported MP3 ready");
        Check(Path.GetExtension(project.ProjectJsonPath) == ".msmproj" && File.Exists(project.ProjectJsonPath) && File.Exists(Path.Combine(project.ProjectFolder, project.SourceAudioFile!)), "Selecting MP3 creates and saves its project and copies audio automatically");
        Check(Math.Abs((await Js("document.getElementById('audio').duration")).GetDouble() - 5) < .15 && !(await Js("document.getElementById('play').disabled")).GetBoolean(), "Imported MP3 plays in the real web editor with its duration");
        Check(project.Notes.Count > 0 && project.Levels.Count == 4 && project.Levels.Values.All(n => n.Count > 0) && (await Snapshot()).GetProperty("notes").GetArrayLength() > 0, "MP3 import automatically places notes and generates all four difficulty charts without Build or Export");
        Check(project.Levels.Values.All(IsSingleLine) && IsSingleLine(new MidiService().Read(Path.Combine(project.ProjectFolder, project.MidiFile!))) && IsSingleLine(new TmbService().ReadTmb(project.ChartPath).Notes), "Actual MP3 generation produces non-overlapping levels, MIDI and TMB for a single mouse");
        Check(((System.Windows.Controls.Button)Field("RefreshChartButton")!).Command == StudioCommands.Generate, "Refresh beside difficulty uses the audio chart-generation command");
        await Js("document.getElementById('play').click()");
        await WaitUntil(async () => (await Js("document.getElementById('audio').currentTime > .1")).GetBoolean(), "MP3 playback advances");
        await InvokeAsync("ExecuteEditorCommandAsync", "Stop");
        var zipPath = Path.Combine(TestRoot, "pipeline.zip");
        await InvokeAsync("ExportZipToFileAsync", zipPath);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Generated chart editor ready");
        Check(File.Exists(Path.Combine(project.ProjectFolder, "song.ogg")) && File.Exists(Path.Combine(project.ProjectFolder, "song.mid")) && File.Exists(project.ChartPath) && project.Notes.Count > 0, "One-click export runs MP3 to OGG to MIDI to automatically placed chart notes");
        Check(project.Notes.Any(n => n.Pitch == 69 && n.Length > 4.5), "Automatic note drafting detects the test recording's A4 pitch and sustained duration");
        // Force a delayed reload after the completion notice to cover slower hosted runners.
        Invoke("SendAudioToEditor", true);
        var exportedAudioKey = (string)Field("_audioKey")!;
        await WaitUntil(async () =>
        {
            var audio = await Js("({src: document.getElementById('audio').getAttribute('src'), ready: document.getElementById('audio').readyState >= 1})");
            return audio.GetProperty("src").GetString()?.EndsWith(exportedAudioKey, StringComparison.Ordinal) == true && audio.GetProperty("ready").GetBoolean();
        }, "Exported song audio ready");
        Check(((System.Windows.Controls.TextBlock)Field("StatusText")!).Text.Contains(zipPath), "Completed export keeps its chosen ZIP location visible after audio reloads");
        var midi = new MidiService().Read(Path.Combine(project.ProjectFolder, "song.mid"));
        Check(midi.Count > 0 && midi.Any(n => n.Pitch == 69) && File.ReadAllBytes(Path.Combine(project.ProjectFolder, "song.mid")).Take(4).SequenceEqual("MThd"u8.ToArray()), "The pipeline produces a real standard MIDI file with note events");
        using (var archive = ZipFile.OpenRead(zipPath))
            Check(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "Pipeline Audio.ogg", "Pipeline Audio.tmb" }), "One-click game ZIP uses the imported MP3 title for its OGG and TMB filenames");
        await InvokeAsync("ChangeDifficultyAsync", "Easy");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Easy level editor ready");
        await Gesture(250, 292, 250, 292);
        await InvokeAsync("SaveChartFromEditorAsync");
        var easyCount = project.Notes.Count;
        await InvokeAsync("ChangeDifficultyAsync", "Normal");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Normal level editor ready");
        Check(easyCount > project.Notes.Count && project.Levels["Easy"].Count == easyCount && project.Difficulty == "Normal", "Difficulty levels retain independent edited notes when switching");
        await UndoRedoProject("Undo", () => project.Difficulty == "Easy");
        Check(project.Notes.Count == easyCount, "Undo difficulty switch restores the selected level and its edited notes");
        await UndoRedoProject("Redo", () => project.Difficulty == "Normal");
        Check(project.Levels["Easy"].Count == easyCount && project.Notes.Count < easyCount, "Redo difficulty switch keeps independent level charts intact");
        var otherLevels = JsonSerializer.Serialize(project.Levels.Where(kv => kv.Key != "Normal").ToDictionary(kv => kv.Key, kv => kv.Value));
        await Gesture(250, 292, 250, 292);
        await InvokeAsync("SaveChartFromEditorAsync");
        Check(project.Notes.Count > 1, "Manual note is present before refreshing the selected difficulty");
        await InvokeAsync("GenerateMidiChartAsync", false);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Refreshed difficulty editor ready");
        Check(project.Notes.Count == 1 && project.Notes[0].Pitch == 69 && JsonSerializer.Serialize(project.Levels.Where(kv => kv.Key != "Normal").ToDictionary(kv => kv.Key, kv => kv.Value)) == otherLevels, "Refresh regenerates the selected level from audio while preserving every other difficulty");
        var autoChartNotes = JsonSerializer.Serialize(project.Notes);
        await UndoRedoProject("Undo", () => project.Notes.Count > 1);
        Check(JsonSerializer.Serialize(project.Levels.Where(kv => kv.Key != "Normal").ToDictionary(kv => kv.Key, kv => kv.Value)) == otherLevels, "Undo Auto Chart restores manual notes and leaves other levels unchanged");
        await UndoRedoProject("Redo", () => JsonSerializer.Serialize(project.Notes) == autoChartNotes);
        Check(JsonSerializer.Serialize(project.Notes) == autoChartNotes, "Redo Auto Chart restores the same generated notes without rerunning audio analysis");
        var copied = Projects.SaveCopyAs(project, Path.Combine(TestRoot, "Pipeline Copy.msmproj"));
        Check(File.Exists(Path.Combine(copied.ProjectFolder, "song.mid")) && copied.Levels["Easy"].Count == easyCount, "Save Project As keeps MIDI, audio and difficulty charts together");
        var audioBeforeTrim = project.SourceAudioFile;
        await WaitUntil(() => Task.FromResult((double)Field("_audioDuration")! > 4.9), "Host audio duration for trimming");
        var allLevelsBeforeTrim = JsonSerializer.Serialize(project.Levels);
        var notesBeforeTrimHistory = JsonSerializer.Serialize(project.Notes);
        var offsetBeforeTrimHistory = project.OffsetSeconds;
        var copiedMp3BeforeTrim = File.ReadAllBytes(Path.Combine(project.ProjectFolder, audioBeforeTrim!));
        await RunWaveformTrimChecks();
        await Js("document.getElementById('clipSelection').click(); true");
        await WaitUntil(async () => (await Js("document.getElementById('audio').readyState >= 1 && Math.abs(document.getElementById('audio').duration - 2) < .1")).GetBoolean(), "Trimmed playback duration");
        Check(File.Exists(Path.Combine(project.ProjectFolder, audioBeforeTrim!)) && project.Notes.All(n => n.Time >= 0 && n.Time + n.Length <= 2.01) && project.Notes.Any(n => n.Time == 0 && Math.Abs(n.Length - 2) < .02), "Audio trimming preserves original audio and crops and realigns chart notes");
        Check(File.ReadAllBytes(mp3).SequenceEqual(original) && File.ReadAllBytes(Path.Combine(project.ProjectFolder, audioBeforeTrim!)).SequenceEqual(copiedMp3BeforeTrim) && project.OggFile != audioBeforeTrim && Path.GetExtension(project.OggFile) == ".ogg", "Clipping from waveform keeps original and imported-copy MP3 bytes unchanged and creates a separate OGG");
        await WaitUntil(async () => (await Js("window.msmTrim.getRange().start === 0 && Math.abs(window.msmTrim.getRange().end - 2) < .1 && document.getElementById('clipSelection').hidden")).GetBoolean(), "Clipped waveform resets handles to new OGG duration");
        Check(project.Levels.Values.SelectMany(n => n).All(n => n.Time >= 0 && n.Time + n.Length <= 2.01) && project.MidiFile is null, "Trimming aligns all difficulty levels and invalidates old MIDI output");
        var trimmedFile = project.OggFile;
        var allLevelsAfterTrim = JsonSerializer.Serialize(project.Levels);
        await UndoRedoProject("Undo", () => project.SourceAudioFile == audioBeforeTrim);
        await WaitUntil(async () => (await Js("Math.abs(document.getElementById('audio').duration - 5) < .1")).GetBoolean(), "Undo trim restores original playback duration");
        Check(JsonSerializer.Serialize(project.Levels) == allLevelsBeforeTrim && JsonSerializer.Serialize(project.Notes) == notesBeforeTrimHistory && project.OffsetSeconds == offsetBeforeTrimHistory, "Undo trim restores original audio, full notes, timing and all difficulty levels together");
        Check(Projects.LoadFile(project.ProjectJsonPath, false).SourceAudioFile == audioBeforeTrim, "Undo trim autosaves restored project audio references");
        await UndoRedoProject("Redo", () => project.OggFile == trimmedFile);
        await WaitUntil(async () => (await Js("Math.abs(document.getElementById('audio').duration - 2) < .1")).GetBoolean(), "Redo trim restores clipped playback");
        Check(JsonSerializer.Serialize(project.Levels) == allLevelsAfterTrim && File.ReadAllBytes(mp3).SequenceEqual(original), "Redo trim restores the exact clipped chart and never rewrites original MP3 bytes");
        var trimmedNotes = JsonSerializer.Serialize(project.Notes);
        await Gesture(320, 358, 320, 358);
        await InvokeAsync("SaveChartFromEditorAsync");
        Check(JsonSerializer.Serialize(project.Notes) != trimmedNotes, "A note can be edited after a trim history boundary");
        await UndoRedoProject("Undo", () => JsonSerializer.Serialize(project.Notes) == trimmedNotes);
        await UndoRedoProject("Undo", () => project.SourceAudioFile == audioBeforeTrim);
        Check(JsonSerializer.Serialize(project.Notes) == notesBeforeTrimHistory, "Undo walks note edits then the preceding trim in chronological order");
        await UndoRedoProject("Redo", () => project.OggFile == trimmedFile);
        await UndoRedoProject("Redo", () => JsonSerializer.Serialize(project.Notes) != trimmedNotes);
        await UndoRedoProject("Undo", () => JsonSerializer.Serialize(project.Notes) == trimmedNotes);
        Check(project.OggFile == trimmedFile && JsonSerializer.Serialize(project.Levels) == allLevelsAfterTrim, "Redo walks the trim then later note edits; undoing that note keeps the trim");
        var notesBeforeExport = JsonSerializer.Serialize(project.Notes);
        await InvokeAsync("ExportZipToFileAsync", Path.Combine(TestRoot, "trimmed-pipeline.zip"));
        Check(JsonSerializer.Serialize(project.Notes) == notesBeforeExport && File.Exists(Path.Combine(project.ProjectFolder, "song.mid")), "Export rebuilds MIDI after trimming while preserving manual chart edits");
        await UndoRedoProject("Undo", () => project.SourceAudioFile == audioBeforeTrim);
        await Gesture(370, 358, 370, 358);
        await InvokeAsync("SaveChartFromEditorAsync");
        Check((await Js("document.getElementById('redo').disabled")).GetBoolean(), "A new note edit after undo clears future trim and note redo steps");
        await InvokeAsync("OpenSongFileAsync", mp3);
        var second = (SongProject)Field("_project")!;
        Check(second.ProjectJsonPath != project.ProjectJsonPath && File.Exists(project.ProjectJsonPath) && File.ReadAllBytes(mp3).SequenceEqual(original), "Opening the same MP3 creates a separate song without overwriting the original audio or project");
        Check(second.Notes.Count > 0 && second.Levels.Count == 4, "Every subsequent MP3 import also creates automatic notes");
        await InvokeAsync("ImportAudioFileAsync", mp3, false);
        var third = (SongProject)Field("_project")!;
        Check(third.ProjectJsonPath != second.ProjectJsonPath && third.Notes.Count > 0 && Projects.LoadFile(second.ProjectJsonPath).Notes.Count == second.Notes.Count, "Import into an occupied song creates its own chart and preserves the previous song");
        second = third;
        var invalid = Path.Combine(TestRoot, "not-a-song.txt"); File.WriteAllText(invalid, "test");
        try { await InvokeAsync("ImportAudioFileAsync", invalid, false); throw new Exception("Invalid audio extension was accepted"); }
        catch (InvalidDataException) { }
        Check(ReferenceEquals(second, Field("_project")), "Invalid audio selections leave the current song intact");
        var crop = AudioDraftService.CropNotes([new() { Time = 1, Length = 2, Pitch = 60, EndPitch = 72 }], .5, 2, 3);
        Check(crop.Count == 1 && crop[0].Time == 0 && crop[0].Length == 1 && crop[0].Pitch == 63 && crop[0].EndPitch == 69, "Trimming accounts for timing offset and preserves the retained part of slide notes");
    }

    private static async Task RunStudioChecks(SongProject audioProject)
    {
        var studioRoot = Path.Combine(TestRoot, "studio-files");
        Directory.CreateDirectory(studioRoot);
        var file = Path.Combine(studioRoot, "Portable Song.msmproj");
        var portable = Projects.SaveCopyAs(audioProject, file);
        Check(File.Exists(file) && portable.AssetsFolder is not null && File.Exists(Path.Combine(portable.ProjectFolder, "source.wav")) && File.Exists(Path.Combine(portable.ProjectFolder, "song.ogg")), "Save Project As copies the chart and both audio files into companion assets");
        portable.Notes[0].Pitch = 65;
        Check(audioProject.Notes[0].Pitch != 65, "Save Project As creates an independent editable project");
        Projects.Save(portable);
        var restored = Projects.LoadFile(file);
        Check(restored.Notes[0].Pitch == 65 && restored.ProjectJsonPath == file, "A named .msmproj file reopens with its edited notes and audio");
        var relocated = Path.Combine(TestRoot, "relocated");
        Directory.CreateDirectory(relocated);
        File.Copy(file, Path.Combine(relocated, Path.GetFileName(file)));
        var relocatedAssets = Path.Combine(relocated, portable.AssetsFolder!);
        Directory.CreateDirectory(relocatedAssets);
        foreach (var asset in Directory.GetFiles(portable.ProjectFolder)) File.Copy(asset, Path.Combine(relocatedAssets, Path.GetFileName(asset)));
        var moved = Projects.LoadFile(Path.Combine(relocated, Path.GetFileName(file)));
        Check(moved.ProjectFolder == relocatedAssets && File.Exists(Path.Combine(moved.ProjectFolder, moved.SourceAudioFile!)), "Projects resolve companion audio correctly after the file and assets folder are moved together");
        var fresh = Projects.CreateAtFile(Path.Combine(studioRoot, "New Studio Song.msmproj"));
        Check(File.Exists(fresh.ProjectJsonPath) && Directory.Exists(fresh.ProjectFolder) && fresh.Name == "New Studio Song", "New Song creates a named studio file and assets folder");
        var changedWorking = Path.Combine(TestRoot, "chosen-working-folder");
        Projects.SetWorkingFolder(changedWorking);
        var reopenedService = new ProjectService(settingsFile: Path.Combine(TestRoot, "studio-settings.json"));
        Check(reopenedService.ProjectsRoot == changedWorking && reopenedService.RecentProjectFiles().Contains(file), "Working folder and recent projects outside that folder persist across restarts");

        Invoke("OpenProject", restored);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Named project editor ready");
        var dialog = (Microsoft.Win32.SaveFileDialog)Invoke("CreateChartSaveDialog")!;
        Check(dialog.Title == "Save Chart As" && dialog.DefaultExt.TrimStart('.') == "tmb" && dialog.AddExtension && dialog.OverwritePrompt, "Save Chart configures the native Save As dialog with extension and overwrite confirmation");
        await InvokeAsync("ExecuteEditorCommandAsync", "SelectAll");
        await WaitUntil(() => Task.FromResult(StudioCommands.Delete.CanExecute(null, Window)), "Native edit menu selection ready");
        await InvokeAsync("ExecuteEditorCommandAsync", "Delete");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "Native Edit commands select and delete notes");
        await InvokeAsync("ExecuteEditorCommandAsync", "Undo");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "Native Undo restores deleted notes");
        var chartFile = Path.Combine(studioRoot, "Chosen Chart.tmb");
        await InvokeAsync("SaveChartToFileAsync", chartFile);
        var chart = new TmbService().ReadTmb(chartFile);
        Check(File.Exists(chartFile) && chart.Notes.Count == 1 && chart.Bpm == 144 && chart.Notes[0].Pitch == 65, "Save Chart writes the latest editor notes to the chosen file and reopens them");
        var midiDialog = (Microsoft.Win32.SaveFileDialog)Invoke("CreateMidiTmbSaveDialog")!;
        Check(midiDialog.Filter.Contains("*.mid") && midiDialog.Filter.Contains("*.tmb") && midiDialog.AddExtension && midiDialog.OverwritePrompt, "MIDI/TMB Save As offers both formats and overwrite confirmation");
        Check(((System.Windows.Controls.Button)Field("SaveMidiTmbButton")!).Command == StudioCommands.SaveMidiTmb, "Notes/MIDI replacement saves files instead of regenerating notes");
        var notesBeforeSave = JsonSerializer.Serialize((await Snapshot()).GetProperty("notes"));
        var selectedMidi = Path.Combine(studioRoot, "Selected Notes.mid");
        await InvokeAsync("SaveMidiTmbToFileAsync", selectedMidi);
        Check(new MidiService().Read(selectedMidi).Single().Pitch == 65, "Save MIDI exports the current editor notes to the chosen filename");
        await InvokeAsync("SaveMidiTmbToFileAsync", chartFile);
        Check(new TmbService().ReadTmb(chartFile).Notes.Single().Pitch == 65 && JsonSerializer.Serialize((await Snapshot()).GetProperty("notes")) == notesBeforeSave, "Save TMB preserves existing notes without running Auto Chart");
        dialog = (Microsoft.Win32.SaveFileDialog)Invoke("CreateChartSaveDialog")!;
        Check(dialog.FileName == "Chosen Chart.tmb" && dialog.InitialDirectory == studioRoot && Projects.LoadFile(file).LastChartSavePath == chartFile, "Chart Save As remembers the last filename and location");
        var chartBytes = File.ReadAllBytes(chartFile);
        chart.Bpm = double.NaN;
        try { new TmbService().WriteTmb(chart, chartFile); throw new Exception("Invalid chart was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(chartFile).SequenceEqual(chartBytes), "Invalid chart saves preserve the existing chosen file");
        var foreignChart = Path.Combine(studioRoot, "foreign.tmb");
        File.WriteAllText(foreignChart, "{\"schema\":\"other-format\"}");
        try { new TmbService().ReadTmb(foreignChart); throw new Exception("Unsupported schema was accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(chartFile).SequenceEqual(chartBytes), "Unsupported chart formats are rejected without changing the active chart");
        var zipPath = Path.Combine(studioRoot, "Chosen Export.zip");
        var exporter = new ExportService();
        exporter.ExportZip(restored, zipPath); exporter.ExportZip(restored, zipPath);
        using var archive = ZipFile.OpenRead(zipPath);
        Check(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "Portable Song.ogg", "Portable Song.tmb" }), "ZIP export overwrites the chosen archive with correctly named audio and chart");
    }

    private static async Task UndoRedoProject(string command, Func<bool> restored)
    {
        await WaitUntil(async () => (await Js("!window.msmEditor.getPreviewState().busy")).GetBoolean(), "History operation ready");
        await InvokeAsync("ExecuteEditorCommandAsync", command);
        await WaitUntil(async () => {
            if (!(await Js("!window.msmEditor.getPreviewState().busy")).GetBoolean()) return false;
            await InvokeAsync("SaveChartFromEditorAsync");
            return restored();
        }, command + " restores project/editor state");
    }

    private static async Task RunWaveformTrimChecks()
    {
        await WaitUntil(async () => (await Js("!document.getElementById('trimStartHandle').hidden && !document.getElementById('trimEndHandle').hidden")).GetBoolean(), "Waveform trim handles ready");
        var before = await Snapshot();
        await Js(@"(() => {
            const width = document.getElementById('wave').getBoundingClientRect().width;
            const duration = window.msmEditor.getPreviewState().duration;
            for (const [id, seconds] of [['trimStartHandle', 1], ['trimEndHandle', -2]]) {
                const h = document.getElementById(id), r = h.getBoundingClientRect(), x = r.left + r.width / 2;
                h.dispatchEvent(new PointerEvent('pointerdown', {bubbles:true, pointerId:91, button:0, clientX:x}));
                h.dispatchEvent(new PointerEvent('pointermove', {bubbles:true, pointerId:91, buttons:1, clientX:x + width * seconds / duration}));
                h.dispatchEvent(new PointerEvent('pointerup', {bubbles:true, pointerId:91, button:0}));
            }
            return true;
        })()");
        await WaitUntil(() => Task.FromResult(Math.Abs(double.Parse(((System.Windows.Controls.TextBox)Field("TrimStartText")!).Text) - 1) < .001 && Math.Abs(double.Parse(((System.Windows.Controls.TextBox)Field("TrimEndText")!).Text) - 3) < .1), "Dragged handles synchronize native trim fields");
        Check((await Js("!document.getElementById('clipSelection').hidden && parseFloat(document.getElementById('trimShadeStart').style.width) > 0 && parseFloat(document.getElementById('trimShadeEnd').style.width) > 0")).GetBoolean(), "Dragging both waveform ends shades excluded audio and offers Clip selection");
        await Js("document.getElementById('trimStartHandle').dispatchEvent(new KeyboardEvent('keydown', {key:'End', bubbles:true})); true");
        Check((await Js("window.msmTrim.getRange().end - window.msmTrim.getRange().start >= .029")).GetBoolean(), "Trim handles cannot cross or select an empty clip");
        ((System.Windows.Controls.TextBox)Field("TrimStartText")!).Text = "1";
        ((System.Windows.Controls.TextBox)Field("TrimEndText")!).Text = "3";
        await WaitUntil(async () => (await Js("Math.abs(window.msmTrim.getRange().start - 1) < .001 && Math.abs(window.msmTrim.getRange().end - 3) < .001")).GetBoolean(), "Typed times synchronize waveform handles");
        Check((await Snapshot()).GetProperty("notes").GetRawText() == before.GetProperty("notes").GetRawText(), "Selecting a waveform trim range leaves chart notes unchanged until clipping");
    }

    private static async Task RunFilesAndRecentChecks()
    {
        var folder = Path.Combine(TestRoot, "files-and-recents"); Directory.CreateDirectory(folder);
        var active = Projects.CreateAtFile(Path.Combine(folder, "File Menu Song.msmproj"));
        active.SourceAudioFile = "source.wav"; WriteWave(Path.Combine(active.ProjectFolder, active.SourceAudioFile));
        active.Notes = [new() { Time = .5, Length = .75, Pitch = 60, EndPitch = 60 }];
        Projects.Save(active); Invoke("OpenProject", active);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "File menu fixture ready");
        var oggButton = (System.Windows.Controls.Button)Field("SaveOggAsButton")!;
        Check(oggButton.Command == StudioCommands.SaveOggAs && StudioCommands.SaveOggAs.CanExecute(null, Window), "Save OGG As is available as a dedicated studio command and button");
        var dialog = (Microsoft.Win32.SaveFileDialog)Invoke("CreateOggSaveDialog")!;
        Check(dialog.Title == "Save OGG As" && dialog.FileName == "File Menu Song.ogg" && dialog.DefaultExt.TrimStart('.') == "ogg" && dialog.AddExtension && dialog.OverwritePrompt, "OGG Save As defaults to the song title and confirms overwrites");
        var destination = Path.Combine(folder, "My Saved Audio.ogg");
        await InvokeAsync("SaveOggToFileAsync", destination);
        var audio = File.ReadAllBytes(destination);
        Check(audio.Take(4).SequenceEqual("OggS"u8.ToArray()) && audio.SequenceEqual(File.ReadAllBytes(Path.Combine(active.ProjectFolder, active.OggFile!))), "OGG Save As converts unbuilt audio and saves the exact converted audio at the chosen location");
        Check(active.OggFile == "song.ogg" && Projects.LoadFile(active.ProjectJsonPath).LastOggSavePath == destination && active.Notes.Count == 1, "Saving OGG preserves linked project audio and chart and persists the chosen destination");
        dialog = (Microsoft.Win32.SaveFileDialog)Invoke("CreateOggSaveDialog")!;
        Check(dialog.FileName == "My Saved Audio.ogg" && dialog.InitialDirectory == folder, "OGG Save As remembers the last filename and folder");
        File.WriteAllText(destination, "old audio"); await InvokeAsync("SaveOggToFileAsync", destination);
        Check(File.ReadAllBytes(destination).SequenceEqual(audio), "OGG Save As safely replaces a chosen existing audio file");
        var invalid = Path.Combine(folder, "untouched.mp3"); File.WriteAllText(invalid, "original");
        try { await InvokeAsync("SaveOggToFileAsync", invalid); throw new Exception("Invalid OGG extension accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(invalid) == "original" && active.LastOggSavePath == destination, "Invalid OGG destinations are rejected without overwriting other audio");
        var exporter = new ExportService();
        var internalAudio = Path.Combine(active.ProjectFolder, active.OggFile!);
        exporter.SaveOgg(active, internalAudio);
        Check(File.ReadAllBytes(internalAudio).SequenceEqual(audio), "Saving OGG onto its working audio path keeps that audio intact");
        var missing = new SongProject { ProjectFolder = active.ProjectFolder, OggFile = "missing.ogg" };
        try { exporter.SaveOgg(missing, destination); throw new Exception("Missing audio accepted"); }
        catch (FileNotFoundException) { }
        Check(File.ReadAllBytes(destination).SequenceEqual(audio) && Directory.GetFiles(folder, "*.tmp").Length == 0, "Failed OGG saves preserve an existing destination and leave no temporary files");

        var inactive = Projects.SaveCopyAs(active, Path.Combine(folder, "Inactive Project.msmproj"));
        Check(inactive.LastOggSavePath is null, "A separate project copy starts with its own OGG Save As filename");
        Invoke("RefreshRecentProjects");
        var recents = (System.Windows.Controls.ListBox)Field("RecentProjectsList")!;
        object Recent(string file) => recents.Items.Cast<object>().Single(i => (string)i.GetType().GetProperty("FilePath")!.GetValue(i)! == file);
        string DisplayName(string file) => (string)Recent(file).GetType().GetProperty("Name")!.GetValue(Recent(file))!;
        recents.ScrollIntoView(Recent(inactive.ProjectJsonPath)); recents.UpdateLayout();
        var container = (System.Windows.Controls.ListBoxItem)recents.ItemContainerGenerator.ContainerFromItem(Recent(inactive.ProjectJsonPath));
        var right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right)
        { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = container };
        var selectedBefore = recents.SelectedItem;
        recents.RaiseEvent(right);
        var contextTarget = Field("_recentContextItem");
        Check(right.Handled && ReferenceEquals(Field("_project"), active) && ReferenceEquals(recents.SelectedItem, selectedBefore) &&
            contextTarget is not null && (string)contextTarget.GetType().GetProperty("FilePath")!.GetValue(contextTarget)! == inactive.ProjectJsonPath,
            "The right-click handler targets the clicked recent project without opening it or changing the edited song");
        var menu = (System.Windows.Controls.ContextMenu)Field("RecentProjectsContextMenu")!;
        Check(menu.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.Tag?.ToString()).Order().SequenceEqual(new[] { "ChartInfo", "Folder", "ImportGame", "Open", "Refresh", "Remove", "RemoveGame", "Rename", "Save" }), "Recent-project context menu includes project actions and game import/removal");
        var noteJson = JsonSerializer.Serialize(inactive.Notes); var assetFiles = Directory.GetFiles(inactive.ProjectFolder).Order().ToArray();
        await InvokeAsync("RenameRecentProjectAsync", inactive.ProjectJsonPath, "Renamed Melody");
        var renamed = Projects.LoadFile(inactive.ProjectJsonPath);
        Check(renamed.Name == "Renamed Melody" && DisplayName(inactive.ProjectJsonPath) == "Renamed Melody" && ReferenceEquals(Field("_project"), active), "Renaming an inactive recent song updates its saved title and menu while leaving the active editor open");
        Check(JsonSerializer.Serialize(renamed.Notes) == noteJson && Directory.GetFiles(renamed.ProjectFolder).Order().SequenceEqual(assetFiles) && File.ReadAllBytes(Path.Combine(renamed.ProjectFolder, renamed.OggFile!)).SequenceEqual(audio), "Recent-project rename preserves notes, audio and asset paths");
        using (var archive = ZipFile.OpenRead(exporter.ExportZip(renamed, Path.Combine(folder, "arbitrary-archive.zip"))))
        {
            Check(archive.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "Renamed Melody.ogg", "Renamed Melody.tmb" }), "Both packaged filenames follow the renamed title independently of the ZIP or project filename");
            using var chartStream = archive.GetEntry("Renamed Melody.tmb")!.Open();
            using var chart = JsonDocument.Parse(chartStream);
            Check(chart.RootElement.GetProperty("name").GetString() == "Renamed Melody", "Packaged TMB metadata matches the renamed song title");
        }
        var saveMenu = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(i => i.Tag?.ToString() == "Save");
        saveMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        await WaitUntil(() => Task.FromResult(!(bool)Field("_busy")!), "Context menu save complete");
        Check(ReferenceEquals(Field("_project"), active) && Projects.LoadFile(inactive.ProjectJsonPath).Name == "Renamed Melody" && ((System.Windows.Controls.TextBlock)Field("StatusText")!).Text.Contains(inactive.ProjectJsonPath), "The context-menu Save action saves its right-clicked project without switching the editor");
        Invoke("RemoveRecentProject", inactive.ProjectJsonPath); Projects.Save(renamed); Invoke("RefreshRecentProjects");
        Check(!Projects.RecentProjectFiles(100).Contains(inactive.ProjectJsonPath) && File.Exists(inactive.ProjectJsonPath) && File.Exists(Path.Combine(renamed.ProjectFolder, renamed.OggFile!)), "Removing a recent entry keeps project files and remains hidden after save and list refresh");
        var restarted = new ProjectService(Projects.ProjectsRoot, Path.Combine(TestRoot, "studio-settings.json"));
        Check(!restarted.RecentProjectFiles(100).Contains(inactive.ProjectJsonPath) && restarted.ProjectName(inactive.ProjectJsonPath) == "Renamed Melody", "Removed entries stay hidden after restarting even when the working-folder scan finds them");
        await InvokeAsync("OpenSongFileAsync", inactive.ProjectJsonPath);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Removed project explicitly reopened");
        Check(Projects.RecentProjectFiles(100).Contains(inactive.ProjectJsonPath) && ((SongProject)Field("_project")!).Notes.Count == 1, "Explicitly opening a removed project restores it to history with its audio and notes");
        await Js("(() => {const b=document.getElementById('bpm');b.value=133;b.dispatchEvent(new Event('change'));})()");
        await InvokeAsync("RenameRecentProjectAsync", inactive.ProjectJsonPath, "Edited Song");
        Check(Projects.LoadFile(inactive.ProjectJsonPath).Bpm == 133 && ((System.Windows.Controls.TextBlock)Field("ProjectTitle")!).Text == "Edited Song" && DisplayName(inactive.ProjectJsonPath) == "Edited Song", "Renaming the current song saves pending editor changes and updates both title and recent entry");
        Invoke("RemoveRecentProject", inactive.ProjectJsonPath); await InvokeAsync("SaveChartFromEditorAsync"); Invoke("RefreshRecentProjects");
        Check(IsCurrent(inactive.ProjectJsonPath) && !Projects.RecentProjectFiles(100).Contains(inactive.ProjectJsonPath) && Projects.LastProjectFile != inactive.ProjectJsonPath, "Removing the current song keeps editing available and prevents autosave or startup from undoing the removal");
        await InvokeAsync("ChangeDifficultyAsync", "Hard");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Hidden project's changed difficulty ready");
        Check(!Projects.RecentProjectFiles(100).Contains(inactive.ProjectJsonPath), "Changing difficulty reloads the editor without undoing a removed history entry");
        var editedName = ((SongProject)Field("_project")!).Name;
        try { await InvokeAsync("RenameRecentProjectAsync", inactive.ProjectJsonPath, "  "); throw new Exception("Blank rename accepted"); }
        catch (InvalidDataException) { }
        Check(((SongProject)Field("_project")!).Name == editedName, "A blank rename preserves the existing song name");
        var safe = Projects.LoadFile(inactive.ProjectJsonPath); safe.Name = "A/B: C?.. "; new TmbService().WriteTmb(safe);
        using (var zip = ZipFile.OpenRead(exporter.ExportZip(safe, Path.Combine(folder, "sanitized.zip"))))
            Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "A_B_ C_.ogg", "A_B_ C_.tmb" }), "Invalid filename characters use the same safe basename for both root ZIP entries");
        Check(ProjectService.SafeName("...") == "Untitled Song" && ProjectService.SafeName("CON") == "_CON" && ProjectService.SafeName("Café Night") == "Café Night", "Package naming handles empty and reserved names while preserving valid Unicode titles");
        static bool IsCurrent(string file) => ((SongProject)Field("_project")!).ProjectJsonPath == file;
    }

    private static async Task RunGameIntegrationChecks()
    {
        Directory.CreateDirectory(Game.SongsFolder);
        var save = Path.Combine(Game.DataFolder, "save"); Directory.CreateDirectory(save);
        var player = Path.Combine(save, "player.json"); File.WriteAllText(player, "{\"progress\":\"keep unchanged\"}");
        var fixture = Projects.CreateAtFile(Path.Combine(TestRoot, "Studio Game Song.msmproj"));
        fixture.SourceAudioFile = "source.wav"; WriteWave(Path.Combine(fixture.ProjectFolder, fixture.SourceAudioFile));
        fixture.Notes = [new() { Time = .5, Length = 1, Pitch = 60, EndPitch = 65, CurvePoints = [new() { Position = 0, Pitch = 60 }, new() { Position = .5, Pitch = 69 }, new() { Position = 1, Pitch = 65 }] }];
        Projects.Save(fixture); Invoke("OpenProject", fixture);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Game integration project ready");
        Check(Game.IsReady && Game.GetSongs().Count == 0 && ((System.Windows.Controls.TextBlock)Field("GameStatusText")!).Text == "Not in game", "An empty game Songs folder reports that the studio song has not been added");
        Check(((System.Windows.Controls.Button)Field("ImportGameButton")!).Command == StudioCommands.ImportGame && StudioCommands.ImportGame.CanExecute(null, Window), "Import into Game has a dedicated enabled button beside ZIP export");
        await InvokeAsync("ImportIntoGameAsync");
        var installed = Game.GetSongs().Single();
        Check(Path.GetFileName(installed.Folder) == "Studio Game Song" && Path.GetFileName(installed.ChartFile) == "Studio Game Song.tmb" && Path.GetFileName(installed.AudioFile) == "Studio Game Song.ogg", "One-click game import creates matching folder, audio and chart names in the game's observed layout");
        Check(installed.HasAudio && installed.Readable && File.ReadAllBytes(installed.AudioFile).Take(4).SequenceEqual("OggS"u8.ToArray()) && new TmbService().ReadTmb(installed.ChartFile).Notes.Count == 2, "Game import builds real OGG and exports the edited curve as valid TMB slides");
        Check(((System.Windows.Controls.TextBlock)Field("GameStatusText")!).Text == "In game · Normal · Up to date" && ((System.Windows.Controls.Button)Field("ImportGameButton")!).Content.ToString() == "Update in Game" && StudioCommands.RemoveGame.CanExecute(null, Window), "Import updates the current song badge, sync status, update button and removal command");
        var recents = (System.Windows.Controls.ListBox)Field("RecentProjectsList")!;
        var recent = recents.Items.Cast<object>().Single(i => (string)i.GetType().GetProperty("FilePath")!.GetValue(i)! == fixture.ProjectJsonPath);
        Check(((string)recent.GetType().GetProperty("GameStatus")!.GetValue(recent)!).Contains("In game · Normal"), "Recent projects display the installed difficulty status");
        var originalAudio = File.ReadAllBytes(installed.AudioFile); var oldChart = File.ReadAllBytes(installed.ChartFile);
        await Js("document.getElementById('noteLength').value=1.4;window.msmEditor.selectAll();document.getElementById('applyLength').click()");
        await WaitUntil(() => Task.FromResult(((System.Windows.Controls.TextBlock)Field("GameStatusText")!).Text.Contains("Out of sync")), "Pending game-sync status");
        Invoke("RefreshRecentProjects");
        var changedRecent = recents.Items.Cast<object>().Single(i => (string)i.GetType().GetProperty("FilePath")!.GetValue(i)! == fixture.ProjectJsonPath);
        Check(((string)changedRecent.GetType().GetProperty("GameStatus")!.GetValue(changedRecent)!).Contains("Out of sync"), "Pending chart edits mark both editor and recent-project game badges out of sync");
        await InvokeAsync("ImportIntoGameAsync");
        var updated = Game.GetSongs().Single();
        Check(new TmbService().ReadTmb(updated.ChartFile).Notes.Sum(n => n.Length) > 1.39 && fixture.Notes.Single().Length == 1.4, "Updating an installed song imports the latest unsaved editor changes without generating replacement notes");
        var backups = Path.Combine(Game.DataFolder, ".MSMStudio", "backups");
        Check(Directory.GetDirectories(backups).Length == 1 && File.ReadAllBytes(Path.Combine(Directory.GetDirectories(backups).Single(), "Studio Game Song.tmb")).SequenceEqual(oldChart), "Updating keeps a complete backup of the previous game chart");
        Check(Game.GetSongs().Count == 1 && File.ReadAllBytes(updated.AudioFile).SequenceEqual(originalAudio), "Repeated import replaces the same level without duplicate songs or altered audio");
        await InvokeAsync("RenameRecentProjectAsync", fixture.ProjectJsonPath, "Renamed Game Song");
        Check(((System.Windows.Controls.TextBlock)Field("GameStatusText")!).Text == "In game · Normal · Out of sync (chart or info changed)", "Stable chart identifiers find a renamed installed song and report its changed metadata as out of sync");
        await InvokeAsync("ImportIntoGameAsync");
        var normal = Game.GetSongs().Single();
        Check(Path.GetFileName(normal.Folder) == "Renamed Game Song" && !Directory.Exists(installed.Folder) && Path.GetFileName(normal.AudioFile) == "Renamed Game Song.ogg", "Updating a renamed song retires the old game folder and installs matching new filenames");
        await InvokeAsync("ChangeDifficultyAsync", "Hard");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Hard game level ready");
        Check(((System.Windows.Controls.TextBlock)Field("GameStatusText")!).Text.StartsWith("Hard not in game") && !StudioCommands.RemoveGame.CanExecute(null, Window), "The selected level reports not imported when only another difficulty is installed");
        await InvokeAsync("ImportIntoGameAsync");
        var hard = Game.GetSongs().Single(s => s.Level == "Hard");
        Check(Game.GetSongs().Count == 2 && Path.GetFileName(hard.Folder) == "Renamed Game Song (Hard)" && Path.GetFileName(hard.ChartFile) == "Renamed Game Song (Hard).tmb" && File.Exists(normal.ChartFile), "Importing another difficulty keeps both levels with matching folder and file names");
        var hardBytes = File.ReadAllBytes(hard.ChartFile);
        await InvokeAsync("ChangeDifficultyAsync", "Normal");
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Normal game level ready");
        await InvokeAsync("ImportIntoGameAsync");
        Check(Game.GetSongs().Count == 2 && File.ReadAllBytes(hard.ChartFile).SequenceEqual(hardBytes), "Updating one level leaves the other installed difficulty untouched");
        var projectBytes = File.ReadAllBytes(fixture.ProjectJsonPath);
        var removed = Game.RemoveSong(hard); Invoke("RefreshRecentProjects");
        Check(!Directory.Exists(hard.Folder) && File.Exists(Path.Combine(removed, Path.GetFileName(hard.ChartFile))) && Game.GetSongs().Count == 1, "Removing a game song moves it out of Songs into a recoverable backup");
        Check(File.ReadAllBytes(fixture.ProjectJsonPath).SequenceEqual(projectBytes) && File.Exists(Path.Combine(fixture.ProjectFolder, fixture.SourceAudioFile!)), "Game-song removal preserves the studio project and its source audio");
        var deletedHard = Game.GetDeletedSongs().Single(s => s.Folder == removed);
        Check(deletedHard.OriginalFolder == Path.GetFileName(hard.Folder) && deletedHard.Reason == "Deleted song" && Game.GetDeletedSongs().Any(s => s.Reason == "Previous version"), "Deleted songs retain the original folder and are distinguished from backed-up previous versions");
        Directory.CreateDirectory(hard.Folder); var conflict = Path.Combine(hard.Folder,"keep.txt"); File.WriteAllText(conflict,"keep");
        try { Game.RestoreSong(deletedHard); throw new Exception("Restore overwrote an occupied folder"); } catch(IOException) { }
        Check(File.ReadAllText(conflict)=="keep" && Directory.Exists(removed), "Restore refuses an occupied game folder and preserves both versions");
        File.Delete(conflict); Directory.Delete(hard.Folder);
        Check(Game.RestoreSong(deletedHard)==hard.Folder && Game.GetSongs().Count==2 && File.ReadAllBytes(hard.ChartFile).SequenceEqual(hardBytes) && !Directory.Exists(removed), "Restore returns the exact archived chart and audio to the original game folder");
        try { Game.RestoreSong(deletedHard); throw new Exception("Stale backup restored twice"); } catch(Exception ex) when(ex is DirectoryNotFoundException or InvalidOperationException) { }
        var removedAgain=Game.RemoveSong(Game.GetSongs().Single(s=>s.Folder==hard.Folder));
        Game.DeleteBackupPermanently(Game.GetDeletedSongs().Single(s=>s.Folder==removedAgain));
        Check(!Directory.Exists(removedAgain) && File.Exists(normal.ChartFile) && File.ReadAllBytes(fixture.ProjectJsonPath).SequenceEqual(projectBytes), "Permanent deletion removes only the chosen backup while preserving installed songs and studio projects");
        try { Game.DeleteBackupPermanently(deletedHard with {Folder=normal.Folder}); throw new Exception("Installed folder accepted as backup"); } catch(InvalidDataException) { }
        Check(File.Exists(normal.ChartFile), "Permanent deletion rejects folders outside the backup root");
        try { Game.RemoveSong(hard); throw new Exception("Stale installed record removed twice"); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or InvalidOperationException) { }
        Check(File.Exists(normal.ChartFile), "A stale removal request cannot affect another game song");
        var outside = Path.Combine(TestRoot, "outside-game"); Directory.CreateDirectory(outside); File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        try { Game.RemoveSong(normal with { Folder = outside }); throw new Exception("Outside folder accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "keep" && File.Exists(normal.ChartFile), "Removal rejects paths outside the selected game's Songs folder");
        fixture.OggFile = "bad.ogg"; File.WriteAllText(Path.Combine(fixture.ProjectFolder, fixture.OggFile), "bad audio");
        var beforeFailure = File.ReadAllBytes(normal.ChartFile);
        try { Game.ImportSong(fixture); throw new Exception("Invalid game audio accepted"); }
        catch (InvalidDataException) { }
        Check(File.ReadAllBytes(normal.ChartFile).SequenceEqual(beforeFailure) && Directory.GetDirectories(Path.Combine(Game.DataFolder, ".MSMStudio", "staging")).Length == 0, "Invalid audio cannot replace an existing game song and leaves no partial staging folder");
        fixture.OggFile = "song.ogg";
        var other = Projects.SaveCopyAs(fixture, Path.Combine(TestRoot, "Independent Project.msmproj")); other.Name = fixture.Name; new TmbService().WriteTmb(other);
        var independent = Game.ImportSong(other);
        Check(independent != normal.Folder && Game.GetSongs().Count == 2 && File.ReadAllBytes(normal.ChartFile).SequenceEqual(beforeFailure), "Different studio projects with the same title get separate game folders instead of overwriting each other");
        var legacyFolder = Path.Combine(Game.SongsFolder, "DAsh"); Directory.CreateDirectory(legacyFolder);
        File.Copy(normal.AudioFile, Path.Combine(legacyFolder, "DAsh.ogg"));
        File.WriteAllText(Path.Combine(legacyFolder, "DAsh.tmb"), "{\"name\":\"Geometry Dash\",\"trackRef\":\"1\",\"difficulty\":3,\"tempo\":120,\"timesig\":4,\"notes\":[[0,1,-55,0,-55]]}");
        var legacy = Game.GetSongs().Single(s => s.Folder == legacyFolder);
        Check(legacy.Name == "Geometry Dash" && legacy.Level == "Easy" && legacy.HasAudio, "The game-song list reads the observed DAsh layout and legacy numeric chart identifiers");
        var legacyProject = new SongProject { Name = " geometry dash ", Difficulty = "Easy" };
        Check(GameSongService.FindLevel(legacyProject, Game.GetSongs())?.Folder == legacyFolder, "Previously imported legacy songs are recognized by normalized title and difficulty");
        var legacyBackup = Game.RemoveSong(legacy);
        Check(Directory.Exists(legacyBackup) && !Directory.Exists(legacyFolder) && Game.GetSongs().Count == 2, "The game-song manager can archive older imports that have no studio project");
        File.Delete(Path.Combine(legacyBackup,".msmstudio-backup.json"));
        var olderBackup=Game.GetDeletedSongs().Single(s=>s.Folder==legacyBackup);
        Check(olderBackup.OriginalFolder=="DAsh" && olderBackup.Reason=="Older backup", "Backups created by earlier studio versions remain listed with their original folder names");
        Game.RestoreSong(olderBackup);
        var reopened=Game.OpenAsProject(Game.GetSongs().Single(s=>s.Folder==legacyFolder),Projects);
        Check(reopened.Name=="Geometry Dash" && reopened.Difficulty=="Easy" && reopened.Notes.Count==1 && reopened.TrackRef=="1" && File.ReadAllBytes(Path.Combine(reopened.ProjectFolder,reopened.OggFile!)).SequenceEqual(originalAudio), "A game-only song opens as an independent editable project with original title, identity, difficulty, notes and audio");
        Check(Projects.RecentProjectFiles(100).Contains(reopened.ProjectJsonPath) && File.Exists(Path.Combine(legacyFolder,"DAsh.tmb")) && reopened.ProjectFolder!=legacyFolder, "Open as Project adds a local recent project without editing the game's installed files");
        Game.RemoveSong(Game.GetSongs().Single(s=>s.Folder==legacyFolder));
        var missing = Projects.SaveCopyAs(fixture, Path.Combine(TestRoot, "Missing Audio.msmproj")); new TmbService().WriteTmb(missing);
        var missingFolder = Path.Combine(Game.SongsFolder, "Missing Audio"); Directory.CreateDirectory(missingFolder);
        File.Copy(missing.ChartPath, Path.Combine(missingFolder, "Missing Audio.tmb"));
        Check(GameSongService.FindLevel(missing, Game.GetSongs()) is { HasAudio: false }, "An installed chart with missing OGG is detected as incomplete");
        Game.ImportSong(missing);
        Check(GameSongService.FindLevel(missing, Game.GetSongs()) is { HasAudio: true }, "Import repairs an incomplete installed song while retaining its previous files in backup");
        var malformedFolder = Path.Combine(Game.SongsFolder, "Broken Song"); Directory.CreateDirectory(malformedFolder);
        File.WriteAllText(Path.Combine(malformedFolder, "Broken Song.tmb"), "not json");
        var malformed = Game.GetSongs().Single(s => s.Folder == malformedFolder);
        Check(!malformed.Readable && GameSongService.ForProject(fixture, [malformed]).Count == 0, "Invalid game charts are listed for management without falsely matching a studio song");
        Check(Directory.Exists(Game.RemoveSong(malformed)), "A broken game song can be removed into backup without parsing its chart");
        Check(File.ReadAllText(player) == "{\"progress\":\"keep unchanged\"}", "Import, update and removal leave game progress/save files untouched");
        Projects.SetGameDataFolder(Game.DataFolder);
        Check(new ProjectService(settingsFile: Path.Combine(TestRoot, "studio-settings.json")).GameDataFolder == Game.DataFolder, "The selected game data folder persists across studio restarts");
        var wrong = new GameSongService(Path.Combine(TestRoot, "not-game-data"));
        try { wrong.ImportSong(fixture); throw new Exception("Missing game folder accepted"); }
        catch (DirectoryNotFoundException) { }
        Check(!Directory.Exists(wrong.DataFolder), "An unconfigured game folder is rejected without creating a fake game directory");
        var gameWindow = new GameSongsWindow(Window, Game, Projects);
        var tabs=((System.Windows.Controls.DockPanel)gameWindow.Content).Children.OfType<System.Windows.Controls.TabControl>().Single();
        Check(tabs.Items.Count==2 && ((System.Windows.Controls.TabItem)tabs.Items[1]).Header.ToString()!.Contains("Deleted"), "The game manager separates installed songs from deleted songs and previous versions");
        var list=(System.Windows.Controls.ListBox)typeof(GameSongsWindow).GetField("_songs",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(gameWindow)!;
        list.SelectedItem=list.Items.Cast<InstalledGameSong>().First(s=>s.Readable && s.HasAudio);
        Check(((System.Windows.Controls.Button)typeof(GameSongsWindow).GetField("_open",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(gameWindow)!).IsEnabled, "Open as Project is enabled for a selected playable installed song");
        gameWindow.Close();
        typeof(MainWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, true);
        var closing = new System.ComponentModel.CancelEventArgs(); Invoke("MainWindow_Closing", Window, closing);
        Check(closing.Cancel && ((System.Windows.Threading.DispatcherTimer)Field("_gameStatusTimer")!).IsEnabled, "A close attempt during import is cancelled without stopping installed-status refresh");
        typeof(MainWindow).GetField("_busy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, false);
    }

    private static async Task RunLibraryPreviewChecks()
    {
        var folder = Path.Combine(TestRoot, "library-preview"); Directory.CreateDirectory(folder);
        var project = Projects.Create("Metadata Practice");
        project.SourceAudioFile = "source.wav"; WriteWave(Path.Combine(project.ProjectFolder, "source.wav"));
        project.Notes = [new ChartNote { Time = .5, Length = 1, Pitch = 60, EndPitch = 62, CurvePoints = [new() { Position = 0, Pitch = 60 }, new() { Position = .5, Pitch = 63 }, new() { Position = 1, Pitch = 62 }] }];
        project.Levels["Normal"] = project.Notes;
        Invoke("OpenProject", project);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")! && (await Js("document.getElementById('audio').readyState>=1")).GetBoolean(), "Library practice editor ready");
        await InvokeAsync("BuildSongAsync");
        var info = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(project))!;
        info.Name = "Configured Song"; info.ShortName = "Custom Short"; info.Author = "Test Artist";
        info.Year = 1998; info.Genre = "Rock"; info.Description = "Artist and song details"; info.TimeSignature = 3;
        info.LevelRatings["Normal"] = 9; info.TmbMetadata["savednotespacing"] = JsonSerializer.SerializeToElement(220);
        info.TmbMetadata["note_color_start"] = JsonSerializer.SerializeToElement(new[] { 1d, 0d, .5 });
        var beforeInfoHistory = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(project))!;
        var notesBeforeInfo = JsonSerializer.Serialize(project.Notes);
        await InvokeAsync("ApplyChartInfoAsync", info);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Metadata editor ready");
        await UndoRedoProject("Undo", () => project.Name == beforeInfoHistory.Name && project.Author == beforeInfoHistory.Author);
        Check(project.ShortName == beforeInfoHistory.ShortName && project.Bpm == beforeInfoHistory.Bpm && JsonSerializer.Serialize(project.Notes) == notesBeforeInfo, "Undo Chart Info restores metadata and timing while preserving all chart notes");
        await UndoRedoProject("Redo", () => project.Name == "Configured Song" && project.Author == "Test Artist");
        Check(project.ShortName == "Custom Short" && project.LevelRatings["Normal"] == 9 && project.TimeSignature == 3, "Redo Chart Info restores custom ratings and metadata with the same chart identity");
        var reopened = Projects.LoadFile(project.ProjectJsonPath, false);
        Check(reopened.Name == "Configured Song" && reopened.ShortName == "Custom Short" && reopened.Author == "Test Artist" && reopened.Year == 1998 && reopened.Genre == "Rock" && reopened.Description == info.Description, "Chart Info persists editable song, short name, artist, year, genre and description");
        Check(JsonSerializer.Serialize(project.Notes) == notesBeforeInfo && reopened.TimeSignature == 3, "Saving chart details preserves original curve notes and chart identity");
        var exported = new TmbService().ReadTmb(project.ChartPath);
        Check(exported.Difficulty == "Normal" && TmbService.Rating(exported) == 9 && exported.ShortName == "Custom Short" && exported.TmbMetadata["savednotespacing"].GetDouble() == 220 && exported.TmbMetadata["note_color_start"][2].GetDouble() == .5, "TMB retains custom metadata and rating independently of the editor difficulty level");
        var metadataWindow = new ChartInfoWindow(Window, project); metadataWindow.Close();
        Check(StudioCommands.ChartInfo.CanExecute(null, Window) && StudioCommands.Library.CanExecute(null, Window), "Chart Info and Online Library commands are available in the studio");
        var contextCopy = Projects.SaveCopyAs(project, Path.Combine(folder,"Context Song.msmproj"));
        var contextInfo = JsonSerializer.Deserialize<SongProject>(JsonSerializer.Serialize(contextCopy))!;
        contextInfo.Author = "Context Artist"; contextInfo.ShortName = "Context Short";
        var activeKey = (string)Field("_projectKey")!; var contextNotes = JsonSerializer.Serialize(contextCopy.Notes);
        await InvokeAsync("ApplyRecentChartInfoAsync", contextCopy.ProjectJsonPath, contextInfo);
        var contextSaved = Projects.LoadFile(contextCopy.ProjectJsonPath,false);
        Check(contextSaved.Author == "Context Artist" && contextSaved.ShortName == "Context Short" && JsonSerializer.Serialize(contextSaved.Notes)==contextNotes && (string)Field("_projectKey")! == activeKey, "Recent-project Chart Info updates the targeted song while preserving notes and the active editor");
        var recentMenu = (System.Windows.Controls.ContextMenu)Field("RecentProjectsContextMenu")!;
        Check(recentMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(i=>i.Tag?.ToString()=="ChartInfo") && ((System.Windows.Controls.Button)Field("ChartInfoButton")!).Command==StudioCommands.ChartInfo, "Chart Info is available in the song header and recent-project right-click menu");
        var isolatedGame = new GameSongService(Path.Combine(folder,"game")); Directory.CreateDirectory(isolatedGame.SongsFolder);
        isolatedGame.ImportSong(project); var song = isolatedGame.GetSongs().Single();
        Check(isolatedGame.SyncStatus(project, song) == "up to date", "Sync comparison accepts matching installed chart metadata and audio");
        project.Author = "Changed Artist";
        Check(isolatedGame.SyncStatus(project,song) == "chart or info changed", "Metadata edits mark installed songs out of sync");
        project.Author = info.Author; project.Notes[0].Length += .1;
        Check(isolatedGame.SyncStatus(project,song) == "chart or info changed", "Note length changes mark installed charts out of sync");
        project.Notes[0].Length -= .1;
        var installedChart = File.ReadAllText(song.ChartFile); File.WriteAllText(song.ChartFile, installedChart.Replace("Test Artist", "External Artist"));
        Check(isolatedGame.SyncStatus(project,song) == "chart or info changed", "External game chart edits are detected without relying on presence or identity");
        File.WriteAllText(song.ChartFile,installedChart);
        var audioBytes=File.ReadAllBytes(song.AudioFile); File.AppendAllText(song.AudioFile,"change");
        Check(isolatedGame.SyncStatus(project,song)=="audio changed", "Changed installed OGG content marks the song out of sync");
        File.WriteAllBytes(song.AudioFile,audioBytes);
        var originalSource = project.SourceAudioFile;
        var changedSource = Path.Combine(project.ProjectFolder,"changed-source.wav");
        File.Copy(Path.Combine(project.ProjectFolder,originalSource!),changedSource); File.AppendAllText(changedSource,"source edit");
        project.SourceAudioFile = "changed-source.wav";
        Check(isolatedGame.SyncStatus(project,song)=="source audio changed", "Source audio edits are detected even when the converted OGG is still unchanged");
        project.SourceAudioFile = originalSource;
        new TmbService().WriteTmb(project); isolatedGame.ImportSong(project);
        Check(isolatedGame.SyncStatus(project,isolatedGame.GetSongs().Single())=="up to date", "Updating game files restores up-to-date status");

        var library = new LibraryService(Projects,Path.Combine(folder,"downloads"));
        var zipFile = new ExportService().ExportZip(project,Path.Combine(folder,"download.zip"));
        var imported = library.Import(zipFile).Single(); library.Record(zipFile,"https://toottally.com/search/",[imported]);
        Check(imported.Name==project.Name && imported.ShortName==project.ShortName && imported.Author==project.Author && imported.Notes.Count==2 && imported.OggFile is not null && File.Exists(Path.Combine(imported.ProjectFolder,imported.OggFile)), "Downloaded nested/game ZIP imports paired audio and existing TMB slides as editable notes without auto generation");
        Check(new LibraryService(Projects,library.Folder).History().Single().Projects.Single()==imported.ProjectJsonPath, "Downloaded packages retain source and editable project history across restarts");
        Check(library.NewDownloadPath("same.zip")!=library.NewDownloadPath("same.zip") && !LibraryService.Supported("run.exe"), "Downloads use distinct paths and accept only chart/package/project formats");
        var unsafeZip=Path.Combine(folder,"unsafe.zip");
        using(var archive=ZipFile.Open(unsafeZip,ZipArchiveMode.Create)) using(var writer=new StreamWriter(archive.CreateEntry("../../escape.tmb").Open())) writer.Write("unsafe");
        try { library.Import(unsafeZip); throw new Exception("Unsafe archive accepted"); } catch(InvalidDataException) { }
        Check(!File.Exists(Path.Combine(folder,"escape.tmb")), "ZIP import rejects traversal before writing outside its extraction directory");
        var noChart=Path.Combine(folder,"no-chart.zip");
        using(var archive=ZipFile.Open(noChart,ZipArchiveMode.Create)) using(var writer=new StreamWriter(archive.CreateEntry("run.exe").Open())) writer.Write("not executable");
        try { library.Import(noChart); throw new Exception("Unsupported package accepted"); } catch(InvalidDataException) { }
        Check(!Directory.GetFiles(library.Folder,"*.exe",SearchOption.AllDirectories).Any(), "Library never extracts executables or accepts chartless ZIPs");
        var portable=Projects.SaveCopyAs(project,Path.Combine(folder,"Portable.msmproj"));
        var projectZip=Path.Combine(folder,"project.zip"); using(var archive=ZipFile.Open(projectZip,ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(portable.ProjectJsonPath,Path.GetFileName(portable.ProjectJsonPath));
            foreach(var asset in Directory.GetFiles(portable.ProjectFolder)) archive.CreateEntryFromFile(asset,portable.AssetsFolder+"/"+Path.GetFileName(asset));
        }
        Check(library.Import(projectZip).Single().Notes[0].CurvePoints.Count==3, "Studio project ZIP preserves the original full curve and linked companion assets");

        // A local HTTP fixture exercises the actual browser DownloadStarting/Completed path without downloading community content.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var fixtureZip = File.ReadAllBytes(zipFile);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(); await using var stream = client.GetStream();
            using var reader = new StreamReader(stream,leaveOpen:true);
            string? line; do { line=await reader.ReadLineAsync(); } while(!string.IsNullOrEmpty(line));
            var headers=System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Disposition: attachment; filename=\"FixtureSong.zip\"\r\nContent-Length: " + fixtureZip.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers); await stream.WriteAsync(fixtureZip);
        });
        var downloadWindow = new LibraryWindow(Window,Projects,library,$"http://127.0.0.1:{port}/FixtureSong.zip") { Opacity=0, ShowInTaskbar=false };
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(30) };
        timeout.Tick += (_,_) => { timeout.Stop(); foreach(var d in (List<Microsoft.Web.WebView2.Core.CoreWebView2DownloadOperation>)typeof(LibraryWindow).GetField("_active",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(downloadWindow)!) d.Cancel(); downloadWindow.Close(); };
        timeout.Start(); downloadWindow.ShowDialog(); timeout.Stop(); listener.Stop();
        try { await server; }
        catch (System.Net.Sockets.SocketException ex) when (downloadWindow.SelectedProject is null)
        {
            var status = (System.Windows.Controls.TextBlock)typeof(LibraryWindow).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(downloadWindow)!;
            throw new InvalidOperationException("Library download fixture did not finish: " + status.Text, ex);
        }
        Check(downloadWindow.SelectedProject is not null && downloadWindow.SelectedProject.Notes.Count==2 && library.History().First().Source.StartsWith("http://127.0.0.1:"), "The actual library browser downloads a ZIP, imports its chart/audio, records its source and hands the editable song to the studio");

        Invoke("OpenProject", project);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString()==(string)Field("_projectKey")! && (await Js("document.getElementById('audio').readyState>=1")).GetBoolean(), "Play-test editor ready");
        var beforePreview=JsonSerializer.Serialize((await Snapshot()).GetProperty("notes"));
        await Js("window.msmPreview.setMode('preview')");
        Check((await Js("document.getElementById('chartViewport').getBoundingClientRect().height===0 && !document.getElementById('previewViewport').hidden && document.getElementById('delete').disabled")).GetBoolean(), "Preview swaps the editor for an automatic-follow view and disables note editing");
        Check(Math.Abs((await Js("window.msmPreview.pitchAt(window.msmEditor.getPreviewState().notes[0],1)")).GetDouble()-63)<.001, "Play-test pitch follows the actual curved path rather than a straight endpoint line");
        var missed=await Js("(() => {window.msmPreview.setMode('test');window.msmEditor.stopPlayback();window.msmPreview.reset();window.msmPreview.setInput(47,false);for(let i=1;i<=160;i++)window.msmPreview.judge(i/100);return window.msmPreview.getState();})()");
        Check(missed.GetProperty("totalSeconds").GetDouble()>.9 && missed.GetProperty("hitSeconds").GetDouble()==0 && missed.GetProperty("combo").GetInt32()==0, "Play Test records missed sustains when the mouse is not held on the note");
        var correct=await Js("(() => {window.msmPreview.reset();const n=window.msmEditor.getPreviewState().notes[0];for(let i=1;i<=160;i++){window.msmPreview.setInput(window.msmPreview.pitchAt(n,(i-.5)/100),true);window.msmPreview.judge(i/100);}return window.msmPreview.getState();})()");
        Check(correct.GetProperty("hitSeconds").GetDouble()>.98 && correct.GetProperty("combo").GetInt32()==1, "Following the curved sustain while holding scores a hit and combo from audio-clock intervals");
        await Js("window.msmPreview.reset();window.msmEditor.togglePlay()");
        await WaitUntil(async () => (await Js("document.getElementById('audio').currentTime>.15 && !document.getElementById('audio').paused")).GetBoolean(), "Play-test real audio clock");
        await Js("window.msmPreview.setInstrument('soft');window.msmPreview.setInput(69,true)");
        await WaitUntil(async () => (await Js("window.msmPreview.getToneState().context==='running' && window.msmPreview.getToneState().rms>.01")).GetBoolean(), "Synthesized play-test output");
        await Task.Delay(120);
        Check(Math.Abs((await Js("window.msmPreview.getToneState().frequency")).GetDouble()-440)<.1, "Holding at MIDI pitch 69 produces audible 440 Hz audio through the live Web Audio graph");
        await Js("window.msmPreview.setInput(57,true)"); await Task.Delay(120);
        Check(Math.Abs((await Js("window.msmPreview.getToneState().frequency")).GetDouble()-220)<.1, "Moving one octave lower continuously changes the sounding pitch to 220 Hz");
        await Js("window.msmPreview.setInput(60.5,true)"); await Task.Delay(120);
        Check(Math.Abs((await Js("window.msmPreview.getToneState().frequency")).GetDouble()-440*Math.Pow(2,(60.5-69)/12))<.1, "Fractional mouse positions produce continuous slide pitches rather than snapping to cells");
        await Js("document.getElementById('audio').volume=.25"); await Task.Delay(100);
        Check(Math.Abs((await Js("window.msmPreview.getToneState().level")).GetDouble()-.055)<.001, "The existing volume control also scales the play-test instrument");
        await Js("document.getElementById('audio').muted=true"); await Task.Delay(150);
        Check((await Js("window.msmPreview.getToneState().rms<.0001")).GetBoolean(), "Muting the song silences the generated instrument output too");
        await Js("document.getElementById('audio').muted=false;document.getElementById('audio').volume=1;window.dispatchEvent(new Event('pointerup'))"); await Task.Delay(150);
        Check((await Js("!window.msmPreview.getState().held && window.msmPreview.getToneState().rms<.0001")).GetBoolean(), "Releasing the mouse stops the actual instrument samples");
        await Js("window.msmPreview.setInput(69,true);window.dispatchEvent(new Event('blur'))"); await Task.Delay(150);
        Check((await Js("!window.msmPreview.getState().held && window.msmPreview.getToneState().rms<.0001")).GetBoolean(), "Losing window focus releases the instrument and prevents stuck notes");
        await Js("window.msmPreview.setInput(69,true)");
        foreach(var preset in new[]{("flute","sine"),("brass","custom"),("organ","custom"),("chip","square"),("soft","triangle")})
        {
            await Js($"document.getElementById('testInstrument').value='{preset.Item1}';document.getElementById('testInstrument').dispatchEvent(new Event('change'))"); await Task.Delay(120);
            Check((await Js($"window.msmPreview.getToneState().waveform==='{preset.Item2}' && window.msmPreview.getToneState().rms>.01 && Math.abs(window.msmPreview.getToneState().frequency-440)<.1 && window.msmPreview.getState().held && !document.getElementById('audio').paused")).GetBoolean(), preset.Item1+" selector changes the audible instrument while preserving pitch, hold and playback");
        }
        await Js("window.msmPreview.setInstrument('none')"); await Task.Delay(150);
        Check((await Js("window.msmPreview.getToneState().rms<.0001 && window.msmPreview.getState().held && !document.getElementById('audio').paused && localStorage.getItem('msm-play-test-instrument')==='none'")).GetBoolean(), "None silences only the instrument and persists the choice while song playback and held input continue");
        await Js("window.msmPreview.setInstrument('flute')"); await Task.Delay(120);
        Check((await Js("window.msmPreview.getToneState().rms>.01 && localStorage.getItem('msm-play-test-instrument')==='flute'")).GetBoolean(), "Selecting an instrument after None restores sound without restarting playback");
        await Js("window.msmPreview.setInstrument('soft')");
        await Js("window.msmEditor.togglePlay()"); var paused=await Js("window.msmPreview.getState().totalSeconds"); await Task.Delay(120);
        Check((await Js("document.getElementById('audio').paused")).GetBoolean() && Math.Abs((await Js("window.msmPreview.getState().totalSeconds")).GetDouble()-paused.GetDouble())<.03, "Pausing play test stops audio timing and scoring together");
        Check((await Js("!window.msmPreview.getState().held && window.msmPreview.getToneState().rms<.0001")).GetBoolean(), "Pausing playback silences the generated note immediately");
        await Js("window.msmPreview.setMode('editor')");
        Check(JsonSerializer.Serialize((await Snapshot()).GetProperty("notes"))==beforePreview && (await Js("document.getElementById('previewViewport').hidden && document.getElementById('chartViewport').getBoundingClientRect().height>0")).GetBoolean(), "Returning from play test restores the editor without changing or regenerating notes");
    }

    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static async Task RunPackageMetadataChecks(string package)
    {
        var service=new TmbService();
        var fixture=new SongProject {Name="Numeric Metadata",Year=2025,Bpm=123,TimeSignature=4,Difficulty="Hard",Notes=[new(){Time=.5,Length=1,Pitch=60,EndPitch=65}]};
        var data=System.Text.Json.Nodes.JsonNode.Parse(service.SerializeChart(fixture))!;
        var file=Path.Combine(TestRoot,"numeric-text.tmb");
        data["year"]="2025"; File.WriteAllText(file,data.ToJsonString());
        Check(service.ReadTmb(file).Year==2025,"TMB year metadata accepts a quoted year without changing notes");
        data["tempo"]="123.0";data["timesig"]="4";data["difficulty"]="8";File.WriteAllText(file,data.ToJsonString());
        var restored=service.ReadTmb(file);
        Check(restored.Bpm==123 && restored.TimeSignature==4 && restored.LevelRatings["Hard"]==8 && Math.Abs(restored.Notes[0].Time-.5)<1e-9 && Math.Abs(restored.Notes[0].Length-1)<1e-9,"Numeric text tempo, time signature and difficulty preserve exact note timing");
        foreach(var value in new[]{"not a year","2025.5","NaN","2147483648"})
        {
            data["year"]=value;File.WriteAllText(file,data.ToJsonString());
            try{service.ReadTmb(file);throw new Exception("Invalid year accepted");}catch(InvalidDataException ex){Check(ex.Message.Contains("year"),"Invalid year '"+value+"' reports the chart field clearly");}
        }
        var hash=GameSongService.HashFile(package);
        var library=new LibraryService(Projects,Path.Combine(TestRoot,"package-downloads"));
        var imported=library.Import(package).Single();
        Check(imported.Year==2025 && imported.Name=="HUNTR/X- Golden" && imported.Author=="HUNTR/X" && imported.Notes.Count==470,"The actual Golden ZIP imports its year, title, artist and all 470 original notes");
        Check(imported.Bpm==123 && imported.TimeSignature==4 && imported.LevelRatings["Hard"]==8 && File.Exists(Path.Combine(imported.ProjectFolder,imported.OggFile!)),"The actual package keeps tempo, level rating and paired OGG audio");
        Check(GameSongService.HashFile(package)==hash,"Import leaves the user's downloaded ZIP unchanged");
        var roundTrip=service.ReadTmb(imported.ChartPath);
        Check(roundTrip.Year==2025 && roundTrip.Notes.Count==470 && roundTrip.TmbMetadata.ContainsKey("color_events"),"Saving the imported TMB retains its original notes and extra chart metadata");
        Invoke("OpenProject",imported);
        await WaitUntil(async()=> (await Snapshot()).GetProperty("notes").GetArrayLength()==470 && (await Js("document.getElementById('audio').readyState>=1")).GetBoolean(),"Actual Golden chart editor and audio");
        Check((await Js("document.getElementById('audio').duration>180 && !document.getElementById('play').disabled")).GetBoolean(),"The imported Golden song loads for playback and editing in the real WebView2 studio");
    }

    private static async Task RunInstallerChecks()
    {
        var directory = Path.Combine(TestRoot, "downloaded-ffmpeg");
        var converter = new FfmpegService(installDirectory: directory);
        Check(!converter.IsAvailable, "Fresh app-private installation starts without FFmpeg");
        var progress = new List<double>();
        var executable = await converter.EnsureInstalledAsync(new ImmediateProgress(progress.Add));
        Check(await FfmpegInstaller.IsVerifiedAsync(executable), "Fresh setup downloads the pinned FFmpeg release and verifies the installed executable");
        Check(!typeof(FfmpegInstaller).Assembly.GetManifestResourceNames().Contains("MySingingMonsterKaraokeStudio.Tools.ffmpeg.exe") && Directory.GetFiles(directory,"*.tmp").Length==0, "The app contains no bundled FFmpeg executable and setup removes the downloaded archive");
        var reference=Path.Combine(TestRoot,"verified-fixture-ffmpeg.exe"); File.Copy(executable,reference);
        Func<Stream> fixture=()=>File.OpenRead(reference);
        Check(progress.Count > 2 && progress[0] == 0 && progress[^1] == 100, "One-click installation reports progress to completion");
        Check(File.Exists(Path.Combine(directory, "FFmpeg-LICENSE.txt")) && File.Exists(Path.Combine(directory, "FFmpeg-SOURCE.txt")), "Installation includes FFmpeg license and source notices");
        var modified = File.GetLastWriteTimeUtc(executable);
        await converter.EnsureInstalledAsync();
        Check(File.GetLastWriteTimeUtc(executable) == modified, "Repeated installation reuses the verified executable");
        File.WriteAllBytes(executable, [1, 2, 3]);
        await Task.WhenAll(new FfmpegService(installDirectory: directory,openExecutable:fixture).EnsureInstalledAsync(), new FfmpegService(installDirectory: directory,openExecutable:fixture).EnsureInstalledAsync());
        Check(await FfmpegInstaller.IsVerifiedAsync(executable), "Concurrent setup requests safely repair a corrupt installed copy");

        var autoDirectory = Path.Combine(TestRoot, "automatic-ffmpeg");
        var automatic = new FfmpegService(installDirectory: autoDirectory,openExecutable:fixture);
        var output = Path.Combine(TestRoot, "automatic-song.ogg");
        await automatic.ConvertToOggAsync(Path.Combine(TestRoot, "source.wav"), output);
        Check(automatic.IsAvailable && File.ReadAllBytes(output).Take(4).SequenceEqual("OggS"u8.ToArray()), "Conversion automatically installs FFmpeg and produces real OGG audio");

        var rejectedDirectory = Path.Combine(TestRoot, "rejected-ffmpeg");
        var rejected = new FfmpegInstaller(rejectedDirectory, () => new MemoryStream([1, 2, 3]));
        try { await rejected.EnsureInstalledAsync(); throw new Exception("Unverified installer content was accepted"); }
        catch (InvalidDataException) { }
        Check(!File.Exists(rejected.ExecutablePath) && Directory.GetFiles(rejectedDirectory, "*.tmp").Length == 0, "Invalid installer content is rejected and temporary files are removed");

        var cancelledDirectory = Path.Combine(TestRoot, "cancelled-ffmpeg");
        using var cancellation = new CancellationTokenSource();
        var cancelled = new FfmpegInstaller(cancelledDirectory,fixture);
        try
        {
            await cancelled.EnsureInstalledAsync(new ImmediateProgress(value => { if (value > 10) cancellation.Cancel(); }), cancellation.Token);
            throw new Exception("Cancelled installation completed");
        }
        catch (OperationCanceledException) { }
        Check(!File.Exists(cancelled.ExecutablePath) && Directory.GetFiles(cancelledDirectory, "*.tmp").Length == 0, "Cancelled installation leaves no executable or partial temporary file");
        await cancelled.EnsureInstalledAsync();
        Check(await FfmpegInstaller.IsVerifiedAsync(cancelled.ExecutablePath), "Installation succeeds when retried after cancellation");
        var badArchiveDirectory=Path.Combine(TestRoot,"bad-download");
        var badArchive=new FfmpegInstaller(badArchiveDirectory,openArchive: _=>Task.FromResult<Stream>(new MemoryStream([1,2,3])));
        try { await badArchive.EnsureInstalledAsync(); throw new Exception("Unverified downloaded archive accepted"); } catch(InvalidDataException) { }
        Check(!File.Exists(badArchive.ExecutablePath) && Directory.GetFiles(badArchiveDirectory,"*.tmp").Length==0,"Invalid downloaded archives are rejected before extraction and cleaned up");
        var failed=new FfmpegInstaller(Path.Combine(TestRoot,"network-failure"),openArchive:_=>Task.FromException<Stream>(new System.Net.Http.HttpRequestException("test offline")));
        try { await failed.EnsureInstalledAsync(); throw new Exception("Failed network download succeeded"); } catch(System.Net.Http.HttpRequestException) { }
        Check(!File.Exists(failed.ExecutablePath) && Directory.GetFiles(failed.InstallDirectory,"*.tmp").Length==0,"Network failure leaves no converter or partial download and can be retried");
    }

    private static async Task Pointer(string type, double x, double y, bool shift = false, int button = 0)
    {
        var data = JsonSerializer.Serialize(new { type, x, y, shift, button });
        await Js("(() => {const p=" + data + ";const c=document.getElementById('chart'),r=c.getBoundingClientRect(),v=document.getElementById('chartViewport');c.dispatchEvent(new PointerEvent(p.type,{button:p.button,pointerId:1,clientX:r.left+p.x-v.scrollLeft,clientY:r.top+p.y-v.scrollTop,shiftKey:p.shift,bubbles:true}));})()");
    }

    private static async Task ContextMenu(double x, double y)
    {
        var data = JsonSerializer.Serialize(new { x, y });
        await Js("(() => {const p=" + data + ";const c=document.getElementById('chart'),r=c.getBoundingClientRect(),v=document.getElementById('chartViewport');c.dispatchEvent(new MouseEvent('contextmenu',{button:2,clientX:r.left+p.x-v.scrollLeft,clientY:r.top+p.y-v.scrollTop,bubbles:true,cancelable:true}));})()");
    }

    private static async Task BrowserMouse(string type, double x, double y, bool held, string button = "right")
    {
        var data = JsonSerializer.Serialize(new { type, x, y });
        var position = await Js("(() => {const p=" + data + ";const c=document.getElementById('chart'),v=document.getElementById('chartViewport');if(p.type==='mousePressed'){if(p.x<v.scrollLeft||p.x>=v.scrollLeft+v.clientWidth)v.scrollLeft=Math.max(0,p.x-v.clientWidth/2);if(p.y<v.scrollTop||p.y>=v.scrollTop+v.clientHeight)v.scrollTop=Math.max(0,p.y-v.clientHeight/2);}const r=c.getBoundingClientRect(),x=r.left+p.x-v.scrollLeft,y=r.top+p.y-v.scrollTop;return {x,y,target:document.elementFromPoint(x,y)?.id??null,width:v.clientWidth,height:v.clientHeight};})()");
        if (type == "mousePressed" && position.GetProperty("target").GetString() != "chart")
            throw new InvalidOperationException("Trusted mouse press must start on the visible chart: " + position.GetRawText());
        await Editor.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new
        {
            type, x = position.GetProperty("x").GetDouble(), y = position.GetProperty("y").GetDouble(),
            button = type == "mouseMoved" ? "none" : button, buttons = held ? (button == "left" ? 1 : 2) : 0, clickCount = type == "mouseMoved" ? 0 : 1, pointerType = "mouse"
        }));
    }

    private static async Task Gesture(double x1, double y1, double x2, double y2, bool shift = false)
    {
        await Pointer("pointerdown", x1, y1, shift);
        await Pointer("pointermove", x2, y2, shift);
        await Pointer("pointerup", x2, y2, shift);
    }

    private static async Task<int> SelectedCount()
    {
        var text = (await Js("document.getElementById('selectionStatus').textContent")).GetString()!;
        return text.StartsWith("No ") ? 0 : int.Parse(text.Split(' ')[0]);
    }

    private static async Task Key(string key, bool ctrl = false)
    {
        var data = JsonSerializer.Serialize(new { key, ctrl });
        await Js("(() => {const p=" + data + ";document.getElementById('chart').dispatchEvent(new KeyboardEvent('keydown',{key:p.key,ctrlKey:p.ctrl,bubbles:true,cancelable:true}));})()");
    }

    private static async Task RunSelectionChecks()
    {
        var group = new SongProject { Name = "Selection Test", ProjectFolder = Path.Combine(TestRoot, "selection"), Bpm = 120, OffsetSeconds = .5,
            Notes = [new() { Time = 3, Length = .5, Pitch = 60, EndPitch = 62 }, new() { Time = 4, Length = .75, Pitch = 64, EndPitch = 64 }, new() { Time = 7, Length = .5, Pitch = 70, EndPitch = 70 }] };
        Projects.Save(group);
        Invoke("OpenProject", group);
        await WaitUntil(async () => (await Snapshot()).GetProperty("notes").GetArrayLength() == 3, "Load selection fixture");
        await Js("(() => {const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;v.scrollTop=44;})()");
        await Js("document.getElementById('tool').value='select'");

        // Coordinates include chart scrolling and the project's half-second timing offset.
        await Gesture(800, 180, 1200, 310);
        Check(await SelectedCount() == 2 && (await Snapshot()).GetProperty("notes").GetArrayLength() == 3, "Marquee selects multiple notes without creating one");
        Check((await Js("document.getElementById('save').textContent")).GetString() == "Save chart…", "Selecting notes does not change the chart or undo history");
        await Gesture(850, 292, 1090, 248);
        var moved = (await Snapshot()).GetProperty("notes");
        Check(moved[0].GetProperty("time").GetDouble() == 4 && moved[1].GetProperty("time").GetDouble() == 5 && moved[0].GetProperty("pitch").GetInt32() == 62 && moved[1].GetProperty("pitch").GetInt32() == 66 && moved[0].GetProperty("endPitch").GetInt32() == 64 && moved[2].GetProperty("time").GetDouble() == 7, "Dragging a selection preserves timing, pitch intervals, and slides");
        await Key("z", true);
        var original = (await Snapshot()).GetProperty("notes");
        Check(original[0].GetProperty("time").GetDouble() == 3 && original[1].GetProperty("time").GetDouble() == 4 && await SelectedCount() == 2, "Group movement undoes in one step and retains selection");
        await Key("y", true);
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("time").GetDouble() == 4, "Group movement redoes in one step");
        await Key("z", true);

        // Right edge of the first selected note: both selected lengths change by the same amount.
        await Gesture(958, 253, 1018, 253);
        var resized = (await Snapshot()).GetProperty("notes");
        Check(resized[0].GetProperty("length").GetDouble() == .75 && resized[1].GetProperty("length").GetDouble() == 1 && resized[2].GetProperty("length").GetDouble() == .5, "Resizing a selection preserves relative lengths");
        await Key("z", true);

        await Gesture(1840, 60, 2010, 90, true);
        Check(await SelectedCount() == 3, "Shift marquee adds notes to existing selection");
        await Gesture(1810, 73, 1810, 73, true);
        Check(await SelectedCount() == 2, "Shift clicking a selected note removes it from selection");
        await Gesture(1810, 73, 1810, 73, true);
        Check(await SelectedCount() == 3, "Shift clicking an unselected note adds it to selection");
        await Key("Escape");
        Check(await SelectedCount() == 0 && (await Snapshot()).GetProperty("notes").GetArrayLength() == 3, "Escape clears selection without deleting notes");
        await Gesture(1200, 310, 800, 180);
        Check(await SelectedCount() == 2, "Reverse marquee selection works with scrolling and offset");
        await Key("Delete");
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1 && await SelectedCount() == 0, "Delete removes the selected group only");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 3 && await SelectedCount() == 2, "Undo restores the entire deleted selection");
        await Key("a", true);
        Check(await SelectedCount() == 3, "Ctrl+A selects all notes");
        await Gesture(850, 292, -800, 900);
        var bounded = (await Snapshot()).GetProperty("notes");
        Check(bounded[0].GetProperty("time").GetDouble() == 0 && bounded[1].GetProperty("time").GetDouble() == 1 && bounded[2].GetProperty("time").GetDouble() == 4 && bounded[0].GetProperty("pitch").GetInt32() == 47 && bounded[2].GetProperty("pitch").GetInt32() == 57, "Group movement clamps at chart boundaries without collapsing spacing");
        await Key("z", true);

        await Pointer("pointerdown", 850, 292);
        await Pointer("pointermove", 1090, 248);
        await Pointer("pointercancel", 1090, 248);
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("time").GetDouble() == 3, "Cancelled drag restores the original group");
        await Gesture(1400, 350, 1402, 351);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 4 && await SelectedCount() == 1, "A click with slight pointer jitter still adds one note");
        await Key("z", true);
        await InvokeAsync("SaveChartFromEditorAsync");
        var savedGroup = Projects.Load(group.ProjectFolder);
        Check(savedGroup.Notes.Count == 3 && savedGroup.Notes[0].Time == 3, "Multi-note edits save and reload using the existing project format");

        group.Notes = [new() { Time = 3.13, Length = .61, Pitch = 60, EndPitch = 60 }, new() { Time = 4.2, Length = .48, Pitch = 64, EndPitch = 64 }];
        Invoke("OpenProject", group);
        await WaitUntil(async () => (await Snapshot()).GetProperty("notes").GetArrayLength() == 2, "Load unsnapped fixture");
        await Gesture(881, 292, 882, 293);
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("time").GetDouble() == 3.13, "Clicking an unsnapped note selects without changing its time");
        await Gesture(1015, 292, 1016, 293);
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("length").GetDouble() == .61, "Clicking an unsnapped right edge does not resize the note");
        await Key("a", true);
        await Gesture(881, 292, 882, 248);
        var vertical = (await Snapshot()).GetProperty("notes");
        Check(vertical[0].GetProperty("time").GetDouble() == 3.13 && vertical[1].GetProperty("time").GetDouble() == 4.2 && vertical[0].GetProperty("pitch").GetInt32() == 62, "Vertical group movement preserves unsnapped timing");
        await InvokeAsync("SaveChartFromEditorAsync");
    }

    private static async Task RunPaintingAndReopenChecks()
    {
        var paint = Projects.CreateAtFile(Path.Combine(TestRoot, "Paint Song.msmproj"));
        File.Copy(Path.Combine(TestRoot, "Pipeline Audio.mp3"), Path.Combine(paint.ProjectFolder, "source.mp3"));
        paint.SourceAudioFile = "source.mp3"; paint.AudioName = "Paint Song.mp3"; paint.OffsetSeconds = .5;
        Projects.Save(paint); Invoke("OpenProject", paint);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")! && (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Paint project ready");
        await Js("(() => {document.getElementById('tool').value='paint';const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;v.scrollTop=44;})()");
        await Gesture(850, 292, 1090, 292);
        var stroke = await Snapshot();
        Check(stroke.GetProperty("notes").GetArrayLength() == 1 && stroke.GetProperty("notes")[0].GetProperty("time").GetDouble() == 3 && stroke.GetProperty("notes")[0].GetProperty("length").GetDouble() == 1 && stroke.GetProperty("notes")[0].GetProperty("pitch").GetInt32() == 60, "Dragging Paint creates one sustained note with correct scroll and audio offset");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 0, "One Undo removes an entire painted stroke");
        await Key("y", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1, "One Redo restores the sustained painted note");
        await Js("document.getElementById('noteLength').value=.6");
        await Gesture(1300, 358, 1300, 358); await Gesture(1600, 402, 1600, 402);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 3 && (await Snapshot()).GetProperty("notes")[1].GetProperty("length").GetDouble() == .6 && (await Snapshot()).GetProperty("notes")[2].GetProperty("length").GetDouble() == .6, "Separate clicks create individual notes using the chosen duration");
        await Js("document.getElementById('noteLength').value=.25");
        await Key("a", true);
        await Pointer("pointerdown", 850, 292, button: 2);
        var erased = (await Snapshot()).GetProperty("notes");
        Check(erased.GetArrayLength() == 2 && erased.EnumerateArray().All(n => n.GetProperty("time").GetDouble() != 3) && await SelectedCount() == 2, "Right-click in Paint erases only the pointed note from a selected group");
        await Pointer("pointerup", 850, 292, button: 2);
        await ContextMenu(850, 292);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 2, "Right-button release does not erase a second note or add a painted cell");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 3 && await SelectedCount() == 3, "Undo restores the right-clicked note and the previous group selection");
        await Key("y", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 2, "Redo repeats only the single-note right-click deletion");
        await Key("z", true);
        var beforeEmpty = await Snapshot();
        await Pointer("pointerdown", 1300, 350, button: 2);
        await Pointer("pointerup", 1300, 350, button: 2);
        await ContextMenu(1300, 350);
        Check((await Snapshot()).GetProperty("revision").GetInt64() == beforeEmpty.GetProperty("revision").GetInt64() && (await Snapshot()).GetProperty("notes").GetArrayLength() == 3, "Right-click on empty Paint space makes no notes and no chart changes");
        await Js("document.getElementById('tool').value='select'");
        await ContextMenu(850, 292);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 2 && await SelectedCount() == 2, "Context-menu deletion also erases only the pointed note in Select mode");
        await Key("z", true);
        await Js("document.getElementById('tool').value='paint'");
        await Key("Escape");
        await Gesture(800, 280, 1150, 310, true);
        Check(await SelectedCount() == 1 && (await Snapshot()).GetProperty("notes").GetArrayLength() == 3, "Shift-drag in Paint mode selects notes without adding notes");
        await Gesture(1300, 270, 850, 270);
        var backtrack = (await Snapshot()).GetProperty("notes");
        Check(backtrack.GetArrayLength() == 4 && backtrack[3].GetProperty("time").GetDouble() == 3 && backtrack[3].GetProperty("length").GetDouble() == 1.75, "Reverse painting draws one note spanning the dragged interval");
        await Gesture(1200, 226, 1500, 358);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 5 && (await Snapshot()).GetProperty("notes")[4].GetProperty("pitch").GetInt32() == 63 && (await Snapshot()).GetProperty("notes")[4].GetProperty("length").GetDouble() == 1.25, "A diagonal Paint drag retains its starting pitch and creates one sustained note");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 4, "Diagonal stroke also undoes as a single operation");
        await Pointer("pointerdown", 1400, 402);
        await Pointer("pointermove", 1600, 380);
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && !await (Task<bool>)Invoke("TryAutoSaveAsync")!, "Automatic saving defers during a live painted stroke");
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean(), "Deferred automatic saving does not end or interrupt painting");
        await Pointer("pointercancel", 1600, 380);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 4, "Cancelled painting removes the uncommitted sustained note");
        await WaitUntil(() => Task.FromResult(Projects.LoadFile(paint.ProjectJsonPath).Notes.Count == 4 && !(bool)Field("_dirty")!), "Automatic chart save");
        Check(Projects.LoadFile(paint.ProjectJsonPath).SourceAudioFile == "source.mp3" && File.Exists(paint.ChartPath), "Automatic saving persists painted notes and the selected MP3 without pressing Save");

        var previous = await Snapshot();
        await Gesture(1810, 204, 1810, 204);
        await WaitUntil(() => Task.FromResult((bool)Field("_dirty")!), "New edit dirty message");
        Invoke("SaveSnapshot", previous.GetRawText());
        await Task.Delay(100);
        Check((bool)Field("_dirty")! && (await Js("document.getElementById('save').textContent.endsWith('*')")).GetBoolean(), "A delayed save acknowledgement cannot mark newer notes as saved");
        await WaitUntil(() => Task.FromResult(Projects.LoadFile(paint.ProjectJsonPath).Notes.Count == 5 && !(bool)Field("_dirty")!), "Newer note automatically saved");
        Check(true, "Automatic saving follows up and persists the newer revision");

        var elsewhere = Projects.CreateAtFile(Path.Combine(TestRoot, "Other Song.msmproj"));
        Invoke("OpenProject", elsewhere);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Other project ready");
        var recents = (System.Windows.Controls.ListBox)Field("RecentProjectsList")!;
        recents.SelectedItem = recents.Items.Cast<object>().Single(i => (string)i.GetType().GetProperty("FilePath")!.GetValue(i)! == paint.ProjectJsonPath);
        await WaitUntil(async () => Field("_project") is SongProject p && p.ProjectJsonPath == paint.ProjectJsonPath && (await Snapshot()).GetProperty("notes").GetArrayLength() == 5 && (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Single-click recent song reload");
        Check(!(await Js("document.getElementById('play').disabled")).GetBoolean() && (await Snapshot()).GetProperty("offset").GetDouble() == .5, "A single recent-project selection restores its MP3, all edited notes and timing");
        Check((await Js("document.getElementById('chartViewport').scrollLeft")).GetDouble() > 0, "Reopening scrolls to the saved notes instead of an empty song introduction");
        Invoke("OpenProject", elsewhere);
        await InvokeAsync("OpenSongFileAsync", paint.ChartPath);
        await WaitUntil(async () => (await Snapshot()).GetProperty("notes").GetArrayLength() == 5 && (await Js("document.getElementById('audio').readyState >= 1")).GetBoolean(), "Associated TMB reopen");
        Check(((SongProject)Field("_project")!).ProjectJsonPath == paint.ProjectJsonPath && (await Snapshot()).GetProperty("offset").GetDouble() == .5, "Open Song recognizes a previously saved TMB and restores its linked editable project and MP3");

        var persistedSettings = new ProjectService(settingsFile: Path.Combine(TestRoot, "studio-settings.json"));
        Check(persistedSettings.LastProjectFile == paint.ProjectJsonPath, "The last open song is remembered independently of creation and save history");
        var reopened = new MainWindow(persistedSettings, Game) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var reeditor = (WebView2)typeof(MainWindow).GetField("EditorWebView", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!;
        reopened.Show();
        await WaitUntil(() => Task.FromResult(typeof(MainWindow).GetField("_project", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened) is SongProject p && p.ProjectJsonPath == paint.ProjectJsonPath), "Startup project restore");
        await WaitUntil(async () => JsonDocument.Parse(await reeditor.CoreWebView2.ExecuteScriptAsync("document.getElementById('audio').readyState >= 1 && window.msmEditor.getSnapshot().notes.length === 5 && !document.getElementById('play').disabled")).RootElement.GetBoolean(), "Startup restored MP3 playback ready");
        Check(true, "A newly opened studio automatically restores the previous project, MP3 and painted chart with playback ready");
        reopened.Close(); reeditor.Dispose();

        var fallback = Projects.SaveCopyAs((SongProject)Field("_project")!, Path.Combine(TestRoot, "Level Recovery.msmproj"));
        fallback.Notes = []; Projects.Save(fallback);
        var recovered = Projects.LoadFile(fallback.ProjectJsonPath);
        Check(recovered.Notes.Count == 5 && recovered.SourceAudioFile == "source.mp3", "Projects recover their active difficulty when the top-level notes are empty but the saved level exists");
    }

    private static async Task RunNoteLengthChecks()
    {
        var fixture = new SongProject { Name = "Note Durations", ProjectFolder = Path.Combine(TestRoot, "lengths"), Bpm = 120, OffsetSeconds = .5 };
        Projects.Save(fixture); Invoke("OpenProject", fixture);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Duration fixture loaded");
        await Js("(() => {document.getElementById('tool').value='paint';document.getElementById('noteLength').value=.6;document.getElementById('snap').value=8;const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;v.scrollTop=44;})()");
        await Gesture(850, 292, 850, 292);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 1 && (await Snapshot()).GetProperty("notes")[0].GetProperty("length").GetDouble() == .6, "A click makes one note using the exact chosen length, independently of grid snap");
        await Gesture(1300, 358, 1302, 359);
        Check((await Snapshot()).GetProperty("notes")[1].GetProperty("length").GetDouble() == .6, "Small click jitter preserves the chosen note length");
        await Gesture(1200, 226, 1500, 358);
        var notes = (await Snapshot()).GetProperty("notes");
        Check(notes.GetArrayLength() == 3 && notes[2].GetProperty("time").GetDouble() == 4.5 && notes[2].GetProperty("length").GetDouble() == 1.25 && notes[2].GetProperty("pitch").GetInt32() == 63 && notes[2].GetProperty("endPitch").GetInt32() == 63, "A long diagonal drag draws one sustained marker at the initial pitch");
        await Key("z", true); await Key("y", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 3 && (await Snapshot()).GetProperty("notes")[2].GetProperty("length").GetDouble() == 1.25, "Sustained note creation undoes and redoes as one edit");
        await Gesture(1540, 270, 1180, 270);
        notes = (await Snapshot()).GetProperty("notes");
        Check(notes.GetArrayLength() == 4 && notes[3].GetProperty("time").GetDouble() == 4.5 && notes[3].GetProperty("length").GetDouble() == 1.25, "Drawing backwards creates one positive-duration marker spanning the interval");
        await Gesture(1600, 450, -80, 450);
        Check((await Snapshot()).GetProperty("notes")[4].GetProperty("time").GetDouble() == 0 && (await Snapshot()).GetProperty("notes")[4].GetProperty("length").GetDouble() == 6, "Reverse drawing clamps its start at zero while preserving positive duration");
        await Key("z", true);
        await Key("Escape");
        await Gesture(982, 292, 1042, 292);
        Check((await Snapshot()).GetProperty("notes")[0].GetProperty("time").GetDouble() == 3 && (await Snapshot()).GetProperty("notes")[0].GetProperty("length").GetDouble() == .75, "Dragging an existing note's right edge changes its length without moving its start");
        await Key("z", true);
        await Gesture(850, 292, 850, 292);
        await Js("document.getElementById('noteLength').value=.83;document.getElementById('applyLength').click()");
        notes = (await Snapshot()).GetProperty("notes");
        Check(notes[0].GetProperty("length").GetDouble() == .83 && notes[1].GetProperty("length").GetDouble() == .6 && notes[2].GetProperty("length").GetDouble() == 1.25, "Set length applies a precise duration only to the selected note");
        await Key("a", true);
        await Js("document.getElementById('noteLength').value=.4;document.getElementById('applyLength').click()");
        Check((await Snapshot()).GetProperty("notes").EnumerateArray().All(n => n.GetProperty("length").GetDouble() == .4), "Set length can resize the whole selected group in one action");
        await Key("z", true);
        notes = (await Snapshot()).GetProperty("notes");
        Check(notes[0].GetProperty("length").GetDouble() == .83 && notes[1].GetProperty("length").GetDouble() == .6 && notes[2].GetProperty("length").GetDouble() == 1.25, "Undo restores each group's previous individual note lengths");
        await Key("z", true);
        var beforeNoOp = await Snapshot();
        await Js("document.getElementById('noteLength').value=.6;document.getElementById('applyLength').click()");
        Check((await Snapshot()).GetProperty("revision").GetInt64() == beforeNoOp.GetProperty("revision").GetInt64(), "Applying the current length adds no redundant chart edit");
        await Key("Escape");
        Check((await Js("document.getElementById('applyLength').disabled")).GetBoolean(), "Set length is available only when notes are selected");
        await Js("document.getElementById('snap').value=0;document.getElementById('noteLength').value=.37");
        await Gesture(871.2, 320, 871.2, 320);
        notes = (await Snapshot()).GetProperty("notes");
        Check(Math.Abs(notes[4].GetProperty("time").GetDouble() - 3.13) < .001 && notes[4].GetProperty("length").GetDouble() == .37, "Snap Off allows exact click timing and arbitrary note duration");
        await Key("z", true);
        await Js("(() => {const n=document.getElementById('noteLength');n.value=-1;n.dispatchEvent(new Event('change'));document.getElementById('snap').value=8;})()");
        Check((await Js("Number(document.getElementById('noteLength').value)")).GetDouble() == .25, "Invalid note lengths normalize to a valid duration");
        var originalEditorHeight = Editor.Height;
        Editor.Height = 380;
        Window.UpdateLayout();
        await Js("window.dispatchEvent(new Event('resize'))");
        await WaitUntil(async () => (await Js("document.getElementById('chartViewport').clientHeight > 0 && document.getElementById('chartViewport').clientHeight < 250")).GetBoolean(), "Small chart viewport ready");
        await Js("document.getElementById('chart').addEventListener('pointerdown',e=>{window.lengthTrusted=e.isTrusted;},{once:true})");
        await BrowserMouse("mousePressed", 850, 341, true, "left");
        await BrowserMouse("mouseMoved", 1090, 341, true, "left");
        Check((await Js("window.lengthTrusted && window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 1, "Trusted left-button dragging stretches one active marker");
        await Js("document.getElementById('chart').dispatchEvent(new PointerEvent('lostpointercapture',{buttons:1,pointerId:1,bubbles:true}))");
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 1, "A held Paint stroke survives temporary pointer capture loss");
        await BrowserMouse("mouseReleased", 1090, 341, false, "left");
        notes = (await Snapshot()).GetProperty("notes");
        Check(notes.GetArrayLength() == 5 && notes[4].GetProperty("length").GetDouble() == 1 && notes[4].GetProperty("pitch").GetInt32() == 58, "Releasing a real left-button drag finishes one sustained note");
        await Key("z", true);
        Editor.Height = originalEditorHeight;
        Window.UpdateLayout();
        await Js("window.dispatchEvent(new Event('resize'))");
        await Js("document.getElementById('noteLength').value=.9");
        await Gesture(1600, 320, 1600, 320);
        await WaitUntil(() => Task.FromResult(Projects.LoadFile(fixture.ProjectJsonPath).Notes.Count == 5 && !(bool)Field("_dirty")!), "Note durations autosaved");
        Invoke("OpenProject", Projects.LoadFile(fixture.ProjectJsonPath));
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Sustained chart reloaded");
        notes = (await Snapshot()).GetProperty("notes");
        Check(notes.GetArrayLength() == 5 && notes.EnumerateArray().Any(n => n.GetProperty("pitch").GetInt32() == 63 && n.GetProperty("length").GetDouble() == 1.25) && notes.EnumerateArray().Any(n => n.GetProperty("pitch").GetInt32() == 59 && n.GetProperty("length").GetDouble() == .9), "Autosave and reopening preserve sustained and individually chosen note lengths");
        var saved = Projects.LoadFile(fixture.ProjectJsonPath);
        var midi = Path.Combine(saved.ProjectFolder, "duration.mid"); new MidiService().Write(saved, midi);
        Check(new TmbService().ReadTmb(saved.ChartPath).Notes.Any(n => n.Pitch == 59 && Math.Abs(n.Length - .9) < .001) && new MidiService().Read(midi).Any(n => n.Pitch == 59 && Math.Abs(n.Length - .9) < .002), "TMB and MIDI exports preserve chosen note durations");
        await Js("document.getElementById('noteLength').value=.25");
    }

    private static async Task RunRightDragChecks()
    {
        var fixture = new SongProject { Name = "Continuous Erase", ProjectFolder = Path.Combine(TestRoot, "eraser"), Bpm = 120, OffsetSeconds = .5 };
        for (var i = 0; i < 5; i++) fixture.Notes.Add(new() { Time = 3 + i * .25, Length = .02, Pitch = 60, EndPitch = 60 });
        fixture.Notes.Add(new() { Time = 3.5, Length = .1, Pitch = 61, EndPitch = 61 });
        for (var i = 0; i < 5; i++) fixture.Notes.Add(new() { Time = 5 + i * .25, Length = .02, Pitch = 64 - i, EndPitch = 64 - i });
        fixture.Notes.Add(new() { Time = 5.5, Length = .1, Pitch = 69, EndPitch = 69 });
        var originalIds = fixture.Notes.Select(n => n.Id).Order().ToArray();
        Projects.Save(fixture); Invoke("OpenProject", fixture);
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")!, "Erase fixture loaded");
        await Js("(() => {document.getElementById('tool').value='paint';const z=document.getElementById('zoom');z.value=240;z.dispatchEvent(new Event('input'));const v=document.getElementById('chartViewport');v.scrollLeft=480;v.scrollTop=44;})()");
        await Key("a", true);
        await Pointer("pointerdown", 800, 292, button: 2);
        await Pointer("pointermove", 1150, 292, button: 2);
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 7, "Holding right-drag continuously erases short notes between sparse pointer events before release");
        await Pointer("pointerup", 1150, 292, button: 2); await ContextMenu(1150, 292);
        var remaining = (await Snapshot()).GetProperty("notes");
        Check(remaining.GetArrayLength() == 7 && remaining.EnumerateArray().Any(n => n.GetProperty("time").GetDouble() == 3.5 && n.GetProperty("pitch").GetInt32() == 61), "Horizontal right-drag erases every crossed note with scroll and offset, keeping the adjacent row");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).Order().SequenceEqual(originalIds) && await SelectedCount() == 12, "One Undo restores all erased notes with original IDs and selection");
        await Key("y", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 7, "One Redo repeats the entire continuous erase stroke");
        await Key("z", true);
        await Js("document.getElementById('tool').value='select'");
        await Pointer("pointerdown", 1200, 292, button: 2);
        await Pointer("pointermove", 800, 292, button: 2);
        await Pointer("pointerup", 800, 292, button: 2); await ContextMenu(800, 292);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 7, "Reverse right-drag also continuously erases in Select mode");
        await Key("z", true);
        await Js("document.getElementById('tool').value='paint'");
        await Pointer("pointerdown", 1324, 209, button: 2);
        Check(await SelectedCount() == 11, "An erase stroke immediately removes the note under its initial right-click");
        await ContextMenu(1324, 209);
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 11, "A context-menu event on right press does not end the stroke or erase another note");
        await Pointer("pointermove", 1564, 297, button: 2);
        await Pointer("pointerup", 1564, 297, button: 2); await ContextMenu(1564, 297);
        remaining = (await Snapshot()).GetProperty("notes");
        Check(remaining.GetArrayLength() == 7 && remaining.EnumerateArray().Any(n => n.GetProperty("pitch").GetInt32() == 69) && remaining.EnumerateArray().All(n => n.GetProperty("time").GetDouble() < 5 || n.GetProperty("pitch").GetInt32() == 69), "Diagonal right-drag hits all crossed short notes and preserves notes outside its path");
        await Key("z", true);
        var beforeCancel = await Snapshot();
        await Pointer("pointerdown", 800, 292, button: 2);
        await Pointer("pointermove", 1150, 292, button: 2);
        await Pointer("pointercancel", 1150, 292, button: 2); await ContextMenu(1150, 292);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 12 && (await Snapshot()).GetProperty("revision").GetInt64() == beforeCancel.GetProperty("revision").GetInt64() && await SelectedCount() == 12, "Cancelling an erase stroke restores every note and selection without recording an edit");
        await Pointer("pointerdown", 800, 350, button: 2);
        await Pointer("pointermove", 1200, 350, button: 2);
        await Pointer("pointerup", 1200, 350, button: 2); await ContextMenu(1200, 350);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 12 && (await Snapshot()).GetProperty("revision").GetInt64() == beforeCancel.GetProperty("revision").GetInt64(), "An erase stroke through empty space adds no notes or undo entries");

        await Js("document.getElementById('chart').addEventListener('pointerdown',e=>{window.eraseTrusted=e.isTrusted;},{once:true})");
        var eraseRect = await Js("(() => {const r=document.getElementById('chart').getBoundingClientRect();return {top:r.top,height:r.height};})()");
        await BrowserMouse("mousePressed", 800, 292, true);
        await BrowserMouse("mouseMoved", 1150, 292, true);
        Check((await Js("window.eraseTrusted && window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 7, "Trusted browser right-button input continuously erases while held");
        var duringErase = await Js("(() => {const r=document.getElementById('chart').getBoundingClientRect();return {top:r.top,height:r.height};})()");
        Check(eraseRect.GetProperty("top").GetDouble() == duringErase.GetProperty("top").GetDouble() && eraseRect.GetProperty("height").GetDouble() == duringErase.GetProperty("height").GetDouble(), "Changing selection and overlap counts does not move or resize the canvas during a drag");
        await Js("document.getElementById('chart').dispatchEvent(new PointerEvent('lostpointercapture',{buttons:2,pointerId:1,bubbles:true}))");
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 7, "A held right-drag erase stroke survives temporary pointer capture loss");
        await BrowserMouse("mouseMoved", 1150, 20, true);
        Check((await Js("window.msmEditor.isInteracting()")).GetBoolean() && await SelectedCount() == 7, "An erase stroke stays active when the pointer leaves the chart");
        await BrowserMouse("mouseReleased", 1150, 20, false);
        Check(!(await Js("window.msmEditor.isInteracting()")).GetBoolean() && (await Snapshot()).GetProperty("notes").GetArrayLength() == 7, "Trusted right-button release outside the chart finishes the complete erase stroke");
        await Key("z", true);
        Check((await Snapshot()).GetProperty("notes").GetArrayLength() == 12, "A real browser mouse erase gesture restores in one Undo");

        await InvokeAsync("SaveChartFromEditorAsync");
        await Js("(() => {const b=document.getElementById('bpm');b.focus();b.value=144;b.dispatchEvent(new Event('change'));})()");
        await WaitUntil(() => Task.FromResult((bool)Field("_dirty")!), "Pending edit before held erase");
        await Pointer("pointerdown", 800, 292, button: 2);
        await Pointer("pointermove", 1150, 292, button: 2);
        await Task.Delay(900);
        Check(!await (Task<bool>)Invoke("TryAutoSaveAsync")! && (await Js("window.msmEditor.isInteracting()")).GetBoolean() && Projects.LoadFile(fixture.ProjectJsonPath).Notes.Count == 12, "Autosave waits for a held erase stroke and keeps the previously saved complete chart");
        await Pointer("pointerup", 1150, 292, button: 2); await ContextMenu(1150, 292);
        await WaitUntil(() => Task.FromResult(Projects.LoadFile(fixture.ProjectJsonPath).Notes.Count == 7 && !(bool)Field("_dirty")!), "Erase stroke automatically saved");
        Check(Projects.LoadFile(fixture.ProjectJsonPath).Bpm == 144, "Finishing an erase stroke automatically persists all deletions and pending timing settings");
        Invoke("OpenProject", Projects.LoadFile(fixture.ProjectJsonPath));
        await WaitUntil(async () => (await Snapshot()).GetProperty("projectKey").GetString() == (string)Field("_projectKey")! && (await Snapshot()).GetProperty("notes").GetArrayLength() == 7, "Erased chart reopened");
        Check(true, "Reopening the project retains the whole continuously erased chart");
    }

    private static void WriteWave(string path)
    {
        const int rate = 22050, samples = rate * 5;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8.ToArray()); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8.ToArray()); writer.Write(samples * 2);
        for (var i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 3000));
    }
}
