# D92 streaming — implementation notes

Running log of implementation decisions, bugs found and fixed, and dead
ends for the D92 sidecar-screen streaming feature (now part of U-Sidecar —
see the top-level [README.md](README.md) for the current app-wide picture;
this file stays D92-specific). Entries are in roughly chronological order,
oldest first — later entries correct or supersede earlier ones rather than
editing them in place, so read to the bottom before trusting anything here,
and check current file paths against the actual tree (`app/Devices/D92/`,
`app/Streaming/`) since some described below predate the multi-device
refactor.

## What's here (as originally built, see later updates for what moved)

- `app/D92/D92Device.cs` (now `app/Devices/D92/D92Device.cs`) — C# port of the StreamDock D92 HID protocol
  (`streamdock.py` in the parent RE repo is the reference). Finds the device
  by VID 0x5548/PID 0x1011 via SetupAPI, opens it with `CreateFileW` +
  `WriteFile`/`OVERLAPPED` (not `HidD_SetOutputReport`, confirmed not to work
  on this device), builds the 32-byte `CRT\0\0DRA` header + 1024-byte chunked
  JPEG push, and the binary `LIG` (brightness) / `DIS` (wake) control
  commands. Rotation (`SET`) is intentionally **not** implemented — per the
  parent repo's findings it has no observed effect on the device; always
  rotate host-side before encoding, same as the Python driver does.
- `app/D92/StreamWorker.cs` (now `app/Streaming/StreamWorker.cs`) — background capture→fit→rotate→encode→push loop.
  Captures a chosen GDI display via `BitBlt` (same technique as the existing
  `MirrorWindow.cs`, but off-screen instead of to a preview window),
  letterbox-fits it into the panel's pre-rotation canvas shape (1920×462),
  rotates (`--rotate 270` was the value confirmed correct on real hardware in
  the Python prototype's test session tonight — see the "Confirmed" section
  below), JPEG-encodes, and pushes via `D92Device`.
- Wired into `Tray.cs`: a new **"D92 Streaming"** menu item toggles it. On
  start, it reuses an already-active Parsec virtual display if one exists,
  otherwise creates one (`Vdd.Controller.AddDisplay()`) and polls up to ~3s
  for Windows to enumerate it, sets it to 1920×1080@60 (best-effort), then
  starts `StreamWorker` against its GDI device name.
- `app/USidecar.csproj` — fixed to build under the .NET 8 SDK
  (`GenerateResourceUsePreserializedResources` + `System.Resources.Extensions`,
  see the earlier commit).

## Operating policy carried over from the Python driver (do not relax these)

Documented at length in the parent repo's `WORK_SUMMARY.md` §4/§7 — the short
version, because violating them bricks the panel until a physical replug:

1. **Never reopen the device handle mid-session.** `StreamWorker` opens once
   in `Start()` and never retries opening on a write failure — it stops and
   reports `Status.Disconnected` instead. The tray shows a balloon warning
   telling the user to physically replug before clicking Start again. **Do
   not "fix" this by adding auto-retry/reopen logic** — that's the #1 known
   cause of an unrecoverable black screen.
2. **Never let the stream go idle.** The loop pushes a frame every
   `IntervalMs` (default 350ms, matching the Python driver's verified-safe
   value) regardless of whether the captured content changed.

## What's confirmed vs. not

