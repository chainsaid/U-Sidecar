using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ParsecDisplay.Devices;

namespace ParsecDisplay.Streaming
{
    /// <summary>
    /// Captures a GDI display device (typically the Parsec virtual display
    /// created for this purpose) and continuously pushes it to whichever
    /// ISidecarDeviceDescriptor it's given (D92 is the only one today --
    /// see SidecarDeviceRegistry) as encoded frames.
    ///
    /// Ported from the Python prototype (scripts/mirror/mirror_vdd.py in the
    /// parent repo, D92-specific) and from MirrorWindow.cs's capture
    /// technique (StretchBlt from the desktop DC), but rendering off-screen
    /// instead of to a visible window, and pushing to an ISidecarDevice
    /// instead of painting a preview.
    ///
    /// Operating policy (see ISidecarDevice's doc comment and, for the D92
    /// specifically, WORK_SUMMARY.md §4/§7 in the parent repo): once
    /// started, this pushes frames on a fixed timer even when content
    /// hasn't changed (the device must never see its channel go idle), and
    /// on any write failure it stops and reports Disconnected rather than
    /// silently reopening the handle — reopening a live session is the #1
    /// known cause of an unrecoverable black screen on the D92, and there's
    /// no reason to assume other devices are more forgiving about it.
    /// </summary>
    public sealed class StreamWorker : IDisposable
    {
        public enum Status
        {
            Idle,
            Streaming,
            DeviceNotFound,     // not enumerated (unplugged, or held by another app)
            Recovering,         // a write failed; attempting a soft USB replug (see UsbRecovery)
            Disconnected,       // recovery gave up (or is disabled) — needs a physical replug
            CaptureSourceGone,  // the target GDI display disappeared (virtual display removed)
        }

        public class Options
        {
            /// <summary>JPEG quality 0-100 passed to the stock GDI+ encoder.
            /// Bumped from the original 75 after it looked too soft on the
            /// real panel -- 75 was carried over as a bandwidth-conscious
            /// default, not something verified to look good on this device.
            /// Larger frames still stream fine at IntervalMs below since the
            /// loop just runs each frame back-to-back with no idle gap when
            /// encode+push takes longer than the target interval (see Loop).</summary>
            public int JpegQuality = 95;

            /// <summary>Target frame interval. 350ms (~2.9fps) was the only
            /// value systematically verified safe in the parent repo's testing
            /// (WORK_SUMMARY.md §7.5 flags anything faster as untested); the
            /// official app reportedly runs 40-100ms (10-25fps) without issue.
            /// 33ms (~30fps) requested for smoother mirroring — if the known
            /// intermittent USB dropout (§4.1 there) starts happening
            /// noticeably more often at this rate, back off toward 50-67ms
            /// before assuming it's a new bug.</summary>
            public int IntervalMs = 33;

            /// <summary>Draw the cursor ourselves on top of the captured frame.
            /// Confirmed necessary on real hardware: with this off, the D92
            /// panel shows no cursor at all -- CAPTUREBLT (see
            /// CaptureScreenRegion) does NOT bake the cursor into the raw
            /// BitBlt capture on this machine, so DrawCursor is the only
            /// source of the on-panel cursor. (An earlier theory that
            /// disabling this would fix a flicker was wrong -- see the
            /// CAPTUREBLT removal below for the actual cause, which was a
            /// flicker of the user's own real cursor, unrelated to this.)</summary>
            public bool DrawCursor = true;

            /// <summary>On a write failure, attempt UsbRecovery.TryFullRecover()
            /// (Windows-level disable+enable of the device node — see that
            /// class's doc comment for why this is safe where blindly
            /// reopening the HID handle is not) instead of immediately giving
            /// up. Bounded by MaxRecoveryAttempts per Start() session.</summary>
            public bool EnableAutoRecovery = true;
            public int MaxRecoveryAttempts = 3;
            public int RecoveryPollTimeoutMs = 15000;
        }

        readonly Options _opts;
        Thread _thread;
        volatile bool _running;
        ISidecarDeviceDescriptor _descriptor;
        ISidecarDevice _device;
        int _lastModeCorrectionTick = int.MinValue;
        const int ModeCorrectionCooldownMs = 3000;

