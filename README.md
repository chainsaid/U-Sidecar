# U-Sidecar

A Windows tray app that mirrors your desktop onto small USB "sidecar"
screens — secondary panels like the MiraBox StreamDock D92 that plug in
over USB and show a slice of your desktop. U-Sidecar drives one of these
panels directly: it creates a virtual display sized to the panel, captures
it, and streams it over the panel's own wire protocol — no vendor software
required.

D92 is the only panel supported today, but the device layer is written so
adding another model doesn't touch the streaming/UI code — see
[Architecture](#architecture) below.

## Features

- **Auto-detect and stream** — plug the panel in, U-Sidecar finds it and
  can start mirroring automatically (Options → "Auto-start when plugged in").
- **Survives sleep/resume and reconnects** — replays the panel's own
  wake sequence on every connect, which fixes the "panel goes black after
  a while" failure mode this app had early on (see
  [D92_NOTES.md](docs/D92_NOTES.md) for the debugging history).
- **Locked to the panel's native resolution** — even though the
  underlying virtual-display driver always offers a long list of desktop
  resolutions, U-Sidecar snaps back to the panel's actual shape if that
  drifts.
- **CLI mode** for scripting virtual displays directly (inherited from the
  driver this is built on — see [docs/VDD_CLI_USAGE.md](docs/VDD_CLI_USAGE.md)).

## Requirements

- Windows 10 1607+ (see [driver version table](docs/PARSEC_VDD_SPECS.md)
  for exact IddCx requirements).
- Administrator rights. U-Sidecar needs these to register the panel's
  resolution with the virtual-display driver
  (`HKLM\SOFTWARE\Parsec\vdd`) — it will prompt for elevation on launch.
- The [Parsec Virtual Display Driver](#credits) installed (bundled with
  the setup installer; see below if installing manually).

## Install

Download the latest setup installer from
[Releases](https://github.com/chainsaid/U-Sidecar/releases) and run it.
It installs U-Sidecar, registers it to launch on startup (optional), and
does not touch the VDD driver install if one is already present.

### Build from source

```
git clone https://github.com/chainsaid/U-Sidecar.git
cd U-Sidecar
dotnet build app/USidecar.csproj -c Release
```

Output lands in `app/bin/Release/`. Requires the .NET Framework 4.7.2
targeting pack (or pass `-p:TargetFramework=net480` for 4.8). Running the
build itself doesn't need admin rights — only running the resulting exe
does.

### Local installer package

`setup.iss` (repo root) is an [Inno Setup](https://jrsoftware.org/isinfo.php)
script. To build an installer locally without going through CI/signing:

```
dotnet build app/USidecar.csproj -c Release
mkdir bin
copy app\bin\Release\USidecar.exe bin\
copy app\bin\Release\USidecar.exe.config bin\
copy app\bin\Release\vdd.cmd bin\
iscc setup.iss
```

The signed release builds (from [Releases](https://github.com/chainsaid/U-Sidecar/releases))
go through the same script via `.github/workflows/publish.yml`, code-signed
by [SignPath.io](#credits).

## Architecture

```
app/
  Devices/              — sidecar-screen device abstraction
    ISidecarDevice.cs      — one open session (WakeUp, SendFrame)
    SidecarDeviceRegistry.cs — every known device model
    D92/                  — the D92 implementation (only one today)
  Streaming/
    StreamWorker.cs       — capture → letterbox → rotate → encode → push loop,
                            generic over whatever ISidecarDeviceDescriptor
                            it's given
  Tray.cs                — tray menu, options, virtual-display setup
  Vdd/                   — Parsec VDD driver client (unrelated to the
                            panel protocol — this manages the *virtual
                            display* U-Sidecar captures from)
```

Adding a new panel model means writing one `ISidecarDeviceDescriptor` +
one `ISidecarDevice` implementation under `Devices/<model>/` and
registering it in `SidecarDeviceRegistry.Known` — `StreamWorker` and
`Tray.cs` don't need to know it exists.

The D92's own wire protocol (what bytes actually go over USB) was
reverse-engineered in the parent repository, not here — see that repo's
`WORK_SUMMARY.md` for the full evidence trail (packet captures, firmware
disassembly, protocol tables) if you're porting a new device or debugging
the D92 implementation.

## Development notes

[D92_NOTES.md](docs/D92_NOTES.md) is a running log of implementation decisions,
bugs found and fixed, and dead ends — most usefully the black-screen
debugging history, since that failure mode is the one thing worth
understanding before changing anything in `Streaming/StreamWorker.cs` or
`Devices/*/*.cs`.

Other reference docs (mostly about the underlying VDD driver itself,
inherited from the project this was built on — not U-Sidecar-specific):

- [docs/PARSEC_VDD_SPECS.md](docs/PARSEC_VDD_SPECS.md) — driver preset
  resolutions, adapter/monitor identity, EDID.
- [docs/PARSEC_VDD_RE.md](docs/PARSEC_VDD_RE.md) — reverse-engineered
  IOCTL protocol for anyone reimplementing the driver client.
- [docs/VDD_CLI_USAGE.md](docs/VDD_CLI_USAGE.md) — the `vdd` CLI mode.
- [docs/VDD_LIBRARY_USAGE.md](docs/VDD_LIBRARY_USAGE.md) — the C/C++
  driver API (`core/parsec-vdd.h`), for embedding VDD control in another
  app entirely.

## Known limitations

Inherited from the VDD driver (see
[docs/PARSEC_VDD_SPECS.md](docs/PARSEC_VDD_SPECS.md)):

- No HDR support.
- Custom resolution presets are capped at 5 entries in
  `HKLM\SOFTWARE\Parsec\vdd`, shared system-wide with any other software
  using the same driver (including the real Parsec app, if installed) —
  U-Sidecar merges into this table rather than overwriting it.
- Requires an interactive user session; won't run before login on a
  headless/auto-login-disabled host.

## Credits

- Virtual display support is powered by the
  [Parsec Virtual Display Driver](https://github.com/nomi-san/parsec-vdd),
  the project this app was originally forked from.
- Code signing for release builds provided by
  [SignPath.io](https://signpath.io), certificate by
  [SignPath Foundation](https://signpath.org).

## License

[MIT](LICENSE)