**Confirmed on real hardware** (via the Python prototype earlier tonight,
`scripts/mirror/mirror_vdd.py` in the parent repo — same protocol, same
constants, this C# port mirrors that logic):
- The overall pipeline (virtual display → capture → letterbox → rotate 270°
  → JPEG → DRA push) displays correctly-oriented, non-distorted content on
  the real panel.
- A pre-existing intermittent USB dropout (unrelated to this feature, see
  parent repo §4.1) can end a session anywhere from ~7s to ~4min in. This C#
  port inherits the same "stop and report, don't auto-reopen" policy rather
  than trying to paper over it.

**NOT yet verified — needs your eyes, I can't check this myself:**
- **This C# port itself has never been run end-to-end against the real
  device.** It's a faithful line-by-line port of the already-verified Python
  logic and it builds clean, but I have no way to visually confirm the panel
  actually lights up correctly from this code — please test it before
  trusting it.
- Whether `Display.ChangeMode(1920, 1080, 60, Landscape)` succeeds against a
  freshly-added Parsec display on the first try (the retry-poll loop assumes
  the display becomes `Active` with a valid `DeviceName` within ~3s; if your
  machine is slower than that, `TryEnsureVirtualDisplay` will show an error
  telling you to click Start again — should just work, but untested timing).
- The `MessageBox`/error-handling paths (driver not installed, exceeded
  display limit, etc.) — copied the existing app's conventions but never
  actually triggered any of them.

## Known gaps / deliberately deferred (not done tonight)

- **No settings UI** for rotate angle / JPEG quality / interval / fit mode —
  they're hardcoded defaults in `StreamWorker.Options`. Wire up a settings
  panel if you want these tweakable without recompiling.
- **No i18n** — the new menu item text ("Start/Stop D92 Streaming") is a
  plain hardcoded string, not a `t_*` translation key, so it always shows in
  English regardless of the app's language setting. Deliberate: didn't want
  to touch all three `Languages/*.xaml` files blindly without your review.
  Add `t_d92_*` keys there when you're ready.
- **The generic "add up to 8 virtual displays" UI is untouched** — this adds
  D92 streaming as one more menu item rather than replacing the app with a
  D92-only tool (per the plan, decided to keep this additive/low-risk rather
  than rip out working functionality I can't test).
- Didn't attempt the "crop to fill instead of letterbox" alternative fit mode
  you might want later — only letterbox (black bars) is implemented.

## How to test

1. `dotnet build parsec-vdd.sln` (already verified clean, 0 warnings/errors).
2. Run `app/bin/Debug/USidecar.exe` (or `bin/Release/...` for a release build), make sure the D92 is plugged in and the
   official MiraBox software is closed.
3. Tray icon → **D92 Streaming**. Should show your desktop's virtual-display
   mirror on the panel, correctly oriented, no stretching.
4. If it looks wrong: rotation is `StreamWorker.Options.RotateDegrees` in
   `Tray.cs`'s call site (currently defaults inside `StreamWorker`, not yet
   exposed) — flip between 90/270 the same way the Python prototype needed.
5. Check `app/bin/debug.log` if anything misbehaves — every status
   transition and error is logged there.

Nothing was pushed to the remote — both this repo and the parent repo have
local commits only, ready for you to review and push.

## Update (same night, after user testing)

Confirmed working end to end on real hardware. Fixed along the way:

- **Resolution was wrong / not visible in Windows' display settings**: the
  VDD driver only offers a small fixed set of desktop-shaped presets by
  default; `ChangeMode(1920, 462)` silently failed because that mode simply
  wasn't in the display's enumerated mode list. Fixed by writing it into the
  driver's custom-mode registry preset (`Vdd.Utils.SetCustomDisplayModes`,
  `HKLM\SOFTWARE\Parsec\vdd`) at startup (`Tray.EnsureD92CustomMode`), which
  requires admin — **`app.manifest` now requests `requireAdministrator`, so
  the app UACs on every launch.** `TryEnsureVirtualDisplay` also now always
  removes+recreates an existing Parsec display rather than reusing it, since
  a display created before the preset was written won't pick up the new mode
  without being recreated.
- **Removed the generic multi-display management UI entirely** per request:
  tray menu no longer has Add/Remove display or Restore/Fallback options;
  `Config.RestoreDisplays`/`SavedDisplays`/`FallbackDisplay` are gone.
  `MainWindow` (the old "add displays" dashboard) is never shown anymore —
  `App.xaml.cs` constructs it (still needed for its Win32 handle, used as the
  MessageBox owner) but skips `.Show()`. Its XAML/code-behind and
  `Components/DisplayItem.xaml.cs` are untouched/dead code, kept only so
  `Tray.AddDisplay`/`RemoveDisplay` (thin pass-throughs, unreachable from any
  normal flow now) satisfy those files' references and the app keeps
  building without touching XAML.