        public Status CurrentStatus { get; private set; } = Status.Idle;
        public event Action<Status, string> StatusChanged;

        public StreamWorker(Options opts = null)
        {
            _opts = opts ?? new Options();
        }

        /// <summary>Start streaming from the given GDI display device (e.g.
        /// "\\.\DISPLAY5") to whatever device the descriptor opens. Returns
        /// false immediately if that device can't be opened right now.</summary>
        public bool Start(ISidecarDeviceDescriptor descriptor, string gdiDeviceName)
        {
            if (_running)
                return true;

            _descriptor = descriptor;
            _device = descriptor.Open();
            if (_device == null)
            {
                SetStatus(Status.DeviceNotFound, $"{descriptor.Name} not found (unplugged, or held by another app)");
                return false;
            }

            // Reopening a device handle within the same physical USB
            // connection (suspend/resume, or the user toggling streaming
            // off/on) is the #1 confirmed cause of a black panel on the D92
            // (parent repo's WORK_SUMMARY.md §4.3) -- assume other devices
            // aren't necessarily more forgiving about it either. Calling
            // WakeUp() here, unconditionally on every Start() rather than
            // only after a detected failure, mirrors what the D92's own
            // official app does on every connect (§8.13 there) and is
            // confirmed on real hardware to revive a panel left black by
            // exactly this kind of reopen, with no PnP reset or physical
            // replug needed.
            try
            {
                _device.WakeUp();
            }
            catch (Exception ex)
            {
                Log.Warn("StreamWorker: wake sequence failed on open: {0}", ex.Message);
                _device.Dispose();
                _device = null;
                SetStatus(Status.DeviceNotFound, $"wake sequence failed: {ex.Message}");
                return false;
            }

            _running = true;
            _thread = new Thread(() => Loop(gdiDeviceName));
            _thread.IsBackground = true;
            _thread.Priority = ThreadPriority.AboveNormal;
            _thread.Start();
            return true;
        }

        public void Stop()
        {
            _running = false;
            _thread?.Join();
            _thread = null;

            _device?.Dispose();
            _device = null;

            if (CurrentStatus == Status.Streaming)
                SetStatus(Status.Idle, "stopped");
        }

        void SetStatus(Status s, string detail)
        {
            CurrentStatus = s;
            Log.Info("StreamWorker: {0} ({1})", s, detail);
            StatusChanged?.Invoke(s, detail);
        }

        void Loop(string gdiDeviceName)
        {
            SetStatus(Status.Streaming, "started");
            int recoveryAttempts = 0;

            while (_running)
            {
                var t0 = Environment.TickCount;

                try
                {
                    using (var frame = CaptureAndFit(gdiDeviceName))
                    {
                        if (frame == null)
                        {
                            // Source display vanished (e.g. virtual display removed
                            // from under us). Stop rather than spin on failures.
                            SetStatus(Status.CaptureSourceGone, gdiDeviceName);
                            _running = false;
                            break;
                        }

                        var jpeg = EncodeJpeg(frame, _opts.JpegQuality);
                        _device.SendFrame(jpeg);
                    }
                }
                catch (Exception ex)
                {
                    // Never just reopen the old handle here — a plain reopen
                    // with no real reset behind it just gives you a handle
                    // into a still-stuck device (see WORK_SUMMARY.md §4.3 in
                    // the parent repo, re: the D92). Recover() below performs
                    // an actual Windows-level device reset
                    // (UsbRecovery.TrySoftReplug) before ever calling the
                    // descriptor's Open() again.
                    _device?.Dispose();
                    _device = null;

                    bool recovered = false;
                    if (_opts.EnableAutoRecovery && recoveryAttempts < _opts.MaxRecoveryAttempts)
                    {
                        recoveryAttempts++;
                        SetStatus(Status.Recovering, $"write failed ({ex.Message}); soft-replug attempt {recoveryAttempts}/{_opts.MaxRecoveryAttempts}");
                        recovered = TryRecover();
                    }

                    if (!recovered)
                    {
                        SetStatus(Status.Disconnected, ex.Message);
                        _running = false;
                        break;
                    }

                    recoveryAttempts = 0; // reset the budget after a successful recovery
                    SetStatus(Status.Streaming, "recovered via soft replug");
                    continue;
                }

                int elapsed = Environment.TickCount - t0;
                int delay = _opts.IntervalMs - elapsed;
                if (delay > 0)
                    Thread.Sleep(delay);
            }

            _device?.Dispose();
            _device = null;
        }

