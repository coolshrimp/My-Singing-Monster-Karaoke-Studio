# Preparing and publishing a release

## Current state

The source version is **0.10.14**. The repository is [coolshrimp/My-Singing-Monster-Karaoke-Studio](https://github.com/coolshrimp/My-Singing-Monster-Karaoke-Studio), owned by Coolshrimp. The first downloadable release uses the `live` tag. Local development notes are not a substitute for Git history.

## Before pushing

- Review the staged diff and run `git diff --cached --check`.
- Check that only source, documentation, screenshots and current test sources are tracked. Build outputs, installers, archives, downloaded FFmpeg, local settings and song assets must stay excluded.
- Scan the staged files for credentials. Never commit access tokens, passwords, private keys, `.env` files or authenticated remote URLs. Use GitHub-managed workflow credentials instead of literal values.
- Run the smoke checks and release builder below; verify the EXE version. Inspect screenshots for private information.

## First GitHub setup

1. Review the staged source and documentation, then create the initial local commit.
2. While authenticated as `coolshrimp`, create the empty `coolshrimp/My-Singing-Monster-Karaoke-Studio` GitHub repository and add its URL as `origin`. GitHub repository names use hyphens instead of spaces; the app and release title remain **My Singing Monster Karaoke Studio**. Avoid creating unrelated initial files on GitHub when pushing an existing local repository.
3. Push `main` with the workflow included. Make sure GitHub Actions is enabled and repository/organization policy permits the release job's `contents: write` permission.
4. Run **Build Windows release** manually from Actions first. It runs checks and produces downloadable build artifacts without publishing a release.
5. After reviewing that build, push the annotated `live` tag for the first release. Future releases use a new version tag matching the project version. Do not overwrite published tags.

Choose a source-code license before presenting the repository as open source. No license has been invented for the application.

## Every release

1. Update `<Version>` in `MySingingMonsterKaraokeStudio.csproj` and update the current-version references in README and this guide.
2. Move applicable Unreleased changes into a matching entry in `CHANGELOG.md`.
3. Run the Windows smoke harness and local release packager:

   ```powershell
   dotnet run --project tests/MySingingMonsterKaraokeStudio.SmokeTests/MySingingMonsterKaraokeStudio.SmokeTests.csproj -c Release
   .\scripts\build-release.ps1 -Tag v0.10.14
   ```

4. Inspect `dist/MySingingMonsterKaraokeStudio.exe`, launch the app, and test the intended game package in the game.
5. Commit the reviewed source changes and push the branch. Then create and push the corresponding annotated tag:

   ```powershell
   git tag -a v0.10.14 -m "My Singing Monster Karaoke Studio 0.10.14"
   git push origin v0.10.14
   ```

   Replace this example with the new version for later releases. Do not reuse published release tags.

For the first release, use `-Tag live` when packaging, then create and push the requested tag:

```powershell
git tag -a live -m "My Singing Monster Karaoke Studio 0.10.14"
git push origin live
```

## What happens when the tag is pushed

The workflow checks out the tagged source on a Windows runner and accepts `live` or a version tag that matches the project version. It runs the real WPF/WebView2 smoke harness, then publishes a compressed self-contained Windows x64 app from that source. No prebuilt local EXE is used.

Only after successful checks and building does a separate job create the GitHub Release and attach **`MySingingMonsterKaraokeStudio.exe`**. This is the only application release asset; no portable ZIP or checksum text file is generated or attached.

The standalone EXE includes .NET and its embedded editor and notices. Users run it directly and install the converter through **Install FFmpeg** in the app. It contains no user songs, downloads or test data.

A **manual workflow run** performs the same checks/build but only uploads a workflow artifact, retained for 14 days. Pushing `live` or a matching version tag publishes the permanent GitHub Release EXE and marks the release as latest. Release titles and EXE metadata use the project version even when the tag is `live`; the download filename stays `MySingingMonsterKaraokeStudio.exe`. A local tag alone does not trigger GitHub Actions.

## Failure and verification

- A test/build failure stops release publication. Inspect Actions logs and retry after fixing the source with a new appropriate version/tag.
- A tag/version mismatch fails before tests or publishing.
- First-time FFmpeg setup in the checks requires network access. WebView2 and a Windows desktop session are required for the smoke harness.
- If release publication fails because a release already exists, inspect that release before changing anything. The workflow deliberately does not overwrite an existing release.
- Verify the uploaded EXE against the local build with `Get-FileHash -Algorithm SHA256` or GitHub's release-asset digest; no separate checksum file is published.
- These releases are not code-signed. Windows may show a reputation warning for a new downloaded EXE. Code signing can be added later when a certificate is available.

Verify the cloud run and the single EXE release asset after publishing. Local validation cannot prove GitHub permissions, hosted-runner availability or release upload behavior.
