# The old launcher and why it was rebuilt

This document records the 2017 launcher as students met it, the failures that mattered in daily
use, their causes, and how the 3.0.0 rebuild responds. It is written for a reviewer who never saw
the old client and for a maintainer who might be tempted to reintroduce one of its shortcuts.

## The starting point

The rebuild began with a compiled application and an installer, not a repository. The installed
client identifies itself as "SVU Online Sessions Launcher" 2.00.1000; `SvuSessionLauncher.exe`
reports version 2.00.0001. There was no source code, no build script, and no protocol
documentation in the package.

That changes what "rebuild" means here. This is not a fork, a patch, or a copy of the original
source. The new client was written independently: the university's public SOAP service and
recordings page were used as published, and the Linktivity session protocol was reproduced from
the outside so the vendor's servers see a normal student client. The old executable was used to
observe behaviour and compare results, not as a source of code.

## What the old client was

The original client was a Visual Basic 6 desktop application from 2017. It was a shell around
three external pieces:

- **Microsoft SOAP Toolkit 3.0** for the university's WebDemo service.
- **A bundled Java 6 runtime and a WebDemo applet** for the actual session.
- **The TrueSpeech ACM codec**, installed silently, for tutor voice.

It worked in the sense that a student could sign in and join a lecture. It also carried every
constraint of that stack: a discontinued IDE, a discontinued SOAP toolkit, an end-of-life Java
runtime, fixed Win32 controls, and a session engine that lived outside the application.

The old client also had a host/tutor mode. A tutor could host a session, not only join one. The
beta does not pretend that mode never existed; it blocks tutor and host accounts because the
host controls, participant management, and tutor flow have not been rebuilt yet. That is a scope
decision, not a claim about the old feature.

The rebuild keeps the university's services and the Linktivity session protocol. It replaces the
client around them.

## What failed, and why

### 1. Joining a session took about 21 seconds

**Symptom.** In a cold run on the same machine and the same session, the 2017 client took
**20.9 seconds** from the Launch click to the meeting window appearing. The Java process appeared
3.9 seconds after the click. In a separate cold start, the launcher's first window took 8.5
seconds to appear after the executable started. There was no useful progress at any point, so a
slow join looked like a frozen app.

**Cause.** The launcher did not join the session itself. It unpacked a bundled Java 6 runtime
(about 109.5 MB in `%TEMP%\SVU2014`), started a JVM, loaded the WebDemo applet, and waited for
the applet to initialize, authenticate, and connect. That is a cold JVM start plus class loading
plus applet setup on a cold join. VB6 had no structured progress channel back from the applet,
so the UI could not say what was happening.

**Why it mattered.** The delay landed at the start of a lecture. Students joined late, clicked
Launch again because they thought the first click had failed, or gave up and watched the
recording later. A 20-second opaque wait is long enough to change what people do.

**Rebuild.** `LinktivitySessionLauncher` creates an in-process `LinktivitySessionConnection`
directly. There is no JVM and no applet. `RetryingSessionConnection` reports each attempt to the
chat page (`retrying`), and the page keeps a loader visible until `hello`. In the latest run the
new client completed the join in about **0.2 seconds**; an earlier join over a slower path took
about **6–8 seconds**. The network still sets the floor, but the client no longer adds a runtime
start to it.

The comparison is join to join. The old client's cold path includes unpacking its bundled
runtime, which is the path on a first run or after a temp cleanup; the new client has no bundled
runtime to unpack. A warm old join would skip the unpack but still start the JVM and applet.

### 2. The window was locked, and the viewer had no zoom

**Symptom.** The main window was fixed, the chat applet was fixed, and the screen-share viewer
stayed at the broadcast's dimensions. None of them could be resized, and the viewer had no zoom
of its own. A tutor who shared a small window or a low-resolution slide left students with two
workarounds: lower the Windows display resolution so everything got bigger, or run Magnifier over
the shared area. Lowering the resolution rearranged every other window and had to be undone after
the lecture; Magnifier followed the cursor, so the slide drifted out of view whenever the mouse
moved.

**Cause.** The old client used fixed pixel layouts and non-resizable windows. The embedded
applet assumed the same fixed area, and the viewer had no zoom or scale control, so it stayed at
the broadcast's own size.

**Why it mattered.** The shared content is the lecture. A student could read a small share, but
only by leaving the lecture surface for a system tool. This happened often enough to be a normal
part of using the client, not an edge case.

**Rebuild.** The studio is a normal resizable WPF window with `WindowResizer` handling every edge
and corner. The web layout is fluid, and the shared column tracks keep the session table aligned
at any width. `app.manifest` declares per-monitor DPI awareness. The viewer adds Reset, 2x, 3x,
and Max presets and can be enlarged independently of the chat. The enlargement happens inside the
viewer, so the desktop layout and the other windows stay as they were. The old client did not
leave students with nothing; it left them with system-level workarounds. The rebuild moves the
same need into the window where the lecture is.

