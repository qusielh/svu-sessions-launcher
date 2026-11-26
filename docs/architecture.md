# Architecture

This document explains how the launcher is put together: which process owns what, how the
windows and web pages talk to each other, where data lives, and how a sign-in becomes a live
session. It is the starting point for reviewers.

- [code-map.md](code-map.md) maps every folder and file.
- [window-transitions.md](window-transitions.md) explains the dissolve system in depth.
- [security-privacy.md](security-privacy.md) covers credentials, logging, and transport limits.
- [legacy-launcher.md](legacy-launcher.md) explains what failed in the 2017 client.

## Shape of the app

The launcher is deliberately small at the process level:

- **One WPF process** (`SvuSessionsLauncher.exe`, .NET 10, x64). It owns all windows, the
  WebView2 controls, the SOAP client, the recording downloader, and the in-process Linktivity
  session client.
- **WebView2 for presentation.** The sign-in, studio, and chat surfaces are local HTML/CSS/JS
  pages hosted inside WPF. The web pages render and collect input; C# remains the source of
  truth for state, validation, and network work.
- **One 32-bit helper process** (`SvuVoiceHelper.exe`). Windows ships the TrueSpeech codec only
  as a 32-bit ACM codec, so the 64-bit launcher cannot decode tutor audio in-process. The helper
  decodes and plays that one stream. It never opens a capture device.
- **No bundled Java, no vendor applet, no child browser.** The old client started a Java 6
  runtime and a WebDemo applet to join a session; a measured cold join took about 21 seconds from
  the Launch click to the meeting window. This build speaks the same Linktivity protocol directly
  from C#, so the join is a network operation rather than a runtime start.

The result is three process types at most: the launcher, WebView2's browser/renderer processes
(managed by the WebView2 runtime), and the optional voice helper.

## Startup and the login loop

`App.xaml.cs` is the composition root.

1. **Crash visibility first.** Handlers for `DispatcherUnhandledException`,
   `AppDomain.UnhandledException`, and `TaskScheduler.UnobservedTaskException` write to the
   sanitized log before anything else can fail.
2. **Warm the browser.** `WebViewRuntime.Prewarm()` starts the shared WebView2 environment. The
   sign-in page and the later in-session loader then paint without paying a cold start on screen.
3. **Load state.** `AppSettings.Load()` reads non-secret preferences. `DpapiCredentialStore` is
   created for the optional saved password.
4. **Create the services.** `CachedWebDemoService(SoapWebDemoService)` supplies catalog and
   session data; `LinktivitySessionLauncher` opens sessions.
5. **Apply language and theme** before any window is created, so the first frame is already
   correct.
6. **Run the window loop.** `RunAppAsync()` shows sign-in (or signs in silently on the first
   prompt when auto sign-in is enabled), then the studio, then waits for sign-out or exit. A
   sign-out loops back to sign-in with the last window bounds preserved; an exit shuts down.

The loop matters because the app has no single "main window" for its whole lifetime. WPF's
`Application.MainWindow` is reassigned to the studio once it exists; otherwise show-desktop and
taskbar restore target the long-gone sign-in window.

### Sign-in

`LoginWindow` hosts `Assets/login`. `LoginViewModel` owns the fields, busy state, and error text.
The page posts `username`, `password`, `savePassword`, `autoSignIn`, `showPassword`, `signIn`,
and `language` actions; the window posts the current state back. The view-model calls
`IWebDemoService.CheckUserPasswordAsync`, then `IsUserTrainerAsync`. Tutor and host accounts are
rejected on purpose: this is a student client. The 2017 client had a host/tutor mode; the beta
blocks those accounts because the host flow has not been rebuilt, not because the feature never
existed.

On success the view-model stores the username in settings, stores the password only through
`ICredentialStore` when "remember" or auto sign-in is on, and raises `SignedIn`. `App` closes the
login window and opens the studio.

### Studio

`MainStudioWindow` hosts `Assets/studio` and keeps `MainViewModel` as the source of truth. The
window pushes a compact JSON snapshot to the page (sessions, filters, theme, language, download
rows) and receives actions back. The bridge is intentionally narrow: the page never calls
services directly.