        /// <summary>Reopen the device (via the same descriptor Start() was
        /// given) and replay its wake sequence. Confirmed on real D92
        /// hardware (parent repo's WORK_SUMMARY.md §8.13) to revive a panel
        /// left black by a plain reopen -- no PnP disable/enable or USB hub
        /// power-cycle needed, which earlier testing (§8.11, D92_NOTES.md
        /// "Update 2") found did NOT fix a dead panel on their own anyway.
        /// UsbRecovery.TryFullRecover() is kept only as a last-resort fallback
        /// for the case where the device isn't enumerating at all (actually
        /// unplugged, or wedged in a way a plain reopen can't reach), tried
        /// after the plain-reopen loop below gives up.</summary>
        bool TryRecover()
        {
            var deadline = Environment.TickCount + _opts.RecoveryPollTimeoutMs;
            while (_running && Environment.TickCount < deadline)
            {
                _device = _descriptor.Open();
                if (_device != null)
                {
                    try
                    {
                        _device.WakeUp();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("StreamWorker: wake sequence failed on reopen: {0}", ex.Message);
                        _device.Dispose();
                        _device = null;
                    }
                }
                Thread.Sleep(300);
            }

            Log.Warn("StreamWorker: plain reopen+wake gave up, falling back to UsbRecovery.TryFullRecover()");
            if (!UsbRecovery.TryFullRecover(_descriptor.VendorId, _descriptor.ProductId))
                return false;

            deadline = Environment.TickCount + _opts.RecoveryPollTimeoutMs;
            while (_running && Environment.TickCount < deadline)
            {
                _device = _descriptor.Open();
                if (_device != null)
                {
                    try
                    {
                        _device.WakeUp();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("StreamWorker: wake sequence failed after UsbRecovery: {0}", ex.Message);
                        _device.Dispose();
                        _device = null;
                    }
                }
                Thread.Sleep(300);
            }
            return false;
        }

        /// <summary>Grab the current frame of gdiDeviceName and letterbox-fit it
        /// into the device's pre-rotation capture canvas shape
        /// (_descriptor.CaptureWidth x CaptureHeight), then rotate. Returns
        /// null if the display's mode can't be read (display no longer
        /// exists).</summary>
        Bitmap CaptureAndFit(string gdiDeviceName)
        {
            if (!TryGetDisplayBounds(gdiDeviceName, out var bounds))
                return null;

            EnforceNativeMode(gdiDeviceName, bounds);

            int canvasW = _descriptor.CaptureWidth;
            int canvasH = _descriptor.CaptureHeight;

            using (var raw = CaptureScreenRegion(bounds))
            {
                var fitted = new Bitmap(canvasW, canvasH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(fitted))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    float scale = Math.Min((float)canvasW / raw.Width, (float)canvasH / raw.Height);
                    int w = Math.Max(1, (int)Math.Round(raw.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(raw.Height * scale));
                    int x = (canvasW - w) / 2;
                    int y = (canvasH - h) / 2;
                    g.DrawImage(raw, x, y, w, h);
                }

                int rotateDegrees = _descriptor.RotateDegrees;
                if (rotateDegrees % 360 == 0)
                    return fitted;

                RotateFlipType rft;
                switch (((rotateDegrees % 360) + 360) % 360)
                {
                    case 90: rft = RotateFlipType.Rotate90FlipNone; break;
                    case 180: rft = RotateFlipType.Rotate180FlipNone; break;
                    case 270: rft = RotateFlipType.Rotate270FlipNone; break;
                    default: rft = RotateFlipType.RotateNoneFlipNone; break;
                }
                fitted.RotateFlip(rft);
                return fitted;
            }
        }

