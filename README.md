# SVU Sessions Launcher

**Beta 3.0.0-beta.1** - a student-only desktop launcher for Syrian Virtual University online sessions.

This is an unofficial, open-source project. It gives SVU students one Windows app for the
session workflow: sign in, browse programmes and courses, join live sessions, use room chat
and screen sharing, and download recordings. It exists to make those everyday tasks quicker
and less fragmented than moving between browser pages, and to keep the experience consistent
in English and Arabic.

> **Beta software.** The main flows work, but features may change and bugs are expected.
> Feedback, bug reports, and suggestions are welcome on
> [GitHub Issues](https://github.com/qusielh/svu-sessions-launcher/issues). If the project
> is useful to you, please star the repository.

## Download

The latest beta build is on the [Releases](https://github.com/qusielh/svu-sessions-launcher/releases)
page. See [Requirements](#requirements) for the runtimes it needs.

## Documentation

- [Architecture](docs/architecture.md) - process model, startup, windows, services, and the
  session stack.
- [Code map](docs/code-map.md) - every folder and file, plus a reviewer's reading route.
- [Legacy launcher](docs/legacy-launcher.md) - what failed in 2017, why, and how this rebuild
  solved it.
- [Security and privacy](docs/security-privacy.md) - credentials, local data, logging, and
  transport limits.
- [Release workflow](docs/release-workflow.md) - versioning, packaging, publishing, and patches.
- [Window transitions](docs/window-transitions.md) - the dissolve system in detail.

## Screenshots

![The old 2017 launcher and the 3.0.0 beta side by side](docs/images/compare.png)

| English | العربية |
| --- | --- |
| ![SVU Sessions Launcher, English](docs/images/new-launcher-en.png) | ![مشغل الجلسات المتزامنة، العربية](docs/images/new-launcher-ar.png) |

Tutor names are blurred in all screenshots. The old screenshot is the 2017 VB6 client; the new
screenshots show the current 3.0.0 beta.

## Why the launcher was rebuilt

The original launcher was written in 2017 on VB6, the discontinued Microsoft SOAP Toolkit,
and a bundled Java 6 runtime. It worked, but it carried the limitations of that stack. This
version keeps the same university services and session engine while replacing the client
around them. The full account of what failed, the cause of each failure, and how this rebuild
addresses each one is in [docs/legacy-launcher.md](docs/legacy-launcher.md).

| | Old launcher (2017) | 3.0.0 beta |
| --- | --- | --- |
| Starting a session | Added a noticeable delay: VB6 started a bundled Java 6 runtime and a WebDemo applet, with no progress while it loaded. | Connects to the room directly and reports each step. |
| Arabic | Bolted onto fixed VB6 controls: clipped text, garbled messages, and locale warnings on some systems. | Full right-to-left layout with the Cairo font, live language switching, and matching English/Arabic text. |
| Window size | Fixed; resizing was disabled. | Resizable main window and viewer, with 1x, 2x, 3x, and Max zoom. |
| Volume | Only the Windows mixer, through a mic-mute checkbox. | Per-session audio with mute and a 0-300% volume slider. |
| Microphone | Switched on by default when joining or starting a session, so an open mic could echo or cut into the tutor's audio. | Muted automatically. The student client never transmits microphone audio, and the status bar reports it muted. |
| Software | VB6, SOAP Toolkit 3.0, Java 6u45 (2013, end of life), a bundled JRE, and a silent system codec install. | .NET 10 and WebView2, with no bundled Java and no silent system installs. |
| Saved credentials | Registry values with weak obfuscation. | Windows DPAPI, current-user scope. |
| Connection loss | No launcher-level recovery; a drop meant rejoining by hand. | Automatic reconnect with a retry budget, offline waiting, and chat kept open. |
| Errors | Raw or missing feedback. | Friendly messages with Retry actions; technical details stay in the log. |
| Theme | Fixed. | Light and dark themes, accent presets, and live re-theming of open windows. |
| Recordings | Not available. | An in-app recording browser with filters, a download queue, progress, and Retry. |

### Other fixes in this beta

- Student microphones are muted automatically. The student client never sends microphone
  audio, so an open mic cannot echo or interrupt the tutor during a lecture.
- Chat stays open through a network drop. The composer disables, the room stays visible, and
  the status line shows "Reconnecting..." instead of failing silently.
- The screen-share viewer keeps its size, supports 2x and 3x even on small screens, and hides
  cleanly when a share ends.
- Maximize, restore, and minimize use one quick dissolve instead of a slow geometry animation,
  so the page and screen share never stutter. Every window edge and corner resizes.
- Downloads show a clear state for every row (Queued, Done, Failed, Cancelled) and can retry
  the exact filter step that failed.
- Theme and accent changes apply immediately to open chat and viewer windows, even when they
  are in the background.
- The session list has proper loading, empty, no-results, and error states, with Retry where
  it makes sense.
- Closing a window that is already closing no longer raises an unobserved exception, and
  failed async commands are logged instead of disappearing.
- English and Arabic stay in parity, and Arabic month names render with Western digits.

## Features

- Sign in with an SVU student account.
- Browse programmes, courses, and sessions.
- Join a student session with the built-in Linktivity client.
- Use room chat and tutor screen sharing.
- Download session recordings.
- Switch between English and Arabic, light and dark themes.

Tutor and host accounts are intentionally blocked in this version. Notifications and private
messaging are not included yet.

## Beta limitations

- This is a student client. The tutor side still needs to be rebuilt before tutor accounts can
  be supported.
- Student microphones are muted by design; there is no student voice channel in this beta.
- Tutor voice still uses the university's TrueSpeech stream, so the TrueSpeech ACM codec is
  required for playback.
- There is no notification system and no private messaging.
- Bugs are expected. Please include the log file and the steps that led to the problem when
  you report one.

## Roadmap

The next improvements depend partly on the university. If SVU is willing to collaborate, some
of these can become official integrations; if not, the project will keep improving them
independently.

- **Move from TrueSpeech to a modern codec such as Opus.** TrueSpeech is old, low quality, and
  only available as a 32-bit Windows codec. Opus would be clearer, smaller, and easier to
  support on modern systems. This needs the university's session service to carry the codec.
- **Rebuild the tutor side.** The student flow is the focus of this beta. A proper tutor
  experience needs host controls, participant management, and the same reliability work.
- **Bring back student voice properly.** Student microphones stay muted for now so nobody can
  echo or cut into the tutor's audio. With SVU's collaboration, voice could return with
  push-to-talk, a moderator mute, and a clear speaking indicator instead of an always-on mic.
- **Add a real notification system.** Session starting, a new screen share, a finished
  download, and connection problems should reach the user even when the launcher is in the
  background.
- **Work with the university, or continue independently.** The launcher is unofficial and not
  endorsed by SVU. Collaboration would make codec and notification changes much easier, but
  the project can continue on its own if that is not possible.

## Requirements

- Windows 10 or Windows 11 (64-bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64) to run a prebuilt release
- The .NET 10 Runtime (x86) for tutor voice playback
- Microsoft Edge WebView2 Runtime
- The TrueSpeech ACM codec for tutor voice playback

## Build

```powershell
dotnet build .\SvuSessionsLauncher.slnx -c Release
```

The launcher is written to:

```text
src\SvuSessionsLauncher\bin\Release\net10.0-windows\
```

## Package a release

```powershell
.\build-release.ps1
```

This writes `dist\SVU-Sessions-Launcher-<version>-win-x64.zip` and prints its SHA256.

## Run

```powershell
dotnet run --project .\src\SvuSessionsLauncher\SvuSessionsLauncher.csproj -c Release
```

## Project layout

```text
src\SvuSessionsLauncher\        WPF host (.NET 10, x64)
  App.xaml.cs                   startup, services, login -> studio -> session loop
  Assets\                       login, studio, chat pages; shared resize helper; fonts
  Helpers\                      dissolve transitions, snapshots, edge/corner resize
  Linktivity\                   in-process vendor protocol and session engine
  Localization\                 English/Arabic strings and formatting
  Models\                       programme, course, session, and download records
  Services\                     SOAP, catalog cache, credentials, logging, downloads, voice
  Theming\                      theme, accent, header, and ink palettes
  ViewModels\                   studio, sign-in, and downloads state
  Views\                        WPF window hosts for login, studio, chat, viewer
src\SvuVoiceHelper\             32-bit TrueSpeech decode/playback helper
docs\                           architecture, code map, legacy history, security, releases, images
```

See [docs/code-map.md](docs/code-map.md) for a per-file description and a reviewer's reading
order.

## Security

- No API keys, passwords, or tokens are stored in this repository.
- Sign-in credentials are entered by the user. The optional "remember password" store is
  encrypted with Windows DPAPI in current-user scope and lives under `%LOCALAPPDATA%`.
- Settings, logs, and diagnostic dumps live under `%APPDATA%\SVU Sessions Launcher`; the catalog
  and credential blob live under `%LOCALAPPDATA%\SVU Sessions Launcher`. None are committed.
- Logs are sanitized before they reach disk: URL query strings are stripped, password-shaped
  values and XML password elements are masked, and unknown usernames are redacted. The log
  rotates at 1 MB.
- WebView2 DevTools and default context menus are disabled in the sign-in, studio, and chat
  surfaces. The studio only navigates to local `file:` pages; the chat opens validated
  `http`/`https` links in the default browser.
- The recording archive client uses the university's public page and does not send
  credentials there. That page is served over plain HTTP.
- Linktivity session traffic uses the vendor's fixed handshake, not TLS. The optional CAST cipher
  is never enabled. This is a limitation of the university's session service; it is documented
  in [docs/security-privacy.md](docs/security-privacy.md) rather than hidden.
- Student microphones are never opened or transmitted. Tutor audio is decoded by the x86 helper;
  mute and volume apply only to that helper's audio session.

Full data-flow, threat-model, and verification details are in
[docs/security-privacy.md](docs/security-privacy.md).

## Credits

- Cairo and Rubik fonts are included under the SIL Open Font License.
- [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) is used for
  the UI host.

## License

MIT License. See [LICENSE](LICENSE).
