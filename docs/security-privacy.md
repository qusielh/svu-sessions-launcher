# Security and privacy

This document describes what the launcher stores, where it goes, how it is protected, and which
risks are accepted because they belong to the university's services rather than this client. It
is written for reviewers and for anyone deciding whether to trust a release.

## Scope

The launcher is an unofficial student client. It talks to three external systems:

| System | Endpoint | Transport | Authentication |
| --- | --- | --- | --- |
| WebDemo SOAP service | `https://svuis.svuonline.org/SVUIS/WebDemo_wservice/NewSessions2.php?wsdl` | HTTPS | Student username/password as SOAP arguments |
| Recordings page | `http://sessions.svuonline.org/new/` | Plain HTTP | None; public page |
| Linktivity meeting servers | `classN.svuonline.org:443` | Vendor TCP protocol, **not TLS** | Session link id and display name |

There is no telemetry, analytics, crash-reporting service, update checker, or third-party SDK.
The only outbound traffic is what a user action requires.

## Data inventory

| Data | Where it lives | Protection |
| --- | --- | --- |
| Username | `%APPDATA%\SVU Sessions Launcher\settings.json` | Plain text; it is not a secret |
| Password (optional) | `%LOCALAPPDATA%\SVU Sessions Launcher\credentials.dat` | DPAPI, current-user scope, app entropy |
| Language, theme, filters, endpoints | `settings.json` | Plain text |
| Programme/course catalog | `%LOCALAPPDATA%\SVU Sessions Launcher\catalog.json` | Plain text cache |
| Log | `%APPDATA%\SVU Sessions Launcher\error.log` (+ `.previous.log`) | Sanitized before write |
| Raw SOAP replies | `%APPDATA%\SVU Sessions Launcher\responses\` | Only during explicit Diagnostics; cleared at start and capped |
| Downloaded recordings | User-chosen folder, default `Downloads\SVU Sessions` | Plain files; `.part` while downloading |
| WebView2 profile | Runtime-managed under the app's user data folder | Runtime defaults |

The repository contains no credentials, tokens, API keys, or personal data. Screenshots in
`docs/images` have tutor names blurred; the signed-in username is intentionally visible.

## Credentials

### Sign-in

The password is typed into the local sign-in page and posted to `LoginViewModel` as a WebView2
message. It is held in memory for the authenticated session and sent only to the HTTPS SOAP
endpoint as method arguments. The service does not use WS-Security; this is the university's
contract, not a choice made here.

### Remember password

When the user enables "remember password" or auto sign-in, `DpapiCredentialStore` encrypts the
password with `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` and app-specific
entropy. The result is a base64 map keyed by username under `%LOCALAPPDATA%`. Another Windows
account on the same machine cannot decrypt it. Malware running as the same user can, which is
the normal DPAPI boundary and is documented rather than overstated.

Disabling "remember" removes the stored entry. Auto sign-in requires the saved password, so
turning off "remember" also turns off auto sign-in. Auto sign-in runs only on the first prompt at
startup; after an explicit sign-out the user must type the password again, so "sign out" cannot
loop straight back in.

### Password in the local page payload

`LoginWindow.SendState()` includes the current password in the JSON state sent to the local
WebView2 page so the field can be prefilled. This is a local, packaged `file:` page, not a
network response. DevTools, default context menus, and status-bar hints are disabled in
Release, and the window cancels navigation away from `file:`. A debugger attached to the process
could still read the value; that is outside the threat model. The alternative — never echoing
the password back — would break prefill and "show password" without removing the in-memory copy
that sign-in already needs.

## Local paths

`AppPaths` is the only place that composes paths:

- `%APPDATA%\SVU Sessions Launcher`
  - `settings.json`
  - `error.log`, `error.previous.log`
  - `responses\` (diagnostics only)
- `%LOCALAPPDATA%\SVU Sessions Launcher`
  - `credentials.dat`
  - `catalog.json`

Earlier builds used `%LOCALAPPDATA%\SvuSessionsLauncher` for the catalog. `AppPaths` copies that
file once if the new location is empty; the old file is left alone because it is only a cache.

## Logging

`Log.Write` sanitizes every line before it reaches disk:

- URL query strings are stripped (`?wsdl` survives, `?user=...` does not).
- `password`, `pwd`, `secret`, `token`, and `api-key` assignments are masked in `key=value`,
  `key: value`, and JSON/XML shapes.
- XML `<passWord>...</passWord>` elements are masked.
- The WebDemo service's `Not Found <user>` pseudo-row becomes `Not Found <redacted>`.
- Lines are capped at 8,000 characters.
- The file rotates at 1 MB to `error.previous.log`, so it cannot grow without bound.

The log is still local diagnostic data. It can contain hostnames, session ids, class ids, and
error text. Treat it as private when sharing a bug report; the sanitizer removes credentials,
not every operational detail.

## Diagnostics

`SoapWebDemoService.DiagnosticsAsync` sweeps the read methods across option values. Raw replies
can contain account data, so:

- `_diagnostics` is true only for the duration of an explicit Diagnostics run.
- `DumpResponse` is a no-op during normal browsing.
- The dump folder is cleared at the start of each run.
- Output is capped (64 files, 1,000,000 characters).

Diagnostics is a developer/support tool, not a background feature. A user who runs it should
still treat the folder as sensitive and delete it afterwards.

## WebView2 hardening

All three surfaces (sign-in, studio, chat) set:

- `AreDevToolsEnabled = false`
- `AreDefaultContextMenusEnabled = false`
- `IsStatusBarEnabled = false`

The studio cancels any navigation that is not `file:`. The sign-in page is local and has no
external navigation. The chat page allows `http`/`https` links, validates the URI, and opens them
with the default browser instead of navigating the embedded view. New-window requests follow the
same rule.

The shared WebView2 environment passes Chromium switches that stop background/occluded windows
from being throttled. It does not open a remote-debugging port.

## Network and transport limits

These are properties of the university's services and the vendor protocol, not client features
that were skipped. Where the client can protect the user without breaking interoperability, it
does; where it cannot, the constraint is stated here.

### WebDemo SOAP: HTTPS

The live endpoint is HTTPS and the client validates the certificate through the platform HTTP
stack. Credentials travel as SOAP arguments, as the service requires. There is no custom
certificate bypass.

### Recordings page: plain HTTP

The university's recordings page is served over plain HTTP. The client uses it because that is
the only published interface. The page is unauthenticated and the client sends no credentials to
it. The risk is network observation or tampering with the downloaded recordings on a hostile
network. Recommendations:

- Prefer a trusted network for large downloads.
- Verify the file plays and matches the expected session.
- Do not treat downloaded files as trusted executables; they are media files with a `.lrec`
  extension.

The downloader sanitizes file and folder names, strips path separators, avoids reserved Windows
names, and resolves collisions with a numeric suffix. Downloads resume with HTTP Range requests
and a `.part` file; a failed download never overwrites a completed one.

### Linktivity: vendor protocol, not TLS

This is the most important limitation. The vendor calls its handshake "SSL", but that name is
misleading:

- The handshake is a fixed client/server preamble, not a TLS negotiation.
- No certificate is validated.
- The client implements no cipher, so session traffic is unencrypted.

Anyone on the network path could observe or alter session traffic. Fixing this requires the
university's session service to support real transport security; the client cannot add TLS
without breaking interoperability. The limitation is stated here, in the README, and in the
release notes so users can make an informed choice.

### DNS and hosts

Meeting hosts are derived from the student URL when it looks like `classN.svuonline.org`;
otherwise from the session's server number. The client does not accept an arbitrary host from
the page. The SOAP endpoint is configurable in `settings.json` for development; a release user
should leave it at the default.

## Microphone and audio privacy

The 2017 client had student mute and tutor force-mute, but starting a broadcast switched every
student microphone on by default. Tutors had to re-mute students constantly, and the student
audio path itself stuttered and produced noise that turned the tutor's voice into gibberish for
every student in the room. Mid-session joins and rejoins triggered it; it was not room echo.

This beta does not open a capture device. The full mute is temporary: student audio stays off
until audio transfer and sharing are split and the university's backend behavior is understood.

- The launcher never opens a capture device.
- The student client never transmits microphone audio.
- `SvuVoiceHelper.exe` is decode/playback only.
- Mute and volume apply to the helper's own Core Audio session, not the system mixer.
- The status bar reports the microphone as muted so the behaviour is visible, not silent.

## Release integrity

- Releases are framework-dependent zips; they do not bundle the .NET runtime or Java.
- `build-release.ps1` removes WebView2 platform folders the x64 build does not use and strips
  XML documentation files, keeping the asset lean.
- The build prints a SHA256 for the zip. Publish that hash with the release and compare it after
  download.
- The executable is not code-signed, so Windows SmartScreen may warn on first run. That is a
  distribution limitation, not a reason to disable system protections.

## Threat model

**Defended against**

- Another local Windows account reading the saved password (DPAPI).
- Casual credential leakage through a shared log (sanitization).
- A compromised or malicious web response navigating the host or opening DevTools (navigation
  guards, DevTools off, external links validated).
- Path traversal or reserved-name collisions in downloaded file names.
- Silent system modification (no bundled runtime, no silent codec install).
- Background data collection (no telemetry, no update pings).

**Not defended against**

- Malware running as the same user (it can use DPAPI and read process memory).
- A network attacker on the Linktivity path (no TLS) or the recordings path (plain HTTP).
- A compromised university server or a malicious meeting host.
- Physical access to an unlocked machine.
- Screen capture by other software.
- A user who runs Diagnostics and shares the raw dump folder.

## Verification checklist

Before publishing a release:

1. `dotnet build SvuSessionsLauncher.slnx -c Release` is clean (0 warnings, 0 errors).
2. `rg -n "password|secret|token|api[_-]?key"` over the source shows only masked logging,
   credential handling, and protocol field names — no hard-coded secrets.
3. `git status` contains no `settings.json`, `credentials.dat`, `error.log`, `responses/`, or
   `catalog.json`.
4. DevTools is false in `LoginWindow`, `MainStudioWindow`, and `ChatWindow`.
5. Raw response dumps are gated on `_diagnostics` and the folder is cleared at the start.
6. The release zip contains no `.pdb`, no XML docs, no arm64/x86 WebView2 runtimes, and no local
   WebView2 profile.
7. The published SHA256 matches the built zip.
8. README screenshots render from the repository and show blurred tutor names.

## Reporting a security issue

Do not open a public issue with credentials, raw logs, or personal data. Contact the maintainer
through the repository's private reporting channel, or open a minimal issue that says a private
report is available. Include the launcher version, the affected surface, and reproduction steps
without real account data.