The studio owns the downloads window state (`DownloadsViewModel`) even though the downloads UI
is part of the same page. Downloads are lazy: the first open initializes the WebForms client.

### Session

`MainViewModel.LaunchCommand` calls `ISessionLauncher.LaunchAsync`. `LinktivitySessionLauncher`
derives the meeting host, creates a `RetryingSessionConnection` around a
`LinktivitySessionConnection`, and returns immediately. `App` then:

- creates or reuses a prewarmed `ChatWindow`,
- creates a `ViewerWindow` bound to the same connection,
- marks the studio's session lock so a second session cannot start,
- shows the chat with the dissolve transition.

The chat window is the session's owner in the practical sense: closing it closes the viewer,
releases the studio lock, and either reports an unexpected end or prewarms the next chat window.
Closing the studio closes the chat, so the app never leaves an orphaned session behind.

The viewer is independent: it appears when the first screen-share frame arrives and hides when
sharing stops. Closing it does not end the session.

## UI surfaces and the bridge

All three web surfaces follow the same pattern:

| Surface | Page | C# owner | Direction |
| --- | --- | --- | --- |
| Sign-in | `Assets/login` | `LoginWindow` + `LoginViewModel` | state down, input actions up |
| Studio | `Assets/studio` | `MainStudioWindow` + `MainViewModel` | state down, commands up |
| Chat | `Assets/chat` | `ChatWindow` + `ISessionConnection` | session events down, chat/voice actions up |

The pages are local files loaded with `file:`. The studio cancels any navigation that is not
`file:`. The sign-in page is local and contains no external links. The chat page allows only
`http`/`https` links and opens them in the default browser after validation. None of the surfaces
enable DevTools or the default context menu in Release.

The studio bridge is deliberately batched. View-model changes mark a dirty flag; a short
dispatcher timer coalesces pushes, and the sessions payload carries a revision number so the
page can skip unchanged lists. This keeps rapid filter changes from flooding the WebView.

## Services

### WebDemo (SOAP)

`IWebDemoService` is the contract the UI sees. The live implementation,
`SoapWebDemoService`, speaks the university's RPC/encoded SOAP 1.1 endpoint over HTTPS. It builds
small XML envelopes, parses the encoded arrays, and maps faults to `WebDemoServiceException`.
Credentials are held in memory only for the authenticated session.

`CachedWebDemoService` wraps it with a local catalog cache. Programmes and courses load from
memory, then `%LOCALAPPDATA%\SVU Sessions Launcher\catalog.json`, then the service. A background
refresh updates the file; a single-programme prefetch warms courses without hammering the server.
Sessions, today's sessions, trainer checks, and diagnostics always go to the live service.

### Settings, credentials, paths

- `AppSettings` persists language, theme, filter picks, endpoints, download preferences, and the
  remembered username. It never stores a password.
- `DpapiCredentialStore` encrypts the saved password with Windows DPAPI, current-user scope, and
  app-specific entropy. The blob is a base64 map keyed by username.
- `AppPaths` is the only place that composes file paths. Roaming data (settings, log, diagnostic
  dumps) and local data (catalog, credentials) are separated, and a legacy catalog is copied once.

### Downloads

`SvuDownloadClient` drives the university's public WebForms recordings page
(`http://sessions.svuonline.org/new/`). It keeps the page's hidden fields and cookies, posts the
dropdown cascade (term → programme → course → tutor → class), parses the result rows, and
streams chosen files with HTTP Range resume and bounded retries.

`DownloadsViewModel` serializes the cascade so two postbacks cannot interleave, preserves a term
the user picks while the initial page is still loading, and restores saved filters once the list
is ready. It also owns the download queue, per-row status, cancellation, and file naming.

### Logging

`Log` writes a single rotating file under `%APPDATA%`. Every line is sanitized first: URL query
strings are stripped, password-shaped assignments and XML password elements are masked, and the
service's `Not Found <user>` pseudo-row is redacted. The log is a support tool, not an audit
trail, and it is capped at 1 MB with one previous file.