        /// <summary>
        /// The Parsec VDD driver bakes in ~26 preset resolutions (each with
        /// several refresh rates) that always show up in Windows' Display
        /// Settings resolution dropdown for this virtual monitor -- that list
        /// is compiled into the driver itself (docs/PARSEC_VDD_SPECS.md,
        /// docs/PARSEC_VDD_RE.md §8) and there's no documented way to hide or
        /// remove them from a user-mode caller; Tray's EnsureCustomDisplayModes
        /// only adds one entry per known device alongside those ~26, it can't
        /// replace them. So the dropdown itself can't be trimmed down to a
        /// single choice.
        ///
        /// What we *can* enforce is that streaming never actually runs at
        /// anything other than the device's native capture shape: if someone
        /// picks a different resolution from that list while streaming is
        /// active, this snaps it back to CaptureWidth x CaptureHeight every
        /// time it drifts (throttled so it doesn't fight a user actively
        /// dragging through the dropdown). Checked every captured frame
        /// since TryGetDisplayBounds is already called for that anyway.
        /// </summary>
        void EnforceNativeMode(string gdiDeviceName, Rectangle bounds)
        {
            int canvasW = _descriptor.CaptureWidth;
            int canvasH = _descriptor.CaptureHeight;

            if (bounds.Width == canvasW && bounds.Height == canvasH)
                return;

            int now = Environment.TickCount;
            if (now - _lastModeCorrectionTick < ModeCorrectionCooldownMs)
                return;
            _lastModeCorrectionTick = now;

            Log.Warn("StreamWorker: virtual display drifted to {0}x{1}, forcing back to {2}x{3}",
                bounds.Width, bounds.Height, canvasW, canvasH);
            try
            {
                // Display's constructor is private -- can't build one directly,
                // have to look it up the same way Tray.cs does.
                var display = Display.GetAllDisplays()
                    .FirstOrDefault(d => string.Equals(d.DeviceName, gdiDeviceName, StringComparison.Ordinal));
                if (display == null)
                {
                    Log.Warn("StreamWorker: couldn't find Display for {0} to correct its mode", gdiDeviceName);
                    return;
                }
                display.ChangeMode(canvasW, canvasH, 60, Display.Orientation.Landscape);
            }
            catch (Exception ex)
            {
                Log.Warn("StreamWorker: failed to force resolution back: {0}", ex.Message);
            }
        }

        static bool TryGetDisplayBounds(string gdiDeviceName, out Rectangle bounds)
        {
            var devmode = default(Native.DEVMODE);
            devmode.dmSize = (short)Marshal.SizeOf<Native.DEVMODE>();

            if (!Native.EnumDisplaySettings(gdiDeviceName, -1, ref devmode))
            {
                bounds = default;
                return false;
            }

            bounds = new Rectangle(devmode.dmPositionX, devmode.dmPositionY, devmode.dmPelsWidth, devmode.dmPelsHeight);
            return bounds.Width > 0 && bounds.Height > 0;
        }

        Bitmap CaptureScreenRegion(Rectangle bounds)
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                var hdcDest = g.GetHdc();
                var hdcSrc = Native.GetDC(IntPtr.Zero);
                try
                {
                    // Plain SRCCOPY, no CAPTUREBLT. Confirmed on real hardware
                    // that CAPTUREBLT does NOT actually bake the cursor into
                    // this capture (DrawCursor below is the only source of
                    // the on-panel cursor) -- so it bought nothing here, but
                    // cost something real: BitBlt+CAPTUREBLT from the desktop
                    // DC at 30fps was making the user's own physical-monitor
                    // cursor visibly flicker, a known Windows quirk where
                    // CAPTUREBLT forces the OS to briefly fall back to
                    // software cursor compositing on every call. Dropping it
                    // removes that side effect on the real desktop.
                    Native.BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height,
                        hdcSrc, bounds.X, bounds.Y, Native.SRCCOPY);
                }
                finally
                {
                    Native.ReleaseDC(IntPtr.Zero, hdcSrc);
                    g.ReleaseHdc(hdcDest);
                }

