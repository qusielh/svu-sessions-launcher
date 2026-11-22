# The old launcher and why it was rebuilt

This document records what the 2017 launcher was, what failed in practice, the causes behind
those failures, and how the 3.0.0 rebuild addresses each one. It is meant to be useful to a
reviewer who never saw the old client and to a future maintainer who is tempted to reintroduce
one of its shortcuts.

## What the old launcher was

The original client was a Visual Basic 6 desktop application from 2017. It was a shell around
three external pieces:

- **Microsoft SOAP Toolkit 3.0** for the university's WebDemo service.
- **A bundled Java 6 runtime and a WebDemo applet** for the actual session.
- **The TrueSpeech ACM codec**, installed silently, for tutor voice.

It worked in the sense that a student could sign in and join a lecture. It also carried every
limitation of that stack: a discontinued IDE, a discontinued SOAP toolkit, an end-of-life Java
runtime, fixed Win32 controls, and a session engine that lived outside the application.

The rebuild keeps the university's services and the Linktivity session protocol. It replaces the
client around them.

## What failed, and why

### 1. Starting a session was slow and opaque

**Symptom.** Joining a session added a noticeable delay. The window gave little or no feedback
while it happened, so a slow join looked like a frozen app.

**Cause.** The launcher did not join the session itself. It started a bundled Java 6 runtime,
loaded the WebDemo applet, and waited for the applet to initialize, authenticate, and connect.
That is a cold JVM start plus class loading plus applet setup on every join. VB6 had no
structured progress channel back from the applet, so the UI could not say what was happening.

**Rebuild.** `LinktivitySessionLauncher` creates an in-process `LinktivitySessionConnection`
directly. There is no JVM and no applet. `RetryingSessionConnection` reports each attempt to the
chat page (`retrying`), and the page keeps a loader visible until `hello`. The slow path is now
a network wait with a visible state, not an opaque runtime start.

### 2. The window was fixed and did not fit modern screens

**Symptom.** The main window could not be resized. On high-DPI displays and small laptops it was
either cramped or partly off-screen.

**Cause.** The VB6 form used fixed pixel positions and a non-resizable border. The embedded
applet assumed the same fixed area. There was no per-monitor DPI handling.

**Rebuild.** The studio is a normal resizable WPF window with `WindowResizer` handling every edge
and corner. The web layout is fluid, and the shared column tracks keep the session table aligned
at any width. `app.manifest` declares per-monitor DPI awareness. The viewer adds 1x, 2x, 3x, and
Max zoom so a small screen can still show a readable share.

### 3. Arabic was bolted on

**Symptom.** Arabic text clipped, mixed-direction lines rendered in the wrong order, and some
systems showed locale warnings. English and Arabic did not feel like the same product.

**Cause.** VB6 controls have weak Unicode and bidi support. The layout was built from fixed
left-to-right positions, and the session applet had its own idea of direction. Translation was
treated as replacing strings, not as designing a right-to-left layout.

**Rebuild.** The UI is HTML/CSS with `dir="rtl"` as a first-class mode, the Cairo font for Arabic,
and a shared font stack that keeps Latin data readable inside Arabic screens. `Loc` provides
matched English/Arabic keys, live language switching, and Western digits for dates. The layout
work is documented as a system (shared tracks, one start edge) rather than a one-off fix.

### 4. Microphone and volume behaviour were unsafe

**Symptom.** The microphone was switched on by default when joining or starting a session. An
open mic could echo, pick up room noise, or cut into the tutor's audio. Volume control was the
Windows mixer, not the session.

**Cause.** The applet requested a capture device as part of its join, and the launcher exposed
only a mic-mute checkbox. There was no per-session audio session to control.

**Rebuild.** The student client never opens a microphone. The status bar reports the mic as
muted. Tutor audio is decoded by the x86 helper and played through `waveOut`; mute and a 0–300%
volume slider act on the helper's own Core Audio session, so they affect only the tutor's audio
and nothing else on the machine. `SendVoiceMuteAsync` remains for protocol completeness, not
because the student sends capture audio.

### 5. Saved credentials were weakly protected

**Symptom.** "Remember password" stored the secret in the registry with obfuscation, not real
encryption. Any process running as the same user could recover it.

**Cause.** VB6 had no modern secret-storage API in use, and the original design treated
obfuscation as protection.

**Rebuild.** `DpapiCredentialStore` encrypts the password with Windows DPAPI in current-user
scope and app-specific entropy. The blob lives under `%LOCALAPPDATA%`, never in the repository.
Settings store only the remembered username. The login view-model keeps the password in memory
for the authenticated session and never writes it to the log.

