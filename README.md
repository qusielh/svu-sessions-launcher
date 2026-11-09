# SVU Sessions Launcher

A student-only desktop launcher for Syrian Virtual University online sessions.

This build is a reworked version by [Qusie](https://github.com/qusielh). It is an
unofficial project and is not an official SVU release.

## Features

- Sign in with an SVU student account.
- Browse programmes, courses, and sessions.
- Join a student session with the built-in Linktivity client.
- Use room chat and tutor screen sharing.
- Download session recordings.
- Switch between English and Arabic, light and dark themes.

Tutor/host accounts are intentionally blocked in this version. Notifications and
private messaging are not included.

## Requirements

- Windows 10 or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
- Microsoft Edge WebView2 Runtime to run
- The TrueSpeech ACM codec for tutor voice playback

## Build

```powershell
dotnet build .\SvuSessionsLauncher.slnx -c Release
```

The launcher is written to:

```text
src\SvuSessionsLauncher\bin\Release\net10.0-windows\
```

## Run

```powershell
dotnet run --project .\src\SvuSessionsLauncher\SvuSessionsLauncher.csproj -c Release
```

## Project layout

- `src\SvuSessionsLauncher` - WPF host, WebView2 UI, services, and Linktivity client
- `src\SvuSessionsLauncher\Assets` - login, studio, and chat web assets
- `src\SvuVoiceHelper` - 32-bit TrueSpeech decode and playback helper

## Credits

- Cairo, Lemonada, Orbitron, and Rubik fonts are included under the SIL Open Font License.
- [Microsoft.Web.WebView2](https://learn.microsoft.com/microsoft-edge/webview2/) is used for the UI host.

## Security

- No API keys, passwords, or tokens are stored in this repository.
- Sign-in credentials are entered by the user; the optional "remember password"
  store is encrypted with Windows DPAPI.
- App state, logs, and saved credentials live under `%APPDATA%` and are not
  committed.
- The recording archive client uses the university's public page and does not
  send credentials there.

## License

MIT License. See [LICENSE](LICENSE).
