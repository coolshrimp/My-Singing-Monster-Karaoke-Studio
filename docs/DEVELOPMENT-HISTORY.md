# My Singing Monster Karaoke Studio

Windows 11 desktop prototype for creating, editing, and exporting custom rhythm-game song packages for the My Singing Monsters song/chart workflow.

## Goal

The finished app is intended to make the workflow as simple as:

`MP3 -> trim -> OGG -> detect/create notes -> edit chart -> TMB -> ZIP -> import into game`

Version 0.10.8 reduces distribution size with compressed single-file packaging and separately downloaded FFmpeg. Version 0.10.3 adds a playable instrument tone with continuous mouse-controlled pitch in Play Test. Version 0.10.2 places Chart Info in the song header and recent-project menu, with colored sync statuses and a visible Manage Game Songs button. Version 0.10.0 introduced configurable Chart Info, content-based game sync status, an online library/download manager, and Preview / Play Test modes. It includes direct game import and a game-song manager with removal into backup. Automatic melody curves, matching TMB/OGG package names, Save OGG As, the recent-project menu, freehand drawing, difficulty levels, playback, trimming and autosave remain available.

---

## Current MVP Features

- Windows 11 WPF/.NET desktop application
- Web-style HTML/CSS/JavaScript chart editor hosted with Microsoft WebView2
- Web editor assets embedded into the published EXE
- Local song/project library
- Open MP3 or other audio directly and create its project automatically
- Audio playback, actual waveform display, click-to-seek, volume, zoom, and follow-playhead controls inside the chart editor
- BPM and timing offset controls
- Note editor:
  - add notes
  - move notes
  - resize notes
  - delete notes
  - draw smooth freehand curves as single editable notes
  - turn the grid off for continuous timing and pitch placement
  - remove overlapping notes for a single mouse
- Save chart/project data locally, including before switching projects or closing
- Session undo/redo and selected-note deletion
- FFmpeg downloads on first setup with checksum verification; installed copies are reused for MP3/audio -> OGG conversion
- TMB chart generation with beat timing, slides, metadata and per-level identifiers
- Export ZIP structure containing `<song name>.ogg` and `<song name>.tmb` at the archive root
- Save converted OGG audio to a chosen filename and folder
- Right-click recent projects to open, rename, save, reveal the folder, remove an entry or refresh
- Import/update the selected level directly into My Singing Monsters Karaoke
- See installed difficulties in the current song and recent-project list
- Manage existing game imports and remove songs into a recoverable backup
- Self-contained Windows x64 single-file publish configuration

---

## Not Finished Yet

The following are planned but are not complete in this MVP:

- More accurate melody extraction and musical difficulty generation (density-based Easy/Normal/Hard/Expert levels are available)
- Polyphonic melody separation (the automatic draft currently follows dominant pitches)
- Dragging trim handles directly on the waveform (time-range trimming is available)
- Persistent revision/history snapshots (session undo/redo is available)
- A full import/playthrough check inside My Singing Monsters Karaoke
- Updates to the pinned FFmpeg download version
- Built-in updater

The export uses the five-number TMB note layout documented in TromboneCharter and the TC Chart Converter. My Singing Monsters Karaoke is the intended target based on the requested TMB/OGG workflow. Generated files pass format and timing checks; actual in-game import and playback have not been tested here.

---

# Fastest Way to Build the EXE

## Requirements on the BUILD computer

Use a Windows 11 x64 PC with:

1. Visual Studio 2022 **or** the .NET 8 SDK
2. Internet access for the first NuGet restore
3. Microsoft Edge WebView2 Runtime

Windows 11 normally already has WebView2 installed.

The final published app is configured as **self-contained**, so users who only run `MSMSongStudio.exe` do **not** need the .NET runtime installed.

---

## Option 1 - One-click build

Extract this ZIP to a normal folder such as:

```text
C:\MSMSongStudio\
```

Then double-click:

```text
BUILD-EXE.cmd
```

The script runs the included PowerShell publisher.

When successful, the EXE will be here:

```text
publish-win-x64\MSMSongStudio.exe
```

You can copy MSMSongStudio.exe by itself to another Windows 11 x64 computer. The audio converter downloads on first setup and is then reused from its local installation.

---

## Option 2 - Build from Terminal

Open PowerShell or Windows Terminal in the extracted project folder and run:

```powershell
 dotnet restore .\MSMSongStudio\MSMSongStudio.csproj
 dotnet publish .\MSMSongStudio\MSMSongStudio.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish-win-x64
```

The finished application should be:

```text
publish-win-x64\MSMSongStudio.exe
```

---

# FFmpeg Setup

FFmpeg is installed separately. Click Install FFmpeg in the sidebar, or double-click INSTALL-FFMPEG.cmd. Audio conversion and export also install it automatically when needed. First setup requires internet access and downloads the pinned Gyan release from its official GitHub mirror, with gyan.dev as a fallback, over HTTPS. Both the archive and extracted executable are verified against SHA-256 hashes. Setup requires no administrator access or system PATH changes. Verified existing installations are reused without downloading.

The application keeps its checksum-verified copy and license/source notices under %LOCALAPPDATA%\MSM Song Studio\Tools\FFmpeg\9.0.2. Check FFmpeg verifies or repairs the installed copy. The app embeds only the small license/source notices. Tools/ffmpeg.exe is no longer bundled; the retained development copy is used by legacy conversion tests.

