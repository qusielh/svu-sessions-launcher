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

`build-release.ps1` reads the project version and uses it in the zip name, so those two must
match. Release notes and the Git tag use the same string with a `v` prefix
(`v3.0.0-beta.1`).

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
      changed; blur tutor names; keep the username visible if it helps the demo.

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
| New/changed screen or flow | `README.md` features/limitations, `docs/architecture.md` flow, screenshots if visible |
| New/changed service or file | `docs/code-map.md` |
| Credential, logging, or transport change | `docs/security-privacy.md` |
| Version or packaging change | this file and `README.md` |
| Removed feature | README limitations/roadmap, architecture, code map |

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
dotnet build .\SvuSessionsLauncher.slnx -c Release
.\build-release.ps1
```

`build-release.ps1`:

- reads `<Version>` from the project,
- publishes the launcher framework-dependently for `win-x64`,
- copies the x86 `SvuVoiceHelper` output that `dotnet publish` does not pick up,
- removes XML docs, debug symbols, arm64/x86 WebView2 runtimes, the WinForms WebView2 assembly,
  and any local WebView2 profile,
- writes a short `README.txt` with requirements and run instructions,
- zips the staged app to `dist\SVU-Sessions-Launcher-<version>-win-x64.zip`,
- prints the SHA256.

Record the hash. It goes in the release notes.

## Step 5 — Verify the artifact

Do not publish the first zip that appears. Check:

- [ ] The zip opens and contains `SvuSessionsLauncher.exe`, `SvuVoiceHelper.exe`, the WPF
      dependencies, and `README.txt`.
- [ ] No `.pdb`, no XML documentation, no `runtimes\win-arm64`, no `runtimes\win-x86`, no
      `SvuSessionsLauncher.exe.WebView2` profile.
- [ ] Extract to a clean folder and launch. Sign-in renders, the studio opens, a session can be
      joined if test credentials are available.
- [ ] The SHA256 printed by the script matches `Get-FileHash` on the zip.
- [ ] The zip size is in the expected range (roughly 1.3 MB for the current lean build; a large
      jump usually means a runtime or docs folder slipped in).

## Step 6 — Commit, tag, and push

```powershell
git add -A
git commit -m "Release 3.0.0-beta.2"
git push origin main

git tag -a v3.0.0-beta.2 -m "SVU Sessions Launcher 3.0.0-beta.2"
git push origin v3.0.0-beta.2
```

Use an annotated tag. The tag must point at the same commit that produced the zip.

### Republishing an existing beta version

Sometimes the code changes but the version must stay the same (for example, a beta whose asset
was superseded before anyone depended on it). In that case the old tag and release must be
removed first, because a published tag must never silently point at different content.

```powershell
gh release delete v3.0.0-beta.1 --yes --cleanup-tag
git push origin :refs/tags/v3.0.0-beta.1   # only if the remote tag still exists
```

Then rebuild, recreate the tag at the new commit, and publish again. State in the release notes
that the beta asset was replaced. For any version users may already have downloaded, publish a
new version instead of moving the tag.

## Step 7 — Publish the GitHub release

Write the notes to a file so they can be reviewed before publishing. A template:

```markdown
## SVU Sessions Launcher 3.0.0-beta.2

Beta software. The main flows work; bugs are expected.

### Changes
- ...

### Fixes
- ...

### Security and privacy
- ...

### Known limitations
- Linktivity session traffic is not TLS; this is a vendor protocol limitation.
- The recordings page is served over plain HTTP.
- Tutor voice requires the TrueSpeech ACM codec.
- Tutor and host accounts are blocked in this beta.

### Requirements
- Windows 10/11 x64
- .NET 10 Desktop Runtime (x64)
- .NET 10 Runtime (x86) for tutor voice
- Microsoft Edge WebView2 Runtime
- TrueSpeech ACM codec for tutor voice

### Asset
`SVU-Sessions-Launcher-3.0.0-beta.2-win-x64.zip`
SHA256: `<hash>`
```

Publish:

```powershell
gh release create v3.0.0-beta.2 `
  .\dist\SVU-Sessions-Launcher-3.0.0-beta.2-win-x64.zip `
  --title "SVU Sessions Launcher 3.0.0-beta.2" `
  --notes-file .\docs\releases\3.0.0-beta.2.md `
  --prerelease
```

Use `--prerelease` for beta versions. For a stable release, omit it and mark the release as
latest.

## Step 8 — Verify the published release

- [ ] The release page shows the correct title, tag, prerelease flag, and notes.
- [ ] The asset downloads and its SHA256 matches the local zip.
- [ ] The extracted build launches on a machine with only the documented runtimes.
- [ ] README images render on GitHub (relative `docs/images/...` paths, committed files).
- [ ] The README download link points at the new release.
- [ ] The old release/tag is gone if this was a same-version republish.
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

## Known limitations
- ...

## Requirements
- ...

## Asset
- File:
- SHA256:
```

## What not to do

- Do not publish a zip that was not produced by `build-release.ps1` from the tagged commit.
- Do not reuse a version for different content once users may have downloaded it.
- Do not commit `settings.json`, `credentials.dat`, `error.log`, `responses/`, `catalog.json`,
  `bin/`, `obj/`, or `dist/`.
- Do not fake screenshots or edit the UI in an image editor. Capture the real app, blur only
  what must be blurred, and keep the caption honest.
- Do not describe a transport as encrypted when it is the vendor's fixed handshake. State the
  limitation.
- Do not leave the launcher running after a test session; close it and confirm no GUI windows
  remain.
