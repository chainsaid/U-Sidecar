# D92 streaming — overnight implementation notes

Built while you were asleep, following the plan discussed earlier. Read this
before testing.

## What's here

- `app/D92/D92Device.cs` — C# port of the StreamDock D92 HID protocol
  (`streamdock.py` in the parent RE repo is the reference). Finds the device
  by VID 0x5548/PID 0x1011 via SetupAPI, opens it with `CreateFileW` +
  `WriteFile`/`OVERLAPPED` (not `HidD_SetOutputReport`, confirmed not to work
  on this device), builds the 32-byte `CRT\0\0DRA` header + 1024-byte chunked
  JPEG push, and the binary `LIG` (brightness) / `DIS` (wake) control
  commands. Rotation (`SET`) is intentionally **not** implemented — per the
  parent repo's findings it has no observed effect on the device; always
  rotate host-side before encoding, same as the Python driver does.
- `app/D92/StreamWorker.cs` — background capture→fit→rotate→encode→push loop.
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
- `app/ParsecDisplay.csproj` — fixed to build under the .NET 8 SDK
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
2. Run `app/bin/ParsecDisplay.exe`, make sure the D92 is plugged in and the
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