---

# Project Layout

```text
MSM-Song-Studio-MVP\
|
|-- BUILD-EXE.cmd
|-- build-win11.ps1
|-- README.md
|
`-- MSMSongStudio\
    |-- MSMSongStudio.csproj
    |-- App.xaml
    |-- App.xaml.cs
    |-- MainWindow.xaml
    |-- MainWindow.xaml.cs
    |
    |-- Models\
    |   `-- SongProject.cs
    |
    |-- Services\
    |   |-- ExportService.cs
    |   |-- FfmpegService.cs
    |   |-- ProjectService.cs
    |   |-- TmbService.cs
    |   `-- WebEditorAssets.cs
    |
    |-- Tools\
    |   `-- README.txt
    |
    `-- WebEditor\
        |-- index.html
        |-- editor.js
        `-- style.css
```

---

# Single-EXE Configuration

The project is already configured for:

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
```

This means the normal release target is one main application file:

```text
MSMSongStudio.exe
```

Some native components may be extracted internally by .NET while the app runs. That is normal for self-contained single-file desktop applications.

---

# First Test Checklist

After building:

1. Launch `MSMSongStudio.exe`.
2. Confirm the main window opens.
3. Create a new song/project.
4. Import a small MP3 test file.
5. Confirm audio playback works.
6. Change BPM and timing offset.
7. Add several chart notes.
8. Move and resize notes.
9. Delete a note.
10. Save the project.
11. Close and reopen the app/project.
12. Confirm the chart is restored.
13. Test OGG conversion if `ffmpeg.exe` is available.
14. Export the song package ZIP.
15. Inspect the ZIP for matching `<song name>.ogg` and `<song name>.tmb` filenames.

If a step fails, note exactly which step and any error text shown.

---

# Development Direction

The intended production pipeline is:

```text
Imported MP3/WAV/OGG
        |
        +--> trim/crop audio
        |
        +--> FFmpeg --> song.ogg
        |
        +--> transcription engine --> detected notes/MIDI
                                   |
                                   +--> clean/quantize
                                   |
                                   +--> difficulty generator
                                   |
                                   `--> chart editor
                                              |
                                              `--> song.tmb