- **Frame interval dropped from 350ms to 33ms** (~30fps) on request — see the
  doc comment on `StreamWorker.Options.IntervalMs` for the risk note (this is
  well past the 350ms value that was actually verified safe in the parent
  repo's testing; back off toward 50-67ms if dropouts get noticeably worse).

**Known issue, confirmed still present**: the intermittent USB dropout from
the parent repo's WORK_SUMMARY.md §4.1 still happens with this pipeline —
user confirmed "会掉线" (does drop) after testing. Per the no-auto-reopen
policy, `StreamWorker` stops and reports `Status.Disconnected` rather than
retrying; a physical replug is needed before starting again. Not investigated
further tonight — root-causing that dropout (independent of this C# port) is
still open work.

## Update 2 (following morning): recovery mechanisms tried, all failed so far

After more testing, the black-screen-after-a-while failure turned out to
usually be the "writes succeed, panel just goes dark" variant (parent repo's
WORK_SUMMARY.md §4.3), not a USB write exception — so `StreamWorker`'s
auto-recovery path (which only fires on a write exception) never even
engages for it. That's a real gap, not yet closed.

**What was tried, in order, all confirmed NOT to fix a dead panel:**

1. **`UsbRecovery.TrySoftReplug()`** — `SetupDiCallClassInstaller` /
   `DIF_PROPERTYCHANGE` (Device Manager's "Disable device" then "Enable
   device"). Confirmed via Device Manager visibly refreshing that it does
   run, but the panel stayed black. This only restarts the driver stack — it
   never cuts power to the USB port, so it can't fix anything that needs an
   actual power-on reset.
2. **`UsbRecovery.TryPowerCyclePort()`** — `IOCTL_USB_HUB_CYCLE_PORT` on the
   parent hub, meant to actually cut/restore VBUS on that port (real
   unplug/replug equivalent). First implementation had a devnode-walking bug
   (read the port address off the wrong ancestor, resolved "the hub" as the
   D92's own composite USB node — see debug.log: `hub USB\VID_5548&PID_1011\...`)
   so the IOCTL silently never fired; fixed to walk up however many hops it
   takes to reach a real `GUID_DEVINTERFACE_USB_HUB` ancestor. **Not yet
   re-tested after that fix** — still needs a real black-screen repro to
   confirm the IOCTL actually fires and, more importantly, whether it fixes
   anything even when it does.
3. **Manual "just reopen and restream" (no reset at all)** — user tested
   this directly: let it go black, skipped the recovery button, clicked
   "D92 Streaming" to Stop then Start fresh. **Did not recover either.**
   This is the most important negative result of the night: it rules out
   the theory that the official app's fix is "nothing special, just
   reopen+stream" — since our app does exactly that and it didn't work.

**Also important**: a second recovery-capture packet trace (this time
launching the official MiraBox software myself, not the user) showed the
D92 device was **already at PnP status "OK" in Device Manager the whole
time**, before MiraBox was even opened — i.e. the device was never actually
gone from Windows' point of view during this particular black-screen
episode. The "burst of GET_DESCRIPTOR requests" pattern from the first
recovery capture (originally read as "the recovery mechanism") reappeared
here too, at a different, shorter delay (~8s vs ~35s) — this now looks more
like routine descriptor housekeeping that happens whenever any app freshly
opens the HID device, not a deliberate reset trigger. **The original
"GET_DESCRIPTOR burst = the fix" theory from Update 1 is likely wrong** and
should not be treated as confirmed; it was never that panel's USB identity
that needed resetting.

**Where this leaves things**: something the official MiraBox software does
still reliably revives a dead panel, and it is not yet identified. Ruled out
so far: PnP driver restart, USB hub port power cycle (pending re-test after
the bug fix), and "just reopen the HID handle and resume streaming" with no
special sequence. Candidates not yet examined: the exact byte-for-byte
content/pacing of the official app's first several frames after connecting
(captured in both recovery-capture pcaps, not yet diff'd byte-for-byte
against our own driver's first frames), and whether MiraBox sends some
control command (handshake/APPNEW, or something else) we're not sending.

Also fixed a papercut along the way: `TryEnsureVirtualDisplay` no longer
tears down and recreates the Parsec virtual display on every single Start()
— it only does that when the existing one isn't already at the D92 shape
(e.g. it predates `EnsureD92CustomMode` ever running). Reuses it as-is
otherwise.

## Update 3: root cause found + fix wired in (this app was the culprit)

The "candidates not yet examined" note above got closed out in the parent
repo (WORK_SUMMARY.md §8.12/§8.13): byte-diffing the two recovery pcaps
against a plain startup capture showed the official app's connect sequence
is always identical — `DIS` (wake) → ~450ms → `LIG` (brightness=50) → then
continuous DRA streaming, no APPNEW/CONNECT, no re-enumeration. Confirmed
on real hardware: opening a fresh HID handle on an already-black panel and
sending exactly that sequence before streaming revived it, no PnP reset or
physical replug needed.

Also confirmed: **this app was itself the thing reliably triggering the
black screen**, independent of any USB flakiness. `StreamWorker.Stop()`
closes the HID handle; `Start()` opens a new one. Per the parent repo's
CLAUDE.md, reopening the handle within the same physical USB connection —
even with the device never actually unplugged — is the #1 confirmed cause
of a black panel. This app does exactly that Stop()+Start() cycle on every
suspend/resume (`Tray.OnPowerModeChanged`/`OnResume`) and every manual
toggle of the "D92 Streaming" tray item (`ToggleD92Streaming`). So normal
use of this app (closing the laptop lid, or clicking the tray toggle) was
enough to hit the known black-screen trigger on its own.

Fix: `D92Device.WakeAndSetBrightness()` (DIS → 450ms → LIG) is now called
after every successful `D92Device.Open()` in `StreamWorker` — both the
initial `Start()` and the reopen inside `TryRecover()` — before streaming
resumes. `TryRecover()` was also simplified: it now tries a plain
reopen+wake first (matches what's confirmed to work) and only falls back to
`UsbRecovery.TryFullRecover()`'s PnP disable/enable dance if the device
isn't enumerating at all — that heavier path was already confirmed (Update
2 above) to *not* fix a dead-but-still-enumerated panel on its own, so it's
no longer the primary recovery strategy.

**Not yet re-tested against this app specifically** (only verified via the
Python reference driver in the parent repo) — next step is to actually
trigger a black screen through this app (e.g. put the laptop to sleep and
wake it while streaming) and confirm the panel comes back on its own.

## Update: refactored for multiple sidecar-screen devices, D92 as one of them

The app was rewritten from "hardcodes the D92" to "drives whatever
sidecar-screen device is plugged in, via a small device abstraction" —
requested ahead of any second device actually existing, so the goal was
just to get the seams in the right place without inventing speculative
machinery for devices that don't exist yet.

New in `app/Devices/`:
- `ISidecarDevice` — one open session against a device: `WakeUp()` +
  `SendFrame(bytes)`.
- `ISidecarDeviceDescriptor` — describes a device *model*: `Name`,
  `VendorId`/`ProductId`, `CaptureWidth`/`CaptureHeight` (pre-rotation
  capture canvas), `RotateDegrees`, `IsPresent()` (SetupDi-only, safe
  anytime), `Open()`.
- `SidecarDeviceRegistry.Known` — the array of registered descriptors.
  D92 is the only entry today (`Devices/D92/D92DeviceDescriptor.cs`); add
  a new device by writing one descriptor + one `ISidecarDevice`
  implementation and adding it here, nothing else needs to know it exists.
- `UsbRecovery` moved out of `Devices/D92/` (it was already generic enough
  to not need to live there) and its VID/PID are now parameters instead of
  reading `D92Device.VendorId`/`ProductId` directly.

`D92Device` (`Devices/D92/D92Device.cs`) is otherwise unchanged --
still the same wire protocol, same operating constraints -- it just also
implements `ISidecarDevice` now (`WakeUp()`/`SendFrame()` forward to the
existing `WakeAndSetBrightness()`/`SendJpeg()`).

`StreamWorker` (moved to `app/Streaming/StreamWorker.cs`) no longer knows
`D92Device` exists: `Start()` takes an `ISidecarDeviceDescriptor` and asks
it to `Open()`; the capture/letterbox/rotate math reads shape and rotation
off that descriptor instead of the `CanvasH`/`CanvasW`/`RotateDegrees`
constants it used to hardcode.

`Tray.cs`: `EnsureD92CustomMode()` (writes the VDD custom-mode registry
preset) became `EnsureCustomDisplayModes()` and now loops
`SidecarDeviceRegistry.Known`, registering every known device's shape
regardless of which one is actually plugged in — so the mode is already
available the instant any of them shows up. Everywhere else that needs "the
device that's here right now" calls `SidecarDeviceRegistry.FindPresent()`.

Deliberately left alone: the tray menu's "D92 Streaming" toggle text and
its status/notification strings. Those name the *feature* (streaming to
whichever D92-shaped panel is plugged in), not the app's own brand or an
implementation detail — accurate regardless of how many device models this
app ends up supporting, so there was no reason to touch them.

Compiled clean, user confirmed real D92 hardware still streams normally
after the refactor.
