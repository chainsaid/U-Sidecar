using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ParsecDisplay.D92
{
    /// <summary>
    /// Captures a GDI display device (typically the Parsec virtual display
    /// created for this purpose) and continuously pushes it to the D92 panel
    /// as JPEG-over-HID frames.
    ///
    /// Ported from the Python prototype (scripts/mirror/mirror_vdd.py in the
    /// parent repo) and from MirrorWindow.cs's capture technique (StretchBlt
    /// from the desktop DC), but rendering off-screen instead of to a visible
    /// window, and pushing to D92Device instead of painting a preview.
    ///
    /// Operating policy (see D92Device's doc comment and WORK_SUMMARY.md
    /// §4/§7 in the parent repo): once started, this pushes frames on a fixed
    /// timer even when content hasn't changed (the device must never see the
    /// OUT endpoint go idle), and on any write failure it stops and reports
    /// Disconnected rather than silently reopening the handle — reopening a
    /// live session is the #1 known cause of an unrecoverable black screen.
    /// </summary>
    public sealed class StreamWorker : IDisposable
    {
        public enum Status
        {
            Idle,
            Streaming,
            DeviceNotFound,     // D92 not enumerated (unplugged, or held by another app)
            Recovering,         // a write failed; attempting a soft USB replug (see UsbRecovery)
            Disconnected,       // recovery gave up (or is disabled) — needs a physical replug
            CaptureSourceGone,  // the target GDI display disappeared (virtual display removed)
        }

        public class Options
        {
            /// <summary>Host-side rotation before encoding. 270 confirmed correct
            /// on real hardware for a 1920x1080-shaped capture; re-check if the
            /// virtual display's native orientation changes.</summary>
            public int RotateDegrees = 270;
            public int JpegQuality = 75;

            /// <summary>Target frame interval. 350ms (~2.9fps) was the only
            /// value systematically verified safe in the parent repo's testing
            /// (WORK_SUMMARY.md §7.5 flags anything faster as untested); the
            /// official app reportedly runs 40-100ms (10-25fps) without issue.
            /// 33ms (~30fps) requested for smoother mirroring — if the known
            /// intermittent USB dropout (§4.1 there) starts happening
            /// noticeably more often at this rate, back off toward 50-67ms
            /// before assuming it's a new bug.</summary>
            public int IntervalMs = 33;
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

        // Panel canvas, pre-rotation "landscape" shape — see WORK_SUMMARY.md §8.7:
        // the official app authors content on a 1920x462 canvas and rotates before
        // push, matching the panel's apparent native orientation. Public so the
        // caller can lock a virtual display to this exact resolution (see
        // Tray.TryEnsureVirtualDisplay) — at 1:1 the letterbox-fit below becomes
        // a no-op instead of scaling.
        public const int CanvasW = 462;
        public const int CanvasH = 1920;

        readonly Options _opts;
        Thread _thread;
        volatile bool _running;
        D92Device _device;

        public Status CurrentStatus { get; private set; } = Status.Idle;
        public event Action<Status, string> StatusChanged;

        public StreamWorker(Options opts = null)
        {
            _opts = opts ?? new Options();
        }

        /// <summary>Start streaming from the given GDI display device (e.g. "\\.\DISPLAY5").
        /// Returns false immediately if the D92 device can't be opened right now.</summary>
        public bool Start(string gdiDeviceName)
        {
            if (_running)
                return true;

            _device = D92Device.Open();
            if (_device == null)
            {
                SetStatus(Status.DeviceNotFound, "D92 not found (unplugged, or held by another app)");
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
            Log.Info("D92 StreamWorker: {0} ({1})", s, detail);
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
                        _device.SendJpeg(jpeg);
                    }
                }
                catch (Exception ex)
                {
                    // Never just reopen the old handle here — a plain reopen
                    // with no real reset behind it just gives you a handle
                    // into a still-stuck device (see WORK_SUMMARY.md §4.3 in
                    // the parent repo). Recover() below performs an actual
                    // Windows-level device reset (UsbRecovery.TrySoftReplug)
                    // before ever calling D92Device.Open() again.
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

        /// <summary>Disable+re-enable the D92 device node (UsbRecovery), then
        /// poll for it to re-enumerate and reopen a fresh handle. Returns
        /// false (leaving _device null) if either step fails or times out.</summary>
        bool TryRecover()
        {
            if (!UsbRecovery.TryFullRecover())
                return false;

            var deadline = Environment.TickCount + _opts.RecoveryPollTimeoutMs;
            while (_running && Environment.TickCount < deadline)
            {
                _device = D92Device.Open();
                if (_device != null)
                    return true;
                Thread.Sleep(300);
            }
            return false;
        }

        /// <summary>Grab the current frame of gdiDeviceName and letterbox-fit it
        /// into the panel's pre-rotation canvas shape (CanvasH x CanvasW),
        /// then rotate. Returns null if the display's mode can't be read
        /// (display no longer exists).</summary>
        Bitmap CaptureAndFit(string gdiDeviceName)
        {
            if (!TryGetDisplayBounds(gdiDeviceName, out var bounds))
                return null;

            using (var raw = CaptureScreenRegion(bounds))
            {
                var fitted = new Bitmap(CanvasH, CanvasW, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(fitted))
                {
                    g.Clear(Color.Black);
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.CompositingQuality = CompositingQuality.HighQuality;

                    float scale = Math.Min((float)CanvasH / raw.Width, (float)CanvasW / raw.Height);
                    int w = Math.Max(1, (int)Math.Round(raw.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(raw.Height * scale));
                    int x = (CanvasH - w) / 2;
                    int y = (CanvasW - h) / 2;
                    g.DrawImage(raw, x, y, w, h);
                }

                if (_opts.RotateDegrees % 360 == 0)
                    return fitted;

                RotateFlipType rft;
                switch (((_opts.RotateDegrees % 360) + 360) % 360)
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
                    Native.BitBlt(hdcDest, 0, 0, bounds.Width, bounds.Height,
                        hdcSrc, bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT);
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

            try
            {
                using (var icon = Icon.FromHandle(cursor.hCursor))
                {
                    g.DrawIcon(icon, cursor.screenPosX - bounds.X - icon.Width / 2,
                                      cursor.screenPosY - bounds.Y - icon.Height / 2);
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
            public const uint CAPTUREBLT = 0x40000000;

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
        }
    }
}