### 3. Arabic was bolted on

**Symptom.** Arabic text clipped, mixed-direction lines rendered in the wrong order, and some
systems showed locale warnings. English and Arabic did not feel like the same product.

**Cause.** VB6 controls have weak Unicode and bidi support. The layout was built from fixed
left-to-right positions, and the session applet had its own idea of direction. Translation was
treated as replacing strings, not as designing a right-to-left layout.

**Why it mattered.** The interface is where students read session names, tutor names, times, and
errors. Clipped labels and reversed lines made ordinary tasks harder exactly when the student
was already dealing with a problem.

**Rebuild.** The UI is HTML/CSS with `dir="rtl"` as a first-class mode, the Cairo font for Arabic,
and a shared font stack that keeps Latin data readable inside Arabic screens. `Loc` provides
matched English/Arabic keys, live language switching, and Western digits for dates. The layout
work is documented as a system (shared tracks, one start edge) rather than a one-off fix.

### 4. The student microphone broke the tutor's audio

**Symptom.** The old client had mute controls: students could mute themselves, and tutors could
force-mute a student. The failure was around broadcasts. Starting a broadcast switched every
student microphone on by default, so tutors had to re-mute students constantly and lost focus on
the lecture. The student audio path was also broken: it stuttered and produced noise, and that
noise turned the tutor's voice into gibberish for every student in the room. It was not room
echo. Mid-session joins and rejoins triggered it, so one student arriving late could damage the
audio for everyone.

**Cause.** The applet owned the session, and the broadcast start reset the room's microphone
state. The client exposed mute and force-mute, but not a way to keep microphones off when a
broadcast began. The student audio path itself was unstable, and its failure corrupted the
tutor's audio for the whole room.

**Why it mattered.** Tutors spent the lecture policing mutes instead of teaching. A single late
join or rejoin could make the tutor unintelligible for everyone, and the students who were
trying to listen could not fix it.

**Rebuild.** This beta keeps the microphone off by default and never opens a capture device. The
status bar reports the mic as muted. Tutor audio is decoded by the x86 helper and played through
`waveOut`; mute and a 0–300% volume slider act on the helper's own Core Audio session, so they
affect only the tutor's audio and nothing else on the machine. The full mute is temporary:
student audio stays off until audio transfer and sharing are split and the university's backend
behavior is understood. `SendVoiceMuteAsync` remains for protocol completeness, not because the
student sends capture audio.

Tutor volume is the other half of the audio story. The applet had its own slider and Windows had
the system mixer, but both stopped at 100%. When a tutor's microphone was quiet at the source,
students wore headphones or installed a booster to hear the lecture. The beta's slider runs to
300% and acts on the helper's own audio session, so a quiet tutor can be raised without changing
the system volume or adding another program. It also raises background noise at high settings,
which is the trade; it cannot fix a bad microphone at the tutor's end.

### 5. Saved credentials were weakly protected

**Symptom.** "Remember password" stored the secret in the registry with obfuscation, not real
encryption. Any process running as the same user could recover it.

**Cause.** VB6 had no modern secret-storage API in use, and the original design treated
obfuscation as protection.

**Why it mattered.** Students reuse passwords, and a shared or infected machine made the
exposure real. The stored value protected the university account, not just the launcher.

**Rebuild.** `DpapiCredentialStore` encrypts the password with Windows DPAPI in current-user
scope and app-specific entropy. The blob lives under `%LOCALAPPDATA%`, never in the repository.
Settings store only the remembered username. The login view-model keeps the password in memory
for the authenticated session and never writes it to the log.

### 6. A dropped connection meant rejoining by hand

**Symptom.** A network drop ended the session with little explanation. The student had to close
everything and join again, paying the cold-join cost a second time. Errors were either raw
transport text or nothing at all.

**Cause.** The applet owned the connection and the launcher had no retry state machine. When the
applet process or socket died, the launcher could only report failure.

**Why it mattered.** A short Wi-Fi hiccup could cost the rest of a lecture. Students retried
blindly or gave up, and support had little to work with.

**Rebuild.** `RetryingSessionConnection` wraps the session in a bounded retry loop with a
per-attempt timeout. The chat window stays open, the composer disables, and the status line says
"Reconnecting..." while attempts run. If all attempts fail, the page gets a `joinFailed` event
with a Retry action. Closing the chat is the only thing that ends the session.

### 7. Errors were raw or missing

**Symptom.** Failures produced either a technical message with no next step or no message at
all. A wrong password, a down server, and a local network problem looked the same.

**Cause.** VB6 error handling and the SOAP toolkit surfaced transport faults directly. There was
no mapping from fault to user action and no consistent log.

**Why it mattered.** Students could not tell what to fix, so they retried blindly or gave up.
Support could not reconstruct the failure after the fact.

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

