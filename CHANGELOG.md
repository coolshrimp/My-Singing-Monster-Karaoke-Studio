# Changelog

The current application version is **0.10.14**, the first GitHub release under the `live` tag. Earlier entries summarize local development versions; they do not imply that earlier Git tags or public releases exist. Detailed earlier notes are preserved in [docs/DEVELOPMENT-HISTORY.md](docs/DEVELOPMENT-HISTORY.md).

## 0.10.14 — 2026-10-02

- Added the `live` release trigger with versioned Windows downloads and credited Coolshrimp as the sole contributor.

- Removed obsolete build caches and the unused source-folder FFmpeg binary; installer notices now explain the separate verified download.
- Excluded temporary files and local credential/key files from Git; downloader requests report the actual application version.

- Fixed build, installer, release and documentation paths after moving the application to the repository root; restored the separate smoke-check project and excluded its sources from the app build.

- Added real app screenshots in `snapshots/` and an expandable README gallery; portable releases include the README image assets.
- Reworked the GitHub README into a current feature and usage guide.
- Added source-control exclusions for builds, synced references, local projects and downloaded FFmpeg.
- Added Windows tag-triggered checks, EXE/ZIP/checksum packaging and GitHub Releases.
- Updated smoke checks to install verified FFmpeg rather than require a source-tree binary, include the current Chart Info menu action, and use short isolated browser data paths.

- Unified note-edit and project undo/redo for Trim Audio, Auto Chart, Chart Info and difficulty changes, with 100 steps per open-song session.
- Undo and redo restore audio references, notes, all difficulty levels and timing together, and save the working project automatically. Imported audio, saved exports and installed game files are not overwritten by undo.

## 0.10.13

- Fixed single-file publish with an external .NET runtime by enabling compression only for self-contained bundles.
- Restored Unicode UI labels and documentation; added shared UTF-8 editor settings.
- Removed obsolete generated test and release outputs while retaining current Windows integration checks.

## 0.10.12

- Added draggable waveform start/end trim handles, shaded excluded regions and Clip selection, with keyboard adjustment and synchronized trim time fields.
- Clipping creates a separate OGG and realigns all difficulty charts; the original imported audio remains unchanged. Converter rejects overwriting its input or trimming to a non-OGG file.

## 0.10.11

- Reorganized source into src/, tests/ and scripts/, with a root solution and consistent application/project naming.
- Moved optional command shortcuts out of the repository root and updated all build/release references.
- Excluded executable/archive build artifacts from Git; downloads remain GitHub Release assets.

## 0.10.10

- Centered top navigation labels with balanced padding, without reserving submenu spacing.
- Added a Created by Coolshrimp link that opens https://coolshrimpmodz.com/ in the default browser.

## 0.10.9

- Fixed downloaded TMB imports when supported numeric metadata is stored as strings.
- Added package checks covering the reported downloaded chart and numeric validation.

## 0.10.8

- Reduced the compressed, self-contained Windows EXE to approximately 74 MB.
- Removed bundled FFmpeg; one-click setup downloads a pinned, checksum-verified converter separately.
- Added converter setup, reuse, verification, cancellation and failure checks.

## 0.10.7

- Open installed game songs as new editable studio projects.
- Separate installed songs from deleted/previous versions, with restore and confirmed permanent deletion.

## 0.10.6

- Added an application icon for the app window and Windows executable.

## 0.10.5

- Added selectable Play Test instruments and a silent None option.

## 0.10.4

- Renamed the application to My Singing Monster Karaoke Studio.

## 0.10.3

- Added synthesized instrument sound with continuous mouse-controlled pitch in Play Test.

## 0.10.2

- Moved Chart Info beside the song information and added it to the recent-project menu.

## 0.10.1

- Added colored game-sync statuses and a visible Manage Game Songs button.

## 0.10.0

- Added configurable chart information and content-based installed-song comparisons.
- Added online library downloads, package import, Preview and Play Test.

## 0.9.1

- Simplified the save/export controls, including Save .mid / .tmb and direct game import beside ZIP export.

## 0.9.0

- Added direct game import/update, installed-level status and recoverable song removal.

## 0.8.1

- Used matching song-named TMB and OGG filenames in ZIP exports.
- Added Save OGG As and recent-project context actions.

## 0.8.0

- Added automatic audio-following curve drafts using pitch and onset analysis.

## 0.7.0

- Added freehand curves, free placement without a grid and overlap cleanup for single-mouse charts.

## 0.6.3

- Added adjustable note lengths and sustained paint strokes.

## 0.6.2

- Added continuous right-drag note erasing.

## 0.6.1

- Fixed right-click deletion in Paint.

## 0.6.0

- Improved paint editing, project/audio restoration and automatic chart creation on import.

## 0.5.0 and earlier

- Established TMB export, audio conversion, playback, waveform seeking, multi-note selection, undo/redo, project persistence and the studio workspace.
