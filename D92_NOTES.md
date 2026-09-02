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
