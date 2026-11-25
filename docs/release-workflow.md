# Release and update workflow

This is the repeatable process for changing the launcher and publishing it. It covers a normal
feature release, a small patch, and the special case of republishing a beta whose asset was
superseded. The goal is that code, documentation, screenshots, and the GitHub release never drift
apart.

## Versioning

The project uses semantic versioning with a beta suffix:

- `3.0.0-beta.1` — current beta.
- `3.0.0-beta.2` — next beta with fixes or small features.
- `3.0.0` — first stable release.
- `3.0.1` — patch after stable.

The version lives in exactly two user-visible places:

1. `src/SvuSessionsLauncher/SvuSessionsLauncher.csproj` → `<Version>`.
2. `README.md` → the bold version line at the top.

`build-release.ps1` and `build-installer.ps1` read the project version and use it in the asset
names, so those two must match. Release notes and the Git tag use the same string with a `v`
prefix (`v3.0.0-beta.1`).

## Branch and commit style

- Work on `main` for small changes; use a short-lived branch for a feature that touches several
  areas.
- One logical change per commit. Do not mix a UI fix, a protocol change, and a documentation
  rewrite in one commit.
- Commit messages are imperative and scoped: `Fix downloads table header alignment`,
  `Document Linktivity transport limits`, `Add AppPaths`.
- Keep generated files, logs, local settings, and screenshots that are not part of the docs out
  of commits.
- Before a release, `git status` should show only intentional changes.

## Pre-flight checklist

Run this before touching the version:

- [ ] `dotnet build SvuSessionsLauncher.slnx -c Release` is clean (0 warnings, 0 errors).
- [ ] The main flows were exercised: sign-in, session list, join, chat, screen share, leave,
      sign-out, downloads open/filter/download/cancel.
- [ ] English and Arabic both render correctly; the downloads table header and rows share the
      same start edge, with and without a scrollbar.
- [ ] Window transitions (minimize, maximize, restore, open, close) are smooth in studio, chat,
      viewer, and sign-in.
- [ ] No launcher window is left open after testing.
- [ ] Security checklist in [security-privacy.md](security-privacy.md) passes.
- [ ] Documentation reflects the change: [architecture.md](architecture.md),
      [code-map.md](code-map.md), [security-privacy.md](security-privacy.md), and README as
      needed.
- [ ] Screenshots still show the current UI. Recapture from the real app if the visible layout
      changed; blur tutor names, and blur student usernames and avatar initials in chat images.
- [ ] Any timing claim names the action measured and the conditions, and comes from a recorded
      run. Compare join to join, not app startup to join.
