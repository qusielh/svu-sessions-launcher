# Code map

This is the reviewer's index to the repository. Each entry says what the file is for and when you
would open it. For the flow-level picture, read [architecture.md](architecture.md) first.

## Reviewer's route

If you have limited time, read in this order:

1. `src/SvuSessionsLauncher/App.xaml.cs` — startup and the login → studio → session loop.
2. `src/SvuSessionsLauncher/ViewModels/MainViewModel.cs` — what the studio can do.
3. `src/SvuSessionsLauncher/Views/MainStudioWindow.xaml.cs` — the C# ↔ web bridge.
4. `src/SvuSessionsLauncher/Services/LinktivitySessionLauncher.cs` and
   `RetryingSessionConnection.cs` — how a session is opened and kept alive.
5. `src/SvuSessionsLauncher/Linktivity/SessionEngine.cs` — the in-process session client.
6. `src/SvuSessionsLauncher/ViewModels/DownloadsViewModel.cs` and
   `Services/SvuDownloadClient.cs` — the recordings workflow.
7. `src/SvuSessionsLauncher/Services/Log.cs` and `CredentialStore.cs` — the security-sensitive
   edges.
8. `docs/legacy-launcher.md` — why the rebuild exists.

## Repository root

| Path | Purpose |
| --- | --- |
| `README.md` | User-facing overview, screenshots, requirements, build, installer, and release commands. |
| `LICENSE` | MIT licence, copyright Qusie. |
| `SvuSessionsLauncher.slnx` | Solution file; contains the launcher and the voice helper. |
| `build-release.ps1` | Publishes the framework-dependent x64 build, trims it, writes `README.txt`, zips it, and writes `SHA256SUMS.txt`. |
| `build-installer.ps1` | Runs the release build, then compiles the Inno Setup installer and writes both hashes. |
| `installer/SvuSessionsLauncher.iss` | Per-user installer definition: shortcuts, uninstaller, runtime checks, and install flow. |
| `installer/BeforeInstall.txt` | Requirements and beta notes shown before installation. |
| `.gitignore` | Excludes build output, local state, logs, captures, and release artifacts. |
| `docs/` | Architecture, code map, legacy history, security, release workflow, window transitions, screenshots. |
| `src/SvuSessionsLauncher/` | The WPF launcher. |
| `src/SvuVoiceHelper/` | The 32-bit TrueSpeech decode/playback helper. |

## `src/SvuSessionsLauncher` — host

| Path | Purpose |
| --- | --- |
| `SvuSessionsLauncher.csproj` | Target framework, version, icon, manifest, asset copy rules, package references. |
| `App.xaml` / `App.xaml.cs` | Application resources and the composition root: services, theme, login loop, session wiring. |
| `AssemblyInfo.cs` | WPF theme/resource metadata. |
| `app.manifest` | DPI awareness, supported OS versions, and per-monitor behaviour. |
| `Themes/Theme.xaml` | Shared WPF brushes, styles, and control templates. |
| `Theming/ThemeManager.cs` | Parses theme/accent/header/ink settings, builds palettes, applies fonts, and broadcasts changes. |

### `Assets/` — the web UI

| Path | Purpose |
| --- | --- |
| `Assets/app.ico` | Application icon. |
| `Assets/login/index.html`, `login.css`, `login.js` | The sign-in page. Fixed 980×660 layout, no scrolling, error state that fits, local-only assets. |
| `Assets/studio/index.html`, `studio.css`, `studio.js` | The main studio: session list, filters, appearance panel, downloads table, launch door. |
| `Assets/chat/index.html`, `app.css`, `app.js` | The in-session chat, roster, screen-share state, reconnecting state, and composer. |
| `Assets/shared/window-resize.js` | Shared left/right/bottom edge and corner hit-testing that posts `resize` actions to the host. |
| `Assets/Fonts/` | Cairo and Rubik families with their OFL licence files. Cairo is the Arabic-first stack; Rubik is the Latin display face. |

The pages are plain HTML/CSS/JS with no build step and no external CDN. That keeps the release
lean and means the UI works offline.

### `Helpers/` — native window behaviour

| Path | Purpose |
| --- | --- |
| `WindowAnimator.cs` | The only entry point for minimize, maximize, restore, open, and close transitions. Applies state once, then dissolves the frozen frame. |
| `WindowSnapshot.cs` | Captures the window's pixels for the frozen frame. |
| `LayeredOverlay.cs` | A click-through layered window that presents snapshot frames at a constant opacity. |
| `WindowResizer.cs` | Edge and corner resize hit-testing and drag loop for borderless windows. |
| `WindowActivation.cs` | Restores foreground/taskbar behaviour after minimize and restore. |

### `Linktivity/` — the vendor protocol

| Path | Purpose |
| --- | --- |
| `LinkConnection.cs` | TCP transport, optional proxy, route negotiation, receive thread, `MessageReceived` / `Failed` events. |
| `Connection.cs` | Transport constants and the vendor's fixed "SSL" preamble. Not TLS. |
| `Message.cs` | One 16-byte-header wire frame, flags, compression, and obfuscation helpers. |
| `Messages.cs` | Builders and parsers for join, heartbeat, roster, chat, voice, and viewport frames. Field order is protocol-critical. |
| `SessionEngine.cs` | The student session: join, roster, public chat, voice frames, viewport dispatch, and lifecycle. |
| `ViewportProtocol.cs` | Decodes the screen-share command stream into a persistent BGRA buffer. |
| `BufferConversion.cs` | Little-endian integers and length-prefixed strings for the wire format. |
| `CastCipher.cs` | Port of the vendor CAST cipher. Present for protocol completeness; never enabled by this client. |
| `CipherTables.cs` | Generated S-box data for `CastCipher`. No logic. |