## Session internals

The session stack is layered so the UI never sees vendor wire types:

```text
ChatWindow / ViewerWindow
        │  ISessionConnection (JSON events, viewport frames)
        ▼
RetryingSessionConnection     retries, synthetic retrying/joinFailed events
        │
        ▼
LinktivitySessionConnection   adapts engine events to UI JSON
        │
        ▼
SessionEngine                 join, roster, chat, voice, viewport
        │
        ├── Messages / Message / LinkConnection   vendor frames and TCP transport
        └── ViewportProtocol                      screen-share command stream
```

`LinktivitySessionLauncher` chooses the meeting host from the student URL when it looks like
`classN.svuonline.org`, otherwise from the session's zero-based server number. It always uses
port 443 and the vendor's fixed route handshake. The vendor calls that handshake "SSL", but it is
not TLS: no certificate is validated and no cipher is negotiated. The limitation is
documented in [security-privacy.md](security-privacy.md) because it cannot be fixed without
changing the university's session service.

`RetryingSessionConnection` is the resilience layer. It opens an attempt, waits for the engine's
`hello` (the page's join signal), and retries a fixed number of times with a per-attempt timeout.
The UI sees one stable connection throughout, so the chat window opens immediately and keeps its
loader up while attempts run. It emits `retrying` and `joinFailed` events for the page.

`LinktivitySessionConnection` translates engine events into the JSON shapes the chat page
consumes (`hello`, `peers`, `chat`, `sharing`, `retrying`, `joinFailed`). It also owns voice
playback through `VoicePlayback`, which launches and feeds the x86 helper.

### Voice

The tutor voice stream is DSP Group TrueSpeech. The helper extracts each 256-byte payload,
decodes it with the OS ACM codec, and plays it through `waveOut`. Mute and volume are applied to
the helper's own Core Audio session, so they affect only the tutor's audio. The slider runs from
0 to 300%; the session volume stops at 100%, so the helper applies software gain above that and
keeps the session level at 1.0.

The launcher never opens a microphone. The 2017 client had student mute and tutor force-mute, but
starting a broadcast switched every student microphone on by default, and the student audio path
stuttered and produced noise that turned the tutor's voice into gibberish for the whole room.
Mid-session joins and rejoins triggered it; it was not room echo. The beta keeps capture off by
default while audio transfer and sharing are split and the university's backend behavior is
understood. The status bar says the microphone is muted, and `SendVoiceMuteAsync` exists for
protocol completeness rather than student capture.

## Window behaviour

`WindowAnimator` is the single entry point for minimize, maximize, restore, open, and close
transitions. It freezes the current pixels, applies the new window state in one step underneath,
and dissolves the frozen frame out. Nothing animates geometry, so WebView2 resizes exactly once
per command and the content never reflows mid-transition. The full rationale, timings, and
fallback paths are in [window-transitions.md](window-transitions.md).

`WindowResizer` provides edge and corner hit-testing for the borderless windows.
`LayeredOverlay` and `WindowSnapshot` implement the frozen frame.
`WindowActivation` restores foreground behaviour after minimize/restore.

## Failure handling

The app prefers a visible, honest state over a silent failure:

- Network and service faults become friendly status text with a Retry action; technical detail
  goes to the sanitized log.
- Session joins retry without tearing down the chat window.
- Downloads keep per-row state (`Queued`, `Downloading`, `Done`, `Failed`, `Cancelled`) and can
  retry the exact filter step that failed.
- Async commands catch and log; closing an already-closing window is a no-op.
- Raw SOAP replies are captured only during an explicit Diagnostics run, never during normal
  browsing.

## Design constraints

- **Smoothness is the priority.** Transitions are opacity-only; geometry changes happen once.
- **One source of truth.** C# view-models own state; pages render and report input.
- **Local-first layout.** Shared column tracks and one font stack keep English and Arabic rows
  aligned; see `Assets/shared/window-resize.js` and the `--dl-*` variables in `studio.css`.
- **Least surprise.** No silent installs, no background services, no telemetry, no hidden
  network calls. The only outbound connections are the ones a user action requires.