### 6. A dropped connection meant rejoining by hand

**Symptom.** A network drop ended the session with little explanation. The student had to close
everything and join again.

**Cause.** The applet owned the connection and the launcher had no retry state machine. When the
applet process or socket died, the launcher could only report failure.

**Rebuild.** `RetryingSessionConnection` wraps the session in a bounded retry loop with a
per-attempt timeout. The chat window stays open, the composer disables, and the status line says
"Reconnecting..." while attempts run. If all attempts fail, the page gets a `joinFailed` event
with a Retry action. Closing the chat is the only thing that ends the session.

### 7. Errors were raw or missing

**Symptom.** Failures produced either a technical message with no next step or no message at all.
Support had little to work with.

**Cause.** VB6 error handling and the SOAP toolkit surfaced transport faults directly. There was
no mapping from fault to user action and no consistent log.

**Rebuild.** `WebDemoServiceException` carries a known key (`login.error.network`,
`login.error.invalid`, `login.error.tutorBlocked`) or a generic service error. The UI shows the
friendly string and a Retry action where it makes sense; the technical detail goes to the
sanitized log. Session and download failures follow the same pattern.

### 8. The software stack was end-of-life

**Symptom.** The app depended on VB6, the discontinued SOAP Toolkit 3.0, Java 6u45 (2013, long
past public updates), a bundled JRE, and a silent system codec install. Installing it changed
the machine in ways the user did not choose.

**Cause.** The stack was frozen in 2017 and never migrated. Each dependency was convenient at
the time and became a liability later.

**Rebuild.** The launcher targets .NET 10 and WebView2. There is no bundled Java and no silent
install. The only remaining system dependency is the TrueSpeech codec, which is required to play
the university's existing voice stream; the release notes state it instead of installing it
silently.

### 9. Recordings were not available in the app

**Symptom.** Students had to leave the launcher and use the university's recordings page in a
browser.

**Cause.** The original client never implemented the WebForms workflow.

**Rebuild.** `SvuDownloadClient` and `DownloadsViewModel` drive the same public page: term →
programme → course → tutor → class, then a selectable result list with per-row status, progress,
cancellation, retry, and Range-resume downloads. Saved filters are restored on reopen.

### 10. The theme was fixed

**Symptom.** One look, regardless of preference or lighting.

**Cause.** VB6 forms used fixed colours and bitmaps.

**Rebuild.** `ThemeManager` provides light and dark themes, accent presets, a custom accent, and
a matching header/ink choice. Changes apply live to open chat and viewer windows, not only to the
studio.

## What the rebuild deliberately kept

- **The same university services.** The WebDemo SOAP endpoint and the recordings page are
  unchanged. The launcher is a better client, not a replacement back end.
- **The same session protocol.** Linktivity's join, roster, chat, voice, and viewport frames are
  reproduced field for field. The vendor's servers still see a normal student client.
- **The same voice codec.** TrueSpeech is still required because that is what the service sends.
  Moving to Opus needs the university's cooperation and is on the roadmap.
- **Student-only scope.** Tutor and host accounts are blocked in this beta. The tutor experience
  needs its own rebuild.

## Trade-offs that remain

- **Linktivity is not TLS.** The vendor calls its handshake "SSL", but it is a fixed preamble and
  an optional CAST cipher that this client never enables. Session traffic is effectively
  unencrypted. Fixing that requires a protocol change on the university side; the limitation is
  documented in [security-privacy.md](security-privacy.md) rather than hidden.
- **The recordings page is plain HTTP.** The page is public and unauthenticated, but a hostile
  network could observe or alter what is downloaded. The app does not send credentials there.
- **TrueSpeech is old and 32-bit.** It is low quality and requires the x86 helper. The roadmap
  proposes Opus if SVU can carry it.
- **No notifications or private messaging.** These were out of scope for the beta.

## Lessons carried into the codebase

1. **Do not wrap a runtime you cannot control.** The applet was the source of most startup,
   audio, and recovery problems. Speaking the protocol directly removed the layer.
2. **Keep the protocol, replace the runtime.** The vendor wire format is compatibility surface;
   the JVM and VB6 were implementation choices.
3. **Make failure visible.** A retry with a status line is better than a silent hang or a raw
   fault.
4. **Treat credentials as a platform feature.** Use DPAPI; never invent obfuscation.
5. **Design right-to-left, do not translate into it.** Shared tracks and one start edge are
   structural, not cosmetic.
6. **Do not install system components silently.** State the dependency and let the user decide.
7. **Delete what you replace.** Dead applet, SOAP, and mic-mute paths were removed rather than
   left behind behind flags.