### `Localization/` and `Models/`

| Path | Purpose |
| --- | --- |
| `Localization/Loc.cs` | English and Arabic string tables, formatting, language detection, and `LanguageChanged`. |
| `Models/SvuModels.cs` | Immutable records for programmes, courses, sessions, auth results, launch requests, and downloads. |

### `Services/` — data, state, and platform edges

| Path | Purpose |
| --- | --- |
| `AppPaths.cs` | The single source of truth for settings, log, diagnostic dumps, catalog, and credential paths; migrates the legacy catalog once. |
| `AppSettings.cs` | Non-secret persisted preferences. Never stores a password. |
| `CredentialStore.cs` | `ICredentialStore` and the DPAPI current-user implementation. |
| `Log.cs` | Sanitized, rotating file logger. Strips URL queries, masks password-shaped values, redacts unknown usernames. |
| `DisplayOptions.cs` | Process-wide display preferences (currently 12/24-hour clock) with a change event. |
| `IWebDemoService.cs` | The WebDemo contract and `WebDemoServiceException`. |
| `SoapWebDemoService.cs` | Live RPC/encoded SOAP 1.1 client; diagnostics-only raw reply dumps. |
| `CachedWebDemoService.cs` | Local catalog cache with background refresh and one-programme prefetch. |
| `MockWebDemoService.cs` | Deterministic offline data for UI development. |
| `ISessionConnection.cs` | The narrow session surface the chat/viewer depend on, plus `ViewportFrame`. |
| `ISessionLauncher.cs` | Session-opening contract and `SessionLaunchException`. |
| `LinktivitySessionLauncher.cs` | Host derivation, port 443, retry wrapper construction. |
| `LinktivitySessionConnection.cs` | Adapts engine events to the chat page's JSON; owns voice playback. |
| `RetryingSessionConnection.cs` | Silent join retries, per-attempt timeout, synthetic `retrying` / `joinFailed` events. |
| `VoicePlayback.cs` | Launches and feeds the x86 helper; mute and volume commands on a separate pipe. |
| `SvuDownloadClient.cs` | WebForms scraping for the recordings page: cookies, hidden fields, cascade posts, row parsing, Range-resume downloads. |
| `RangeObservableCollection.cs` | `ObservableCollection` with an efficient range add. |
| `WebViewRuntime.cs` | One shared WebView2 environment, prewarmed at startup. |

### `ViewModels/`

| Path | Purpose |
| --- | --- |
| `MainViewModel.cs` | Studio state: filters, modes, session list, selection, launch, appearance, diagnostics, status text. |
| `LoginViewModel.cs` | Sign-in fields, busy/error state, auto sign-in, credential persistence. |
| `DownloadsViewModel.cs` | Downloads window: WebForms cascade, filter restore, row selection, queue, progress, cancellation, file naming. |
| `ObservableObject.cs` | Minimal `INotifyPropertyChanged` base. |
| `RelayCommand.cs` | Synchronous and asynchronous `ICommand` with can-execute refresh. |

### `Views/`

| Path | Purpose |
| --- | --- |
| `LoginWindow.xaml` / `.xaml.cs` | Fixed sign-in window; hosts the login page; posts state and receives input. |
| `MainStudioWindow.xaml` / `.xaml.cs` | Studio window; WebView bridge, resize grips, downloads state, theme/language pushes, navigation guard. |
| `ChatWindow.xaml` / `.xaml.cs` | Session window; prewarm/reveal, chat/voice/leave actions, connection event relay, external-link guard. |
| `ViewerWindow.xaml` / `.xaml.cs` | Screen-share viewer; BGRA frame copy, 1x/2x/3x/Max zoom, independent lifetime. |

## `src/SvuVoiceHelper` — 32-bit audio helper

| Path | Purpose |
| --- | --- |
| `Program.cs` | Reads length-prefixed records from stdin, decodes frames, and dispatches playback/control commands. |
| `TrueSpeechDecoder.cs` | Wraps the OS TrueSpeech ACM codec. |
| `WavePlayer.cs` | `waveOut` playback with a small buffer queue. |
| `SessionVolume.cs` | Core Audio per-process mute and volume for the helper's own session. |
| `Native.cs` | P/Invoke declarations for ACM, `waveOut`, and Core Audio. |
| `SvuVoiceHelper.csproj` | x86 target, framework-dependent, invariant globalization. |

## Conventions

- **Namespaces follow folders.** `SvuSessionsLauncher.Services`, `.ViewModels`, `.Views`,
  `.Linktivity`, `.Helpers`, `.Theming`, `.Localization`, `.Models`.
- **Vendor-facing files keep vendor names and field order.** `Messages.cs`, `Message.cs`, and
  `ViewportProtocol.cs` are close to the captured protocol on purpose; do not "clean up" the
  ordering.
- **One concern per service.** Network, persistence, logging, and window behaviour each have a
  single owner.
- **Comments explain why, not what.** Protocol quirks, timing constraints, and failure
  trade-offs are documented at the point where they matter.
- **No dead code.** Removed features are deleted rather than left behind behind flags.