- [ ] [Inno Setup 6](https://jrsoftware.org/isdl.php) is installed if this release includes the
      installer.

## Step 1 — Make the change

1. Create a branch if the change is large.
2. Implement the smallest coherent change.
3. Update or add comments only where the "why" is not obvious.
4. Delete replaced code; do not leave dead paths behind a flag.
5. Build and run the affected flow.
6. Commit the code and its tests/docs separately where practical.

## Step 2 — Update documentation

Every user-visible change needs a documentation pass:

| Change | Update |
| --- | --- |
| New/changed screen or flow | `README.md` scope and deferred work, `docs/architecture.md` flow, screenshots if visible |
| New/changed service or file | `docs/code-map.md` |
| Credential, logging, or transport change | `docs/security-privacy.md` |
| Version or packaging change | this file and `README.md` |
| Removed feature | README "Not in this beta", architecture, code map |
| Deferred feature | README "Not in this beta", release notes |

If the change alters a design decision (for example, a new retry policy), record the reason in
the relevant doc, not only in the commit message.

## Step 3 — Bump the version

For a new release:

1. Edit `<Version>` in `src/SvuSessionsLauncher/SvuSessionsLauncher.csproj`.
2. Edit the bold version line in `README.md`.
3. Add or update release notes (see the template below).
4. Commit: `Release 3.0.0-beta.2`.

For a documentation-only change that does not alter the binary, do not bump the version and do
not publish a new asset. Push the docs and update the existing release notes only if the release
text is now wrong.

## Step 4 — Build and package

```powershell
.\build-installer.ps1
```

This is the release path because it produces both assets from one staged build. It:

- runs `build-release.ps1` to publish the launcher framework-dependently for `win-x64`,
- copies the x86 `SvuVoiceHelper` output that `dotnet publish` does not pick up,
- removes XML docs, debug symbols, arm64/x86 WebView2 runtimes, the WinForms WebView2 assembly,
  and any local WebView2 profile,
- writes a short `README.txt` with requirements and run instructions,
- zips the staged app to `dist\SVU-Sessions-Launcher-<version>-win-x64.zip`,
- compiles `installer\SvuSessionsLauncher.iss` with Inno Setup into
  `dist\SVU-Sessions-Launcher-<version>-win-x64-setup.exe`,
- writes both SHA256 values to `dist\SHA256SUMS.txt` and prints them.

The installer is per-user (`PrivilegesRequired=lowest`), installs under
`%LOCALAPPDATA%\Programs\SVU Sessions Launcher`, creates Start Menu shortcuts and an uninstaller,
and can optionally create a desktop shortcut. It checks the .NET 10 Desktop Runtime (x64) and
WebView2 before installing, and warns about the optional x86 runtime and TrueSpeech codec. It
does not download or install runtimes silently.

The wizard has no license or terms page. A static "Before you install" page lists the
requirements without a read-only text box, and the MIT license is installed as `LICENSE` in the
app folder.

The setup is compiled into `%TEMP%\opencode\svu-setup-out` and then moved into `dist`. Antivirus
scanners often lock a setup file that was just executed, and Inno cannot update resources in a
locked file.

Record both hashes. They go in the release notes and `SHA256SUMS.txt`.

Use `.\build-release.ps1` alone only when you need the portable zip without the installer.

## Step 5 — Verify the artifacts

Do not publish the first files that appear. Check the zip:

- [ ] The zip opens and contains `SvuSessionsLauncher.exe`, `SvuVoiceHelper.exe`, the WPF
      dependencies, `README.txt`, and `LICENSE`.
- [ ] No `.pdb`, no XML documentation, no `runtimes\win-arm64`, no `runtimes\win-x86`, no
      `SvuSessionsLauncher.exe.WebView2` profile.
- [ ] Extract to a clean folder and launch. Sign-in renders, the studio opens, a session can be
      joined if test credentials are available.
- [ ] The SHA256 printed by the script matches `Get-FileHash` on the zip.
- [ ] The zip size is in the expected range (roughly 1.3 MB for the current lean build; a large
      jump usually means a runtime or docs folder slipped in).

Check the installer:

- [ ] `Get-FileHash` on the setup matches `dist\SHA256SUMS.txt`.
- [ ] A silent install into a temporary folder works, and the app files land at the install root
      rather than a nested `app` folder:

  ```powershell
  $setup = (Resolve-Path 'dist\SVU-Sessions-Launcher-<version>-win-x64-setup.exe').Path
  $dir = Join-Path $env:TEMP 'opencode\svu-installer-test'
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  $p = Start-Process $setup -ArgumentList @(
    '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOICONS',"/DIR=$dir"
  ) -Wait -PassThru
  $p.ExitCode
  Test-Path (Join-Path $dir 'SvuSessionsLauncher.exe')
  ```

- [ ] Uninstalling runs `unins000.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, removes the
      install folder and the Start Menu folder, and removes the uninstall registry key at
      `HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{139FD741-0EB5-468B-BD78-641B5891DD0E}_is1`.
- [ ] A normal interactive install shows the "Before you install" page and creates the Start
      Menu entry.
- [ ] The installer does not request elevation and does not install runtimes itself.

## Step 6 — Commit, tag, and push

```powershell
git add -A
git commit -m "Release 3.0.0-beta.2"
git push origin main

git tag -a v3.0.0-beta.2 -m "SVU Sessions Launcher 3.0.0-beta.2"
git push origin v3.0.0-beta.2
```

Use an annotated tag. The tag must point at the same commit that produced the artifacts.

### Republishing an existing beta version

Sometimes the version must stay the same because the code did not change, but the packaging,
documentation, or screenshots did. For example, a beta may gain a setup installer and corrected
release notes while its binary source stays identical.

If the source changed, the old tag and release must be removed first, because a published tag must
never silently point at different content:

```powershell
gh release delete v3.0.0-beta.1 --yes --cleanup-tag
git push origin :refs/tags/v3.0.0-beta.1   # only if the remote tag still exists
```

Then rebuild, recreate the tag at the new commit, and publish again.

If only docs, images, and installer/build scripts changed and the application source is untouched,
keep the tag where it is, replace the release assets, and say in the release notes that the
assets were refreshed. Do not move the tag for a packaging-only refresh; the tag remains the
record of the code that produced the binary. For any version users may already have downloaded
with different code, publish a new version instead of moving the tag.

## Step 7 — Publish the GitHub release

Write the notes to a file so they can be reviewed before publishing. The repository keeps the
final text under `docs/releases/`; the template is at the end of this file. A short form:

```markdown
## SVU Sessions Launcher 3.0.0-beta.2

Beta software. The main flows work; bugs are expected.

### Changes
- ...

### Fixes
- ...

### Security and privacy
- ...

### Inherited constraints
- Linktivity session traffic is not TLS; this is a vendor protocol limitation.
- The recordings page is served over plain HTTP.
- Tutor voice requires the TrueSpeech ACM codec.

### Not in this beta
- Tutor and host mode; the 2017 client had it, the host flow needs a rebuild.
- Student voice; the microphone stays off while audio transfer and sharing are split and the
  university's backend behavior is understood.
- Notifications and private messaging.

### Requirements
- Windows 10/11 x64
- .NET 10 Desktop Runtime (x64)
- .NET 10 Runtime (x86) for tutor voice
- Microsoft Edge WebView2 Runtime
- TrueSpeech ACM codec for tutor voice

### Assets
- `SVU-Sessions-Launcher-3.0.0-beta.2-win-x64-setup.exe` — SHA256: `<hash>`
- `SVU-Sessions-Launcher-3.0.0-beta.2-win-x64.zip` — SHA256: `<hash>`
```

Publish a new release with both assets:

```powershell
gh release create v3.0.0-beta.2 `
  .\dist\SVU-Sessions-Launcher-3.0.0-beta.2-win-x64-setup.exe `
  .\dist\SVU-Sessions-Launcher-3.0.0-beta.2-win-x64.zip `
  .\dist\SHA256SUMS.txt `
  --title "SVU Sessions Launcher 3.0.0-beta.2" `
  --notes-file .\docs\releases\3.0.0-beta.2.md
```

### Beta visibility

By default GitHub hides releases marked as prerelease from the repository sidebar and from
`releases/latest`, even though the release page itself exists. This project keeps beta status in
the title and the first paragraph of the notes, but publishes the release without the prerelease
flag so students can find it. Do not add `--prerelease` unless there is a reason to hide the
build. Verify with:

```powershell
gh api repos/qusielh/svu-sessions-launcher/releases/latest --jq '{tag_name, isPrerelease, isDraft}'
```

For a stable release, use the same flow and remove "beta" from the title.

### Replace assets on an existing release

```powershell
gh release upload v3.0.0-beta.1 `
  .\dist\SVU-Sessions-Launcher-3.0.0-beta.1-win-x64-setup.exe `
  .\dist\SVU-Sessions-Launcher-3.0.0-beta.1-win-x64.zip `
  .\dist\SHA256SUMS.txt `
  --clobber

gh release edit v3.0.0-beta.1 `
  --notes-file .\docs\releases\3.0.0-beta.1.md
```

`--clobber` replaces an asset with the same filename. State the replacement in the notes.

## Step 8 — Verify the published release

- [ ] The release page shows the correct title, tag, notes, and both assets.
- [ ] The release is visible: `releases/latest` returns it and the repository sidebar shows it.
      A `404` from the latest endpoint usually means the release is marked prerelease or draft.
- [ ] The setup and zip download, and their SHA256 values match `SHA256SUMS.txt` and the notes.
- [ ] The installer publishes the expected version and does not request elevation.
- [ ] The extracted zip launches on a machine with only the documented runtimes.
- [ ] For a same-version refresh, any old asset with the same name was replaced and the notes say
      so.
- [ ] README images render on GitHub (relative `docs/images/...` paths, committed files). Release
      notes may use a `raw.githubusercontent.com` URL because they are not repository content.
- [ ] The README download link points at the new release.
- [ ] The old release/tag is gone if this was a code-changing same-version republish.
- [ ] No credentials, logs, or personal data are attached to the release.

## Patch workflow (after stable)

For a small fix:

1. Branch from `main` (or work on `main` if trivial).
2. Fix, build, and test the exact flow.
3. Add a regression note to the release notes and, if the behaviour was documented, update the
   doc.
4. Bump the patch version (`3.0.1`).
5. Run the same package and verification steps.
6. Tag `v3.0.1`, publish a normal (non-prerelease) release.
7. If the fix is security-sensitive, publish the release before describing the exploit in
   detail, and avoid attaching user data.

## Rollback

- **Bad release, same version not yet adopted:** delete the release and tag, fix, rebuild, and
  republish. State that the asset was replaced.
- **Bad release already adopted:** publish a new patch version that reverts or fixes. Do not move
  the old tag; users and caches may already have it.
- **Bad local change:** use `git revert <commit>` on `main` and publish a patch. Keep the history
  honest rather than force-pushing a shared branch.

## Release notes template

Keep a file per release under `docs/releases/` if the notes are long enough to review. The file
is the source for `gh release create --notes-file`, and it stays in the repository as a record.

```markdown
# 3.0.0-beta.N

## Highlights
- ...

## Fixes
- ...

## Security and privacy
- ...

## Inherited constraints
- ...

## Not in this beta
- ...

## Requirements
- ...

## Assets
- `SVU-Sessions-Launcher-<version>-win-x64-setup.exe` — SHA256: `<hash>`
- `SVU-Sessions-Launcher-<version>-win-x64.zip` — SHA256: `<hash>`
```

## What not to do

- Do not publish a zip or setup that was not produced by `build-installer.ps1` (or
  `build-release.ps1` for the zip alone) from the tagged source.
- Do not reuse a version for different code once users may have downloaded it. A packaging-only
  refresh may keep the version; a code change needs a new one.
- Do not hide a beta from `releases/latest` by reflex; this project publishes betas without the
  prerelease flag so students can find them, and states beta status in the title and notes.
- Do not commit `settings.json`, `credentials.dat`, `error.log`, `responses/`, `catalog.json`,
  `bin/`, `obj/`, or `dist/`.
- Do not fake screenshots or edit the UI in an image editor. Capture the real app, blur only
  what must be blurred, and keep the caption honest.
- Do not call a deliberately deferred feature a limitation. Limitations are constraints inherited
  from the university or vendor; deferred work is a scope decision.
- Do not publish a timing claim without the measured action and conditions. Compare join to join,
  not app startup to join.
- Do not describe a transport as encrypted when it is the vendor's fixed handshake. State the
  limitation.
- Do not leave the launcher running after a test session; close it and confirm no GUI windows
  remain.