                if (_opts.DrawCursor)
                    DrawCursor(g, bounds);
            }
            return bmp;
        }

        static void DrawCursor(Graphics g, Rectangle bounds)
        {
            var cursor = default(Native.CURSORINFO);
            cursor.cbSize = Marshal.SizeOf<Native.CURSORINFO>();
            if (!Native.GetCursorInfo(ref cursor) || cursor.flags != 0x1)
                return;
            if (!bounds.Contains(cursor.screenPosX, cursor.screenPosY))
                return;

            // GetCursorInfo's screenPosX/Y is the cursor's hotspot (the actual
            // pointer position), not the top-left of its icon bitmap. Most
            // cursors (the default arrow included) have their hotspot away
            // from the icon's center, so drawing at width/2,height/2 (as this
            // used to) offsets the rendered cursor from the real pointer by
            // roughly half the icon size. GetIconInfo gives the icon's real
            // xHotspot/yHotspot to draw from instead.
            int hotspotX = 0, hotspotY = 0;
            if (Native.GetIconInfo(cursor.hCursor, out var iconInfo))
            {
                hotspotX = iconInfo.xHotspot;
                hotspotY = iconInfo.yHotspot;
                // GetIconInfo hands back new bitmap handles the caller owns —
                // must delete them or every frame leaks two GDI objects.
                if (iconInfo.hbmMask != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmColor);
            }

            try
            {
                using (var icon = Icon.FromHandle(cursor.hCursor))
                {
                    g.DrawIcon(icon, cursor.screenPosX - bounds.X - hotspotX,
                                      cursor.screenPosY - bounds.Y - hotspotY);
                }
            }
            catch
            {
                // Some cursor handles (e.g. animated) aren't valid Icon handles;
                // skip drawing the cursor for that frame rather than crash the loop.
            }
        }

        static byte[] EncodeJpeg(Bitmap bmp, int quality)
        {
            var encoder = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);

            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, encoder, encParams);
                return ms.ToArray();
            }
        }

        static ImageCodecInfo GetJpegEncoder()
        {
            foreach (var codec in ImageCodecInfo.GetImageEncoders())
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                    return codec;
            throw new InvalidOperationException("No JPEG encoder registered on this system");
        }

        public void Dispose() => Stop();

        static class Native
        {
            public const uint SRCCOPY = 0x00CC0020;

            [StructLayout(LayoutKind.Sequential)]
            public struct DEVMODE
            {
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string dmDeviceName;
                public short dmSpecVersion;
                public short dmDriverVersion;
                public short dmSize;
                public short dmDriverExtra;
                public int dmFields;
                public int dmPositionX;
                public int dmPositionY;
                public int dmDisplayOrientation;
                public int dmDisplayFixedOutput;
                public short dmColor;
                public short dmDuplex;
                public short dmYResolution;
                public short dmTTOption;
                public short dmCollate;
                [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
                public string dmFormName;
                public short dmLogPixels;
                public int dmBitsPerPel;
                public int dmPelsWidth;
                public int dmPelsHeight;
                public int dmDisplayFlags;
                public int dmDisplayFrequency;
                public int dmICMMethod;
                public int dmICMIntent;
                public int dmMediaType;
                public int dmDitherType;
                public int dmReserved1;
                public int dmReserved2;
                public int dmPanningWidth;
                public int dmPanningHeight;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct CURSORINFO
            {
                public int cbSize;
                public uint flags;
                public IntPtr hCursor;
                public int screenPosX;
                public int screenPosY;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct ICONINFO
            {
                public bool fIcon;
                public int xHotspot;
                public int yHotspot;
                public IntPtr hbmMask;
                public IntPtr hbmColor;
            }

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

            [DllImport("user32.dll")]
            public static extern IntPtr GetDC(IntPtr hwnd);

            [DllImport("user32.dll")]
            public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

            [DllImport("gdi32.dll")]
            public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetCursorInfo(ref CURSORINFO pci);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

            [DllImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteObject(IntPtr hObject);
        }
    }
}