**Why it mattered.** The runtime no longer received security updates, the install made silent
system changes, and compatibility with modern Windows depended on a shrinking set of machines.
The bundled runtime also made the cold join large and slow.

**Rebuild.** The launcher targets .NET 10 and WebView2. There is no bundled Java and no silent
install. The only remaining system dependency is the TrueSpeech codec, which is required to play
the university's existing voice stream; the requirements state it instead of installing it
silently.

### 9. Recordings required a browser, a second sign-in, and a manual search

**Symptom.** To watch a recording, a student left the launcher, opened a browser, signed in to
the university portal, and worked through the recordings page: term → programme → course → tutor
→ class, then a result list. The old client had no recordings UI, so this was the only path.

**Cause.** The original client never implemented the WebForms workflow behind the recordings
page.

**Why it mattered.** The task was common after a missed lecture, when motivation was lowest. A
student had to remember where to go, sign in again, and repeat a five-step search; a wrong
choice produced an empty list with no explanation.

**Rebuild.** `SvuDownloadClient` and `DownloadsViewModel` drive the same public page: term →
programme → course → tutor → class, then a selectable result list with per-row status, progress,
cancellation, retry, and Range-resume downloads. Saved filters are restored on reopen. No
credentials are sent to the recordings page. The browser trip, the separate sign-in, and the
manual search are gone.

### 10. The theme was fixed

**Symptom.** One look, regardless of preference or lighting.

**Cause.** VB6 forms used fixed colours and bitmaps.

**Why it mattered.** This was the least serious of the failures, but it was part of the same
fixed-control approach: the client could not adapt to the person using it. Long sessions in low
light made the single bright theme tiring.

**Rebuild.** `ThemeManager` provides light and dark themes, accent presets, a custom accent, and
a matching header/ink choice. Changes apply live to open chat and viewer windows, not only to the
studio.

## What the rebuild deliberately kept

- **The same university services.** The WebDemo SOAP endpoint and the recordings page are
  unchanged. The launcher is a better client, not a replacement back end.
- **The same session protocol.** Linktivity's join, roster, chat, voice, and viewport frames are
  reproduced field for field. The vendor's servers still see a normal student client.
- **The same voice codec.** TrueSpeech is still required because that is what the service sends.

## Inherited constraints

These are properties of the university's services and the vendor protocol. They are not features
the client chose to skip, and most cannot be fixed in the client alone.

- **Linktivity is not TLS.** The vendor calls its handshake "SSL", but it is a fixed preamble and
  an optional CAST cipher that this client never enables. Session traffic is effectively
  unencrypted. Fixing that requires a protocol change on the university side; the constraint is
  documented in [security-privacy.md](security-privacy.md) rather than hidden.
- **The recordings page is plain HTTP.** The page is public and unauthenticated, but a hostile
  network could observe or alter what is downloaded. The app does not send credentials there.
- **TrueSpeech is old and 32-bit.** It is low quality and requires the x86 helper. The service
  sends it, so the client must decode it.

## Deliberately deferred

These are scope decisions for the beta. They are not limitations of the student flow and they
are not claims that the old client never had the feature.

- **The tutor and host side.** The old client could host or tutor a session. The host controls,
  participant management, and tutor flow need their own rebuild; blocking those accounts is
  better than shipping a half-working host mode.
- **Student voice.** The microphone stays off for now while audio transfer and sharing are split
  and the university's backend behavior is understood. Tutor audio is unaffected.
- **Notifications.** Session starting, a new screen share, a finished download, and connection
  problems should reach the user even when the launcher is in the background.
- **Private messaging.** Room chat is included; one-to-one messages are not.
- **A modern codec such as Opus.** TrueSpeech is the inherited constraint; changing the codec
  needs the university's session service to carry it.
- **Smaller friction reducers.** Opening the student account page from inside the app is one
  example; it is not in this beta.

## Lessons carried into the codebase

1. **Measure the old client before replacing it.** The 21-second cold join was a stopwatch
   result, not a feeling. It set the target and kept the comparison honest.
2. **Do not wrap a runtime you cannot control.** The applet was the source of most startup,
   audio, and recovery problems. Speaking the protocol directly removed the layer.
3. **Keep the protocol, replace the runtime.** The vendor wire format is compatibility surface;
   the JVM and VB6 were implementation choices.
4. **Make failure visible.** A retry with a status line is better than a silent hang or a raw
   fault.
5. **Treat credentials as a platform feature.** Use DPAPI; never invent obfuscation.
6. **Design right-to-left, do not translate into it.** Shared tracks and one start edge are
   structural, not cosmetic.
7. **Do not install system components silently.** State the dependency and let the user decide.
8. **Delete what you replace.** Dead applet, SOAP, and mic-mute paths were removed rather than
   kept behind flags.