song.ogg + song.tmb --> validation --> ZIP export
```

The user should ultimately be able to press one button to generate a playable first draft, then manually correct the chart in the visual editor.

---

# Suggested Next Build Priorities

1. Verify the exact working TMB schema using a known-good game chart.
2. Add real waveform display and trimming.
3. Add bundled/automatic FFmpeg setup.
4. Add automatic audio transcription.
5. Convert detected pitches into playable chart notes.
6. Add Easy / Normal / Hard / Expert generation.
7. Add chart validation before export.
8. Add proper revision/history snapshots.

---

## Important Testing Note

This is an MVP/development build, not a finished game-content authoring release. Keep copies of original audio and working chart files while testing.

## Development update — September 29, 2026

- Fixed missing System.IO imports that prevented compilation.
- Corrected note placement, dragging, resizing, and deletion after scrolling.
- Save, Build, and Export now retrieve the current editor chart directly, replacing a fixed-delay save request.
- Build status now identifies output as a draft; game compatibility remains unverified.
- FFmpeg is now embedded in the EXE and is installed automatically in the app user folder.

Release build and self-contained publish are checked during development. Playback is now covered by the real WebView2 workflow tests; game import still requires format verification. Next: obtain a known-good target-game chart to verify the TMB schema, then implement real waveform trimming.


## Playback and function repairs — September 29, 2026

- Local audio now streams through the host with content types and byte-range support, replacing virtual-folder media loading.
- The editor waits for project/audio readiness and shows the actual duration and useful playback errors.
- Real waveform decoding, click-to-seek, volume, zoom, and follow-playhead controls.
- Note selection, session undo/redo, deletion, pointer capture for dragging, and correct scrolled coordinates.
- Timing offset now aligns notes and beat lines with audio; notes retain their chart-relative times when the offset changes.
- The chart canvas only draws the visible area, keeping long songs responsive.
- Save retrieves the current editor state; switching projects and closing save pending changes. Imports preserve notes and invalidate previous OGG output.
- Conversion writes a temporary OGG, preserving existing output on failure. Export automatically converts audio when needed.
- Buttons report errors and prevent conflicting operations. Draft chart output is still not a verified game format.
- Project/chart JSON writes are replaced atomically; repeated exports use distinct names.

### Running checks

Run `dotnet run --project .\Tests\MSMSongStudio.SmokeTests.csproj -c Release` to exercise the real WebView2 editor, playback, waveform, seek, undo/redo, save/reload, byte ranges, FFmpeg conversion, and ZIP export. Tests generate audio and project files in their own output directory. They do not change your song library.

Automatic transcription, audio trimming, multiple difficulties, and a verified game TMB schema remain planned work.


Latest validation: 56 workflow checks passed against the repaired application, including real WebView2 playback and waveform decoding, pause/stop/seek, notes after scrolling, undo/redo/delete, save/reload, stale-project protection, byte-range requests, real FFmpeg OGG conversion, preserving output after failed conversion, and repeated ZIP exports. Release compilation reported zero warnings and zero errors; the self-contained Windows x64 publish completed successfully.


## Multi-note selection

Choose **Select** in the editor toolbar, then click and drag from empty chart space to draw a selection box. **Paint** is the default tool: a click creates one note using **Length (s)**; dragging creates one sustained note at the initial pitch. Hold Shift while dragging from empty space to select notes even in Paint mode. Selected notes are highlighted and the toolbar shows their count.

- Shift-click toggles individual notes; Shift-drag adds a box to the selection.
- Drag a selected note to move the entire selection while preserving relative timing and pitches.
- Drag a selected right edge to resize the selected notes by the same amount.
- Delete or the Delete toolbar button removes the selection. Ctrl+A selects all; Escape clears it.
- Undo/redo treats a group edit as one operation and restores the selection.
- Right-click erases only the note under the pointer in either Paint or Select; Delete removes the selected group. Single-note erasing supports Undo/Redo and automatic saving.
- Hold the right mouse button and drag to continuously erase notes along the path. A whole erase stroke is one Undo/Redo operation.

Verified in the real WebView2 editor: multi-selection, reverse boxes, scroll and timing offset coordinates, group movement/resizing/deletion, additive selection, boundary clamping, cancelled drags, and clicks on unsnapped notes. All 45 workflow checks passed; Windows x64 publish completed.


## One-click audio converter setup

Use the Install FFmpeg sidebar button, or double-click INSTALL-FFMPEG.cmd. The standalone installer opens a small progress window and reports completion. The updated EXE can be copied alone; no Tools folder is required at runtime.

The converter is installed offline from the embedded, checksum-verified FFmpeg 9.0.2 executable. Conversion and export also perform setup automatically. Existing verified installations are reused, damaged copies are repaired, and interrupted or invalid extractions leave no partial executable. Concurrent setup requests are serialized. The installer keeps license and source notices with the converter and does not change system PATH.

Validation: all 56 workflow checks passed. The published EXE was copied into an otherwise empty directory and successfully installed FFmpeg with no external Tools folder. The installed executable's checksum matched the embedded release and its version command ran successfully. Windows x64 publish completed.

## Studio workspace update — September 29, 2026

The studio now has a consistent dark desktop workspace with File, Edit, Playback, View, and Tools menus. Actions use the same commands in the menus, buttons, and keyboard shortcuts. The header shows the project file, note count, audio name, and saved/unsaved state. A progress indicator appears during file operations.

### Projects and chart files

- **New Song…** asks where to create an editable `.msmproj` file. Its companion `<song>.assets` folder holds the imported audio, converted OGG, and working chart. Keep the project file and its assets folder together when moving a song.
- **Open Project…** opens `.msmproj` files or older `project.json` files. **File → Open Project Folder…** also supports the older folder-based projects.
- **Save Project** saves the working notes and timing settings. Switching songs and closing also save pending changes.
- **Save Project As…** creates a separate project and copies its audio and chart into a companion assets folder.
- **Save Chart As…** opens the native Windows Save As dialog every time, including the editor Save button and Ctrl+S. Choose a `.tmb` filename and folder; existing files require overwrite confirmation. Cancel returns to the editor without saving. The dialog remembers the last chart filename and location for that project.
- **Open Chart…** loads an MSM Studio draft chart into the active song while retaining its audio, or creates a song if none is open. Other chart schemas are rejected. These charts still use the studio's draft schema; target-game compatibility is unverified.
- **Save OGG As…** opens the native Windows Save As dialog. Choose a `.ogg` filename and folder; existing files require overwrite confirmation. If needed, the audio is converted automatically after choosing a destination. The last destination is remembered and the project's working audio stays linked. Cancel leaves the audio unchanged.
- **Export ZIP…** asks for the archive filename and location, then saves the current notes, converts audio if needed, and writes `<song name>.ogg` plus `<song name>.tmb` at the archive root. Both use the project title; invalid filename characters are replaced consistently. For example, `My Melody.zip` contains `My Melody.ogg` and `My Melody.tmb`.

### Folder buttons

**Project Folder** opens the active song's audio and working chart folder. **Working Folder** opens the library used for new songs. **Studio Folder** opens the folder containing the running application. **Change…** lets you choose a different working folder; the choice and recent project files persist across restarts. Recent songs saved outside the working folder remain available in the sidebar.

| Shortcut | Action |
| --- | --- |
| Ctrl+N | New song |
| Ctrl+O | Open project |
| Ctrl+S | Save Chart As |
| Ctrl+Shift+S | Save Project As |
| Ctrl+Alt+S | Save Project |
| Ctrl+Z / Ctrl+Y | Undo / redo |
| Ctrl+A / Escape | Select all notes / clear selection |
| Delete | Delete selected notes |
| Space in the editor | Play / pause |

Validation: 72 checks passed using the real WebView2 editor, including named project save/reopen, companion audio relocation, persisted working-folder preferences and recents, chart Save As destinations, native Edit/Playback commands, chart validation, and chosen ZIP destinations. Both chart-save buttons were also checked in the published application against the actual Windows Save As dialog; saving produced a valid chart file and cancellation returned to the editor. The self-contained Windows x64 release is available under `publish-win-x64`.

## MP3 pipeline repair and first-draft generation — September 29, 2026

Use **Import MP3 / Audio…** from the welcome screen; creating a project first is no longer required. **Open Song / Project…** and Ctrl+O also accept MP3 and other supported audio files. Opening audio creates a named `.msmproj` and companion assets folder in the working library, preserves the original file, loads the waveform and playback controls, and automatically generates notes for all four difficulties. Importing another audio file creates a separate song when the current song already contains audio or notes. To continue earlier edits, open its `.msmproj` file or click the song once in Recent Projects. The studio restores the last open project on startup.

The numbered buttons support one action per step:

1. **Import MP3…** chooses audio, prepares the song in the library, converts OGG and generates editable notes and MIDI automatically.
2. **Convert OGG** converts the selected audio and reports where the OGG was saved.
3. **Notes / MIDI** detects a dominant-pitch first draft, writes a standard single-track MIDI file, reads MIDI note events back into the chart, and places editable notes against the audio. Existing notes require confirmation before replacement.
4. **Build Song** runs conversion and note drafting when needed and writes OGG, MIDI and the TMB chart. Existing edited notes are retained and used to rebuild MIDI.
5. **Export ZIP…** runs missing build steps and lets you choose the ZIP filename. The game ZIP contains `<song name>.ogg` and `<song name>.tmb` at its root. `song.mid` stays in the project assets folder.

**Trim Audio:** enter start and end times in seconds, then click Trim Audio. The studio keeps that portion of the audio, crops and realigns notes in all difficulty levels, clears the old MIDI output, and retains the original audio file in the project folder. The next build/export recreates MIDI from the trimmed chart.

**Levels:** choose Easy, Normal, Hard or Expert. Each level keeps separate notes in the project. Imports generate all levels from the detected audio notes. **↻ Auto Chart** beside the selector regenerates only the selected level from the current audio; it asks before replacing existing notes and preserves the other difficulties. A missing level starts from the current chart with reduced note density where appropriate. Switching levels saves current edits. Each TMB/ZIP exports the selected level, with a numeric game difficulty and its own chart identifier. All levels stay together in the `.msmproj` file.

The pitch detector is a lightweight local first-draft tool. It follows the strongest spectral pitch, groups sustained frames into notes, rejects quiet audio, and maps detected octaves into the visible editor range. Full song mixes, chords, vocals over accompaniment and percussion need manual correction. It does not perform full polyphonic instrument separation or promise accurate melody extraction. No external AI service or account is required.

The placeholder studio chart schema has been replaced with the TMB layout used by TromboneCharter and TC Chart Converter. Earlier studio draft charts still open and are upgraded on save. Real TMB charts with a constant tempo can be opened; timing is converted from beats to seconds and slides are retained. Variable-tempo charts are not supported. Imported optional lyrics/background/improv metadata is preserved but is not edited or retimed by the audio trimmer. Actual MSM Karaoke import and gameplay still need a check in the game. This release is the Windows studio with an embedded web chart editor; it is not yet a standalone browser deployment.

Validation: 102 real WebView2, chart-format and file workflow checks passed, including MP3 selection without a project, real MP3 playback, automatic OGG/MIDI/chart/ZIP creation, known-pitch detection, difficulty isolation, keeping MIDI/audio/levels in project copies, trimming playback to the requested duration, offset/slide cropping, preserving edited notes during export, and keeping source audio and earlier projects intact.

## TMB export format — version 0.5

The former `msm-song-studio-draft-1` JSON was a project draft, not a game chart. Version 0.5 exports the TMB structure used by [TromboneCharter](https://github.com/towai/TromboneCharter/blob/master/tmb_info.gd) and [TC Chart Converter](https://tc-chart-converter.github.io/). The developer advertises fan-chart imports for [My Singing Monsters Karaoke](https://store.steampowered.com/app/4633140/My_Singing_Monsters_Karaoke/).

A note row is `[startBeat, lengthBeats, startPitch, pitchDelta, endPitch]`. Studio seconds become beats with `seconds * BPM / 60`. The audio offset is baked into note start times. Pitch values use `(MIDI pitch - 60) * 13.75`. Slides retain start/end pitches, and notes crossing a negative offset are cropped at audio time zero. The chart includes name, shortName, trackRef, author, year, genre, description, difficulty, tempo, timesig, savednotespacing, endpoint, lyrics and UNK1. Notes outside MIDI pitches 47–73 are rejected at export.

Easy/Normal/Hard/Expert map to game ratings 2/5/8/10. Each level has a stable identifier; a separate saved project copy gets a new identifier. ZIP files contain only the selected level's `<song name>.tmb` and `<song name>.ogg`; MIDI remains available locally for further editing.

Validation includes an independent known TMB note-layout fixture, positive and negative offset handling, slide conversion, numeric metadata and endpoint checks, legacy studio-chart migration, and MIDI/audio alignment. Format checks do not constitute a game runtime test.

The published version was also checked through the actual Windows Open Song and Export Song dialogs: an MP3 opened with playback enabled, and one-click export produced a ZIP containing valid TMB note rows and OGG audio. Export keeps the saved ZIP location visible after audio reloads.

## Painting, project restoration and automatic imports — version 0.6

- **Paint:** click or drag through empty cells to add a continuous row or melody. Fast mouse movement is interpolated to fill crossed cells, and backtracking skips occupied cells. Each stroke is one Undo/Redo operation. Pointer cancellation discards an unfinished stroke. Existing notes can still be selected, moved or resized.
- **Select:** drag a selection box; Shift-drag also works from empty space while Paint is selected. Existing group movement, resizing and deletion remain available.
- **Automatic saving:** notes and timing settings are saved after a short pause. Saving waits until the mouse stroke ends. Revision checks prevent an older save acknowledgement from clearing newer unsaved edits. Save Chart As still lets you choose an external TMB filename.
- **Reopen:** the studio loads the last open project on startup. One click in Recent Projects opens its saved MP3, notes and difficulty. The editor scrolls to the first saved notes. Open Song recognizes a previously exported TMB when its saved project is available in the library/history, and restores that project's audio and all levels. A standalone foreign TMB remains a chart-only import.
- **New audio:** each import automatically converts OGG, detects notes, writes MIDI and creates Easy/Normal/Hard/Expert charts. Importing into an occupied song creates a new project, preserving earlier work. Recordings with no detectable melody remain imported for manual editing.
- **Refresh:** use **↻ Auto Chart** beside Level to regenerate that level from song audio. Other difficulties are retained. Review generated notes, especially for full song mixes.

Validation: 128 real WebView2 and file workflow checks passed, including continuous horizontal/reverse/diagonal painting, single-stroke undo/redo, cancellation, Shift selection, saving during a live stroke, delayed save acknowledgements, automatic persistence, one-click history reopening, restoring MP3/notes on a fresh studio startup, associated TMB reopening, automatic charts for successive imports, per-difficulty refresh isolation and accurate audio-ready status. Release compilation completed with zero warnings and errors. Tests use isolated generated songs and do not change the user's song library.

### Right-click erasing — version 0.6.1

Right-click a note to erase it immediately in Paint or Select. This erases just the note under the pointer, even when the entire painted stroke is selected. Delete still removes the selected group. Erasing is undoable, supports redo and is saved automatically. Right-clicking empty chart space leaves the chart unchanged.

Validation: 134 workflow checks passed, including Paint-mode right-button press/release, retaining other selected notes, single-note undo/redo, empty-space right-clicks and Select-mode erasing. The published Windows x64 build includes the updated editor.

### Continuous right-drag erasing — version 0.6.2

Hold the right mouse button and drag over notes to erase continuously in Paint or Select. Start on a note or empty space; fast horizontal, reverse and diagonal strokes remove every crossed note, including short notes between pointer events. The stroke continues while dragging outside the chart and finishes when the right button is released. The browser context menu is suppressed during erasing. A single right-click still erases one note.

One Undo restores the entire erase stroke and its prior selection; Redo repeats it. Escape, pointer cancellation or loss of capture without a held button restores an unfinished stroke. A held stroke continues through temporary capture loss and finishes on release. Automatic saving waits for release, then saves the complete edit. Empty strokes make no chart change.

Validation: 151 workflow checks passed, covering single clicks, continuous erasing in both tools, short notes between sparse pointer events, adjacent-row preservation, diagonal/reverse paths, context-menu events during the stroke, grouped Undo/Redo, cancellation, automatic saving after release and reopening the erased chart. Trusted browser mouse input also verifies held-button erasing and releasing outside the chart. Release build and self-contained Windows x64 publish completed.

### Sustained drawing and adjustable lengths — version 0.6.3

- **Click:** choose **Length (s)**, then click empty chart space to add one note with that duration. The default is 0.25 seconds. This value is independent of grid snap.
- **Drag:** in Paint, press in empty space and drag horizontally to create one sustained marker. Its start and end follow the grid snap, and it keeps the pitch where drawing began. Drawing backwards works too. Each draw is one Undo/Redo edit.
- **Resize:** drag the white right edge of an existing note to shorten or extend it. The cursor changes to a horizontal resize arrow over this edge. Set Snap to Off for free timing.
- **Exact size:** select a note, enter its desired duration in **Length (s)**, then click **Set length**. Multiple selected notes can be set together; Undo restores their individual previous durations. Changing the input alone changes the size of future clicks.
- Continuous right-drag erasing, group selection, saving, reopening and exports remain available. Previously saved notes keep their existing durations.

Validation: 170 workflow checks passed, including single sustained-marker drawing, independently sized click notes, click jitter, reverse drawing and time-zero clamping, edge resizing without moving the start, exact and grouped length changes, no-op length changes, free timing, invalid-length normalization, trusted left-button drawing, autosave/reopening and preserving durations in TMB and MIDI files. Build completed with zero warnings and errors; Windows x64 publish completed.

### Freehand curves and single-mouse charts — version 0.7.0

- **Curve:** press in empty chart space and drag along the desired mouse path. The entire stroke becomes one note, with a smooth rise/fall trajectory and continuous pitch. Draw forwards or backwards; backtracking redraws the tail so there is only one position at each moment. A single click uses Length (s). Each curve is one Undo/Redo operation.
- **Grid:** uncheck Grid to hide pitch rows and beat lines and disable snapping. Paint and note movement then use continuous timing and pitch. Grid remains available for conventional editing, and its setting is saved with the song. Curve drawing always follows the mouse freely.
- **Editing:** drag the body of a curve to move all its points. Drag the bright handle at its end to shorten/extend the duration, or use Set length. Resizing scales the curve in time while keeping its shape. Select boxes and right-drag erasing follow the actual path, including turns away from the starting row.
- **Automatic charts:** MP3 import and Auto Chart generate one mouse line for every difficulty. Simultaneous targets are reduced to one note; a sustain that reaches into the next note is shortened at that note's onset. Density still depends on difficulty. Generation does not change other saved levels.
- **Existing overlaps:** the toolbar shows an overlap count. Remove overlaps trims sustained notes and removes simultaneous targets in the current chart. Undo restores the complete previous chart. Existing songs are not automatically rewritten when reopened.
- **Saving/export:** .msmproj retains the full curve and fractional pitch positions across autosave, reopening, difficulty changes, copying and audio trimming. Standard five-value TMB rows support straight slides, so export represents a curve with joined non-overlapping slide segments. A standalone TMB opens those segments as separate editable notes; opening the associated studio project restores the original single curve. MIDI remains a discrete-pitch draft and does not store freehand curves. Actual in-game handling of joined segments still needs a game playthrough.

Validation: 218 isolated workflow checks cover freehand drawing with trusted browser mouse input, fractional timing/pitch, moving/resizing/selection/erasing curved paths, reverse strokes, cancellation, curve persistence and trimming, overlap-free difficulty generation, MIDI/TMB timing, invalid-curve rejection, held gestures through capture loss, and all existing studio workflows. Tests do not modify the user's song library.

### Audio-following Auto Chart curves — version 0.8.0

**Auto Chart** and new audio imports now keep continuous pitch movement from the decoded song. Phrases with changing pitch become editable curves, while a steady tone remains a flat note. Detected attacks place note starts against the waveform. Real silence ends a phrase; large pitch jumps create separate notes. Each difficulty retains a single non-overlapping mouse path, with less curve detail and fewer starts on easier levels.

Gold ticks along the bottom of the waveform mark the detected attacks. These include musical note attacks and percussion; they are not a guaranteed beat grid. When a new import has a sufficiently regular pulse, its BPM starts from the estimated tempo. Refreshing an existing level retains the user's BPM and offset and preserves every other saved level.

The MIDI file remains a discrete-pitch reference. The editor and game export now use the continuous audio analysis directly, so a MIDI round-trip cannot discard generated curves. Curves, timing markers and the tempo estimate survive project saving/copying; trimming realigns the markers as well as the chart. Existing saved charts are not regenerated during app updates. Click **↻ Auto Chart** to generate the new patterns for the selected level; the normal overwrite prompt still applies.

The local analyzer uses centered pitch-spectrum frames, tonal-confidence filtering, pitch smoothing and attack detection from short-time energy rises and positive spectral changes. The onset approach follows the spectral-flux/local-frequency-filtering principle described in [librosa's onset-strength documentation](https://librosa.org/doc/0.11.0/generated/librosa.onset.onset_strength.html). No additional audio-analysis package or online service is required. Full music mixes still need review because the detector follows the dominant audible pitch rather than separating every instrument or isolating the lead singer.

Validation: 242 studio checks passed, including a known curved melody with half-second beats, pitch-contour accuracy, rests, abrupt jumps, steady tones, silence/noise rejection, MP3 auto-import, all four levels, visible waveform markers, saving, per-level refresh, trimming and ZIP export. Nine additional checks passed on a separate copy of the selected song; its Normal analysis produced 445 notes including 83 curves, with no overlaps in the chart or exported TMB. Tests did not regenerate or replace the user's working chart. Actual game playback of joined TMB slide segments remains unverified.

### Song filenames and recent-project menu — version 0.8.1

ZIP entries now share the song's title: `My Melody.ogg` and `My Melody.tmb`. The archive's chosen filename can differ. Internal working assets can still be called `song.ogg` and `song.tmb`; packaging gives them their song-specific names. The chart extension is `.tmb`.

**Save OGG As…** is beside Convert OGG and in the File menu. It converts missing OGG audio, saves a separate copy to the chosen destination, and remembers that destination. Existing destinations are replaced only after the native overwrite prompt.

Right-click a recent-project entry for **Open Project**, **Rename Song…**, **Save Project**, **Open Project Folder**, **Remove from Recent Projects**, or **Refresh List**. Right-clicking does not switch the editor to that project. Rename changes the song title and future packaged filenames; its project filename and assets folder stay in place. Renaming the open song saves pending chart edits. Removing an entry hides it without deleting files, and the removal survives refresh, autosave and restart. Opening that project explicitly adds it to history again. Right-click an empty area of the list to refresh.
Validation: 268 isolated studio workflow checks passed, including real audio conversion, matching ZIP entry names after rename, native Save As configuration, OGG replacement and error preservation, recent-item targeting, title persistence, and hiding entries across saves, difficulty changes and restarts. The tests do not alter the user song library.

### Direct game import and installed-song status — version 0.9.0

**Import into Game** sits immediately beside **Export ZIP**. It saves the current editor notes, builds any missing OGG/MIDI/TMB output, and installs the selected difficulty directly. An already installed level shows **Update in Game**. Updates keep the previous game folder in backup and preserve other installed difficulties. If the project is renamed, its stable chart ID finds the previous import and the next update uses the new matching folder and filenames.

The default data folder is `%USERPROFILE%\AppData\LocalLow\Big Blue Bubble Inc\My Singing Monsters Karaoke`. **Game → Choose Game Data Folder…** supports another location and remembers it. The selected folder must contain `Songs`. The actual local library was inspected: its `DAsh` and `bbli$zard` folders contain matching `<folder>.ogg` and `<folder>.tmb` files. The studio follows that structure. A first imported level uses the song title; additional levels or independent projects with the same title get a difficulty/suffix in both folder and filenames.

The header reports whether the current difficulty is installed. Each recent-project entry lists installed difficulties. Status is read from the actual game folders, refreshed automatically every three seconds, and can be refreshed from the Game menu. Existing studio imports are matched by chart ID; older imports can be recognized by title and difficulty. Missing OGG audio is reported and can be repaired with import/update. Updating a legacy same-title game song asks before replacing it.

**Game → Manage Game Songs…** lists all custom songs, including older imports without studio projects. It provides refresh, open-song-folder, open-backup-folder and remove actions. **Remove Current Level from Game…** is also in the Game menu. A recent project's right-click menu supports import/update and removal for its saved selected level. Removal asks for confirmation and moves that custom-song folder outside `Songs` to `.MSMStudio\backups` in the game data folder. It does not delete the studio project or source audio. Updates use the same backup area. No game progress/save files are edited.

Imports are prepared in `.MSMStudio\staging` before being moved into `Songs`, preventing partially copied audio/chart pairs from appearing in the game library. A failed replacement attempts to restore the previous folder. Invalid audio and folders outside the selected game library are rejected. Folders containing several charts require manual management instead of replacing/removing multiple charts unintentionally.

If the game is already running, refresh its song list or restart it after importing, updating or removing songs. Folder/file integration and chart output are verified locally; a full gameplay check inside the game remains unverified.

Validation: 300 isolated studio workflow checks passed, covering real audio/chart builds, installed status, imports/updates after rename, independent difficulties, duplicate-title projects, removal backups, corrupt/missing files, path boundaries and preservation of game save data. The actual user's existing game songs are inspected read-only.

### Simpler save controls — version 0.9.1

The Notes / MIDI button is now **Save .mid / .tmb…**. Its Save As dialog exports the selected level's current editor notes as MIDI or a game TMB chart, without regenerating notes or requiring audio conversion. MIDI stores discrete pitch notes; TMB preserves slides through joined segments. **Auto Chart** remains beside the difficulty selector for generation.

The Build Song button and Tools menu item were removed. **Export ZIP…** and **Import into Game** sit together and automatically prepare the required files. Open Chart, Save Chart As and Save OGG As remain available.

Validation: 304 isolated workflow checks passed, including exporting current notes as MIDI and TMB without regeneration. Build completed with zero warnings and errors.

### Chart Info, sync status, online library and play testing — version 0.10.0

**Chart Info…** opens a saved configuration for song name, short name, artist, release year, genre, description, BPM, beats per bar, the selected level's 1–10 rating, note spacing and start/end colors. The short name is no longer forced to the first 20 characters of the full title. The editor level (Easy/Normal/Hard/Expert) stays independent of the custom numeric rating. Imported chart metadata is preserved. The stable chart identifier remains unchanged when editing these fields.

**Game sync status** now reports **Up to date** or **Out of sync** with a reason. It compares the generated chart JSON with the installed TMB semantically (ignoring formatting) and compares the OGG contents by SHA-256. Pending editor edits immediately mark the current level out of sync; saving rechecks it. Recent projects report each installed level separately. Changing shared metadata marks affected installed levels out of sync. External installed chart/audio changes are detected on the regular status refresh. File hashes are cached by path, modification time and size to avoid rereading unchanged audio every three seconds. New imports also retain a source-audio fingerprint in `.msmstudio-import.json`, so edits to an MP3 are detected even if an old converted OGG is still present. Older imports gain that fingerprint on their next update. Update in Game backs up and replaces the installed copy, restoring Up to date. Conversion checks the source fingerprint and rebuilds stale converted audio while retaining manual chart edits.

**Library…** (also View → Online Library / Downloads) opens a separate explorer with TromboneDB, Trombone Charts and TootTally source buttons, web address navigation, Back, Refresh, and Open in Browser. It uses each site's own browser/search UI rather than a combined API search. A supported ZIP, TMB or studio-project download is saved under `%LOCALAPPDATA%\MSM Song Studio\Downloads`. Download history retains the source, package and editable project paths across restarts. With **Download → import and open in editor** enabled, completed single-chart downloads open directly for editing. Packages containing several charts list all editable songs for selection. **Open Local ZIP…** imports an existing package; **Downloads Folder** opens saved files.

Downloaded TMBs retain their existing notes and paired OGG audio; no automatic replacement chart is generated. Same-stem OGG pairing is preferred, with a single OGG in the chart's folder accepted as a fallback. Studio-project ZIPs preserve their full curves and companion assets. ZIP extraction rejects traversal, linked entries, duplicate filenames and oversized packages (512 MB / 2048 entries); executable files are not extracted or run. TMBs with variable tempo arrays, unsupported note data or pitches outside the export range cannot currently import. A chart-only download can open without audio, but needs audio before playback/game import. Site logins, redirects, changed layouts or non-package links may need the original site in a browser or a local ZIP. Downloading into the studio does not install the chart into the game; use Import into Game after reviewing it.

**View: Editor / Preview / Play Test** is in the editor toolbar. Preview displays approaching notes and follows their actual curve automatically. Play Test uses a white hit line: move the mouse vertically to follow the pitch and hold left click or **Z** while a note passes. **Space** plays/pauses, **Restart** starts from the beginning, and pitch tolerance can be Normal, Relaxed or Precise. Practice accuracy measures the proportion of active note duration followed while holding; notes with at least 60% accuracy contribute to the combo. Scoring and rendering use the same audio element/current-time clock as the editor, including its timing offset. Seeking/restarting or changing tolerance resets practice results. Preview and Play Test disable editor note controls and shortcuts; returning to Editor retains all notes and undo history. This is a local chart timing/practice tool, not an exact replica of the game's scoring, physics or mod-specific effects.

References: [TC Chart Converter metadata fields](https://tc-chart-converter.github.io/), [TromboneDB](https://tc-mods.github.io/TromboneDB/), [Trombone Charts](https://trombonecharts.com/), [TootTally search](https://toottally.com/search/), [WebView2 download operations](https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2downloadoperation).

Validation: 329 isolated workflow checks passed, including metadata round trips, pending/recent sync badges, external chart/audio changes, archive traversal rejection, studio-project ZIPs, the real WebView2 download-to-editor handoff using a local HTTP fixture, curved play-test hits/misses, pause behavior and editor preservation. Build completed with zero warnings and errors.


### Sync colors and game-manager button — version 0.10.1

The current song and recent projects use green for Up to date, amber for Out of sync, red for missing audio, blue when another level is installed, and gray for Not in game or unavailable status. Text labels remain visible and tooltips show the full status. **Manage Game Songs…** is now a toolbar button beside Import / Update in Game, opening the existing installed-song manager.

### Chart Info placement — version 0.10.2

**Chart Info…** is now in the song header beside the project-save buttons, and in the recent-project right-click menu. The recent-menu action edits the clicked project's metadata without switching the active editor; notes and linked audio are retained. Saving refreshes the song title and sync status.


Validation: 26 focused metadata, sync, library and preview checks passed, including editing an inactive recent project's details without changing the active editor or its notes. Build completed with zero warnings and errors.

## Version 0.10.3 — Play Test instrument

- Hold left click or Z while the song plays to sound a soft instrument tone. Move vertically to change pitch continuously, including curved slides and fractional pitches.
- The existing Volume slider scales both the song and the instrument. Pause, stop, seek, mouse/key release, focus loss and leaving Play Test silence the instrument.
- Preview remains an automatic visual guide. The tone is a synthesized practice instrument, not a sample from the game.
- Verified in the live WebView2 audio graph: audible output, 440/220 Hz octave mapping, continuous pitch, volume/mute, release, focus loss, pause, and unchanged chart notes.

## Version 0.10.4 — App title

The window title, sidebar heading, version label and embedded editor title now show My Singing Monster Karaoke Studio. Existing project, audio, game and settings paths are preserved.

## Version 0.10.5 — Play Test instrument selection

Play Test includes an Instrument selector: Soft tone, Flute, Brass, Organ, Chiptune, or None. These are synthesized practice sounds. None silences only the practice instrument; song playback and scoring continue. The choice is remembered across sessions. Switching instruments preserves pitch, playback, scores and chart notes.

## Version 0.10.6 — App icon

A custom purple singing-monster and turquoise microphone icon is embedded in the Windows executable and main window. Assets/StudioIcon.png preserves the original image, and Assets/StudioIcon.ico includes 16, 24, 32, 48, 64, 128 and 256 pixel sizes for Windows displays. Both resources are bundled in the self-contained executable.

## Version 0.10.7 — Game song projects and recovery

Manage Game Songs has Installed Songs and Deleted Songs / Previous Versions tabs. Select an installed song and click Open as Project (or double-click) to copy its original chart and OGG into an editable studio project and recent history. The game files are kept intact. Songs with missing audio or invalid charts cannot be opened as playable projects.

Deleted songs and update backups can be restored to their original game folder. Restore refuses to overwrite an existing folder. Older backups are supported. Delete Permanently removes only the selected backup after a warning with No selected by default; installed songs, studio projects and game progress are preserved. Linked files/folders and paths outside the backup root are rejected.

Validation: 42 isolated game-integration checks passed, including project copies, recent history, exact restore, restore conflicts, old backups, permanent-deletion scope, untouched progress files and manager controls.

## Version 0.10.8 — Smaller app and separate FFmpeg installer

The app no longer embeds the 105 MB FFmpeg executable. First setup downloads the pinned 9.0.2 essentials ZIP from the official Gyan GitHub mirror (gyan.dev fallback), checks its archive and executable hashes, installs FFmpeg locally, then deletes the temporary download. Conversion keeps working offline after setup. Existing verified installations are reused; failed or cancelled setup keeps existing files and removes partial downloads. Earlier release notes describing an embedded/offline installer apply only to those older versions.

Single-file compression is enabled in the project and Windows build script. The .NET desktop runtime stays included for portability. Compression may add a small startup cost. The icon, editor assets, and license/source notices remain embedded.

Measured Windows EXE size: 74,212,882 bytes (74.2 MB / 70.8 MiB), down from 270,503,161 bytes (270.5 MB / 258.0 MiB), a 72.6% reduction. All 13 installer checks passed, including a real fresh release download, checksum verification, absence of bundled FFmpeg, automatic conversion, reuse, concurrent repair, cancellation, invalid archives and failed network cleanup.

## Version 0.10.9 — Downloaded chart numeric metadata

TMB import accepts finite numeric text for year, constant tempo, time signature and difficulty, alongside JSON numbers. Integer fields still require whole values within integer bounds; bad metadata produces a field-specific error. Note rows remain strictly validated. This fixes Golden from Trombone Charts, whose year is stored as the string 2025.

Validation: 11 metadata/package checks passed against the user's existing Golden ZIP, including all 470 original notes, preserved metadata and ZIP, paired audio, TMB save/reopen and real WebView2 playback readiness. The 41 existing library/preview checks also passed.
