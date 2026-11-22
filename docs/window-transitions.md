# Window transitions

Design for maximize, restore, minimize, and restore-from-taskbar on the studio,
chat, viewer, and sign-in windows, plus the chat's open and close. Target
release: `3.0.0-beta.1`.

## Goal

All four state changes use one transition: freeze the current pixels, apply the
new window state in a single step underneath, then dissolve the frozen frame
out. The window never animates its geometry, so the page and the WebView2 child
resize exactly once per command and the visible motion is pure opacity.

## Why a dissolve

A live geometry animation resizes the WebView2 child on every frame. Chromium's
re-layout and re-raster cost caused 30-55ms hitches, which is exactly the
stutter the transition was supposed to hide. Opacity is different: the snapshot
is presented 1:1 by a layered window, and each frame only changes a constant
blend value. Nothing reflows, nothing scales, and the content stays sharp.

The trade-off is deliberate: the new layout appears immediately under the old
one and the old view dissolves away. There is no zoomed screenshot, because the
snapshot is never stretched.

## Components

| Component | Responsibility |
| --- | --- |
| `WindowAnimator` | State machine, snapshot lifecycle, instant state changes, open/close dissolves, lifetime DWM transition suppression |
| `LayeredOverlay` | Topmost, no-activate, click-through ULW window that presents the snapshot at 1:1 and runs its own fade thread paced by `DwmFlush` |
| `WindowSnapshot` | `BitBlt` screen capture with a `PrintWindow(PW_RENDERFULLCONTENT)` fallback |

`LayeredOverlay` presents the snapshot's device context directly; the pixels are
never copied into a second bitmap.

## Data flow

Every command follows the same sequence:

1. Capture the current screen area into a snapshot. The copy starts from the
   exact pixels the user sees, even when another window covers part of it.
2. Create the overlay over that area and show it before the state change, so no
   uncovered frame can appear. Maximize captures the union of the current window
   and the monitor work area, so the maximized layout cannot pop in around the
   old rect. `DwmFlush` runs after the overlay is shown, so the frozen frame is
   composed before the state changes.
3. Apply the new `WindowState` in one step. The native state is authoritative,
   so the taskbar, Aero Snap, Win+Up/Down, and the system menu agree with what is
   on screen immediately.
4. Hold the overlay fully opaque for 60ms while the window paints its new
   layout. Without this hold, a partially faded overlay can reveal the stale
   surface at the new size before WPF and WebView2 finish reflowing.
5. Fade the overlay from alpha 255 to 0 over 120ms, linearly. The fade runs on a
   dedicated thread paced by `DwmFlush`, so UI-thread layout work and WebView2
   re-rastering cannot stall it. A UI-thread timer would inherit those stalls,
   and WPF's render loop throttles while the window is minimized.
6. Dispose the overlay.

DWM transitions are disabled once, when the window attaches, and stay disabled
for its lifetime. That keeps Windows from scaling the old frame up before the
frozen frame is composed, and removes the need to toggle the attribute around
each state change.

Restore-from-minimize is the same rule with the opposite frame: the window is
still minimized, so the capture is the desktop (or whatever covers the restore
bounds) and the restored window dissolves in over it. No snapshot is kept
between transitions; every fade owns and disposes its own frame.

## Opening and closing

The chat window is prewarmed hidden. Opening captures the destination rect,
shows the window at full opacity under the frozen desktop frame, holds for 60ms
while the surface is composed, then runs the same 120ms dissolve. The capture
happens before the prewarmed window is moved into place, so the frozen frame is
always the desktop, never the chat itself.

Closing captures the chat surface, hides the window, dissolves the frozen frame
to the desktop, and closes the window when the fade finishes. Hiding first keeps
`Closed` from firing at the start of the fade, so the chat and viewer close
sequence stays coordinated. The viewer uses the same 120ms dissolve; because it
is pure WPF, a covered viewer is printed with `PrintWindow` so its own surface
fades instead of the chat's. A minimized, hidden, or covered chat closes
directly, with no dissolve, because a screen copy would freeze whatever covers
it.

## Reversal

The state change is instant, so a second command always acts on the real
current state:

- Maximize during a maximize fade restores: the active fade is finished and a
  restore fade starts from the maximized pixels.
- Restore during a restore fade does nothing; the window is already normal and
  the fade is allowed to finish.
- Minimize during any fade captures the current frame and starts a minimize
  fade. Restore during a minimize fade finishes it and starts a restore fade
  from the live desktop.
- A native move or resize (`WM_ENTERSIZEMOVE`, a grip drag) finishes the active
  fade immediately and proceeds from the live window. A minimized window cannot
  be resized, so that request is refused.

No command waits for the current transition to end.

## Input

The overlay is topmost and click-through (`WS_EX_TRANSPARENT`, no activation),
so every click during a fade reaches the real window, the taskbar, or whatever
is underneath. There is no hit forwarding and no synthetic input.

## Constraints

- The page stays full-bleed; no WebView2 inset.
- The page is not told that a transition is running. There are no per-frame
  resizes, so no animation flags or deferred renders are needed.
- The sign-in window uses the animator for minimize and restore, and can never
  maximize or resize.
- The resize border is removed while maximized so a double-click at `y=0`
  restores, and restored when normal.
- Restore-to-maximized from the taskbar is preserved with the
  `WPF_RESTORETOMAXIMIZED` placement flag.
- The release stays framework-dependent; the transition uses only user32/gdi32
  and WPF.

## Edge cases

| Case | Handling |
| --- | --- |
| Maximize while maximized | No-op |
| Restore while normal | No-op |
| Minimize while minimized | No-op |
| Rapid command spam | Last command wins; the active fade is finished first |
| Close during a fade | `Closed` finishes the motion and disposes the overlay |
| Snapshot or overlay creation fails | Apply the state directly with no fade |
| Window occluded at capture | The screen copy already matches what the user sees |
| New layout slower than the hold | The dissolve starts from the frozen frame; no stale surface is exposed before it |
| Restore from minimized | The screen at the restore bounds is captured, then the window dissolves in |
| Multi-monitor / per-monitor DPI | Rects are device pixels from `GetWindowRect` / `GetWindowPlacement` |
| Window partly off-screen | Windows clips the overlay; the state change still completes |
| DWM transitions unavailable | The attribute call is ignored; the fade still runs |

## Acceptance criteria

- Maximize, restore, minimize, restore-from-taskbar, chat open, chat close, and
  viewer close each show one dissolve, with no geometry animation, no scaling,
  no stale frame at the new size, and no end-of-motion layout snap.
- The fade remains smooth while the page reflows underneath.
- A second command during a fade reverses immediately.
- Every resize edge and corner works from normal, maximized, and after a fade.
- Double-click at `y=0` while maximized restores; double-click on the page does
  not.
- Sign-in never maximizes; its minimize and restore use the same dissolve.
- Release build: 0 warnings, 0 errors.
