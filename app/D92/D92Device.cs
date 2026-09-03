using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ParsecDisplay.D92
{
    /// <summary>
    /// C# port of the StreamDock D92 protocol client (see the Python reference
    /// implementation, streamdock.py, and WORK_SUMMARY.md in the parent
    /// reverse-engineering repo for the full evidence trail).
    ///
    /// Wire protocol summary:
    ///   - VID 0x5548 / PID 0x1011, single vendor-defined HID interface
    ///     (Usage Page 0xFFA0). OutputReportByteLength = 1025 (1 byte
    ///     ReportID=0x00 + 1024 bytes of wire data); the ReportID byte is a
    ///     Windows HID API convention only — it never appears on the wire.
    ///   - HidD_SetOutputReport does NOT work on this device (err=87). Must
    ///     use WriteFile with FILE_FLAG_OVERLAPPED.
    ///   - Image frames: 32-byte "CRT\0\0" + "DRA" header (only on the first
    ///     1024-byte chunk) followed by JPEG bytes, chunked to 1024 bytes,
    ///     zero-padded on the last chunk.
    ///   - Control commands share the same 1024-byte envelope: "CRT\0\0" +
    ///     3-letter verb + parameter byte(s) at a verb-specific offset.
    ///     Only LIG (brightness, offset 10) and DIS (wake, no real payload)
    ///     are capture-verified. Rotation (SET) sends capture-correct bytes
    ///     but has no observed effect — always rotate host-side before
    ///     encoding, matching Python's StreamDock.send_image(rotate=...).
    ///
    /// Critical operating constraints (violating these reliably bricks the
    /// display until a physical unplug/replug — see WORK_SUMMARY.md §4/§7):
    ///   1. Open the handle once and hold it for the caller's lifetime. Do
    ///      NOT reopen after a write failure / disconnect — surface the
    ///      failure to the caller instead (see StreamWorker's policy).
    ///   2. Never let the OUT endpoint go idle for long while a session is
    ///      open. The caller must keep pushing frames continuously.
    /// </summary>
    public sealed class D92Device : IDisposable
    {
        public const int VendorId = 0x5548;
        public const int ProductId = 0x1011;

        const int OutLen = 1025;   // ReportID + 1024-byte wire envelope
        const int Chunk = 1024;
        const int HdrLen = 32;

        static readonly byte[] Magic = { 0x43, 0x52, 0x54, 0x00, 0x00 }; // "CRT\0\0"

        IntPtr _handle = IntPtr.Zero;

        public bool IsOpen => _handle != IntPtr.Zero && _handle != Native.InvalidHandle;

        D92Device(IntPtr handle)
        {
            _handle = handle;
        }

        /// <summary>
        /// True if a D92 HID device path is currently enumerated by Windows
        /// (plugged in and bound to the HID driver), without opening it.
        /// Safe to call any time, including while StreamWorker already holds
        /// the one-and-only handle it's allowed to hold (see the class-level
        /// operating constraints above) -- this only walks SetupDi, it never
        /// calls CreateFileW, so it can't be the reopen that bricks the panel.
        /// Useful for status/diagnostics UI that shouldn't touch the handle.
        /// </summary>
        public static bool IsDeviceEnumerated() => FindDevicePath() != null;

        /// <summary>
        /// Find and open the first D92 HID device path. Returns null if no
        /// device is currently enumerated (unplugged, or exclusively opened
        /// by another process such as the official MiraBox software).
        /// </summary>
        public static D92Device Open()
        {
            var path = FindDevicePath();
            if (path == null)
                return null;

            var handle = Native.CreateFileW(
                path,
                Native.GENERIC_READ | Native.GENERIC_WRITE,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero,
                Native.OPEN_EXISTING,
                Native.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle == Native.InvalidHandle)
                return null;

            Native.HidD_SetNumInputBuffers(handle, 128);
            Native.HidD_FlushQueue(handle);

            return new D92Device(handle);
        }

        static string FindDevicePath()
        {
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

            IntPtr deviceInfoSet = Native.SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == Native.InvalidHandle)
                return null;

            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);

                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    int requiredSize = 0;
                    Native.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);
                    if (requiredSize == 0)
                        continue;

                    var detailBuffer = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA is 6 on 64-bit
                        // (4-byte cbSize field + pointer alignment quirk); write
                        // it manually rather than Marshal.SizeOf<T> to avoid the
                        // classic x64 marshaling pitfall for this struct.
                        Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                        if (!Native.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, detailBuffer, requiredSize, ref requiredSize, IntPtr.Zero))
                            continue;

                        string devicePath = Marshal.PtrToStringUni(detailBuffer + 4);
                        if (string.IsNullOrEmpty(devicePath))
                            continue;

                        if (MatchesVidPid(devicePath, VendorId, ProductId))
                            return devicePath;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuffer);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            return null;
        }

        static bool MatchesVidPid(string devicePath, int vid, int pid)
        {
            // Device paths look like \\?\hid#vid_5548&pid_1011#...
            string lower = devicePath.ToLowerInvariant();
            string vidTag = $"vid_{vid:x4}";
            string pidTag = $"pid_{pid:x4}";
            return lower.Contains(vidTag) && lower.Contains(pidTag);
        }

        /// <summary>
        /// Write one 1024-byte wire chunk (zero-padded if shorter). Throws
        /// on failure — the caller decides whether/how to react (see the
        /// class-level constraints above: never silently reopen).
        /// </summary>
        void Send(byte[] wire, int retries = 3, int retryDelayMs = 50)
        {
            if (wire.Length > Chunk)
                throw new ArgumentException("wire chunk must be <= 1024 bytes");

            var packet = new byte[OutLen];
            Array.Copy(wire, 0, packet, 1, wire.Length); // [0] stays 0x00 = ReportID

            Exception last = null;
            for (int attempt = 0; attempt < retries; attempt++)
            {
                try
                {
                    WriteFileOverlapped(packet);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < retries - 1)
                        System.Threading.Thread.Sleep(retryDelayMs);
                }
            }
            throw new D92IOException($"D92 write failed after {retries} in-place retries: {last?.Message}", last);
        }

        void WriteFileOverlapped(byte[] packet, int timeoutMs = 2000)
        {
            var overlapped = new Native.OVERLAPPED();
            overlapped.hEvent = Native.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
            if (overlapped.hEvent == IntPtr.Zero)
                throw new D92IOException("CreateEvent failed");

            try
            {
                int written;
                bool ok = Native.WriteFile(_handle, packet, packet.Length, out written, ref overlapped);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Native.ERROR_IO_PENDING)
                    {
                        uint wait = Native.WaitForSingleObject(overlapped.hEvent, (uint)timeoutMs);
                        if (wait != Native.WAIT_OBJECT_0)
                        {
                            Native.CancelIo(_handle);
                            throw new D92IOException("write timeout");
                        }
                        if (!Native.GetOverlappedResult(_handle, ref overlapped, out written, false))
                            throw new D92IOException($"GetOverlappedResult failed, err={Marshal.GetLastWin32Error()}");
                    }
                    else
                    {
                        throw new D92IOException($"WriteFile failed, err={err}");
                    }
                }
            }
            finally
            {
                Native.CloseHandle(overlapped.hEvent);
            }
        }

        static byte[] BuildDraHeader(int jpegLen)
        {
            var hdr = new byte[HdrLen];
            Array.Copy(Magic, 0, hdr, 0, 5);
            hdr[5] = (byte)'D'; hdr[6] = (byte)'R'; hdr[7] = (byte)'A';
            hdr[8] = 0x00;
            int total = HdrLen + jpegLen;
            hdr[9] = (byte)((total >> 16) & 0xFF);
            hdr[10] = (byte)((total >> 8) & 0xFF);
            hdr[11] = (byte)(total & 0xFF);
            hdr[12] = 0xB1;
            // [13..31] already zero
            return hdr;
        }

        /// <summary>Push a full JPEG frame, chunked to 1024 bytes. Returns chunk count.</summary>
        public int SendJpeg(byte[] jpeg)
        {
            var header = BuildDraHeader(jpeg.Length);
            var payload = new byte[header.Length + jpeg.Length];
            Array.Copy(header, 0, payload, 0, header.Length);
            Array.Copy(jpeg, 0, payload, header.Length, jpeg.Length);

            int chunks = 0;
            for (int off = 0; off < payload.Length; off += Chunk)
            {
                int len = Math.Min(Chunk, payload.Length - off);
                var wire = new byte[len];
                Array.Copy(payload, off, wire, 0, len);
                Send(wire);
                chunks++;
            }
            return chunks;
        }

        static byte[] BuildCtrl(string verb, int? valueOffset = null, int value = 0, int valueLen = 1)
        {
            if (verb.Length != 3)
                throw new ArgumentException("verb must be 3 characters");

            int need = 8 + (valueOffset.HasValue ? valueOffset.Value + valueLen - 8 : 0);
            var buf = new byte[Math.Max(8, need)];
            Array.Copy(Magic, 0, buf, 0, 5);
            buf[5] = (byte)verb[0]; buf[6] = (byte)verb[1]; buf[7] = (byte)verb[2];

            if (valueOffset.HasValue)
            {
                int off = valueOffset.Value;
                for (int i = 0; i < valueLen; i++)
                    buf[off + valueLen - 1 - i] = (byte)((value >> (8 * i)) & 0xFF); // big-endian
            }
            return buf;
        }

        /// <summary>Brightness 0..100. Capture-verified: verb "LIG", 1-byte value at offset 10.</summary>
        public void SetBrightness(int value)
        {
            value = Math.Max(0, Math.Min(100, value));
            Send(BuildCtrl("LIG", 10, value));
        }

        /// <summary>Wake/display-on. Capture-verified: verb "DIS", zero payload observed.</summary>
        public void ScreenOn()
        {
            Send(BuildCtrl("DIS"));
        }

        /// <summary>
        /// Replays the official app's connect opening sequence: DIS (wake) ->
        /// ~450ms -> LIG (brightness). Confirmed byte-for-byte identical across
        /// three real captures (two "official app revives a black panel"
        /// sessions plus one plain startup) in the parent repo's
        /// WORK_SUMMARY.md §8.12/§8.13 -- the official app does this on every
        /// connect, not just to fix a black panel, and it is the one thing no
        /// recovery attempt in this port had tried before it was confirmed to
        /// actually revive a dead panel on real hardware. Call this right
        /// after every successful Open() (fresh start and post-recovery
        /// reopen alike), then start streaming immediately -- do not leave
        /// the OUT endpoint idle afterward.
        /// </summary>
        public void WakeAndSetBrightness(int brightness = 50, int delayMs = 450)
        {
            ScreenOn();
            System.Threading.Thread.Sleep(delayMs);
            SetBrightness(brightness);
        }

        public void Dispose()
        {
            if (IsOpen)
            {
                Native.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        sealed class D92IOException : Exception
        {
            public D92IOException(string message, Exception inner = null) : base(message, inner) { }
        }

        static class Native
        {
            public const uint GENERIC_READ = 0x80000000;
            public const uint GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x1;
            public const uint FILE_SHARE_WRITE = 0x2;
            public const uint OPEN_EXISTING = 3;
            public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
            public const int ERROR_IO_PENDING = 997;
            public const uint WAIT_OBJECT_0 = 0;
            public static readonly IntPtr InvalidHandle = new IntPtr(-1);

            public const uint DIGCF_PRESENT = 0x2;
            public const uint DIGCF_DEVICEINTERFACE = 0x10;

            [StructLayout(LayoutKind.Sequential)]
            public struct OVERLAPPED
            {
                public IntPtr Internal;
                public IntPtr InternalHigh;
                public int Offset;
                public int OffsetHigh;
                public IntPtr hEvent;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVICE_INTERFACE_DATA
            {
                public int cbSize;
                public Guid InterfaceClassGuid;
                public int Flags;
                public IntPtr Reserved;
            }

            [DllImport("hid.dll")]
            public static extern void HidD_GetHidGuid(out Guid hidGuid);

            [DllImport("hid.dll")]
            public static extern bool HidD_SetNumInputBuffers(IntPtr handle, uint count);

            [DllImport("hid.dll")]
            public static extern bool HidD_FlushQueue(IntPtr handle);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateFileW(string filename, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool WriteFile(IntPtr handle, byte[] buffer, int numberOfBytesToWrite, out int numberOfBytesWritten, ref OVERLAPPED overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern IntPtr CreateEventW(IntPtr securityAttributes, bool manualReset, bool initialState, IntPtr name);

            [DllImport("kernel32.dll")]
            public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

            [DllImport("kernel32.dll")]
            public static extern bool CancelIo(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool GetOverlappedResult(IntPtr handle, ref OVERLAPPED overlapped, out int bytesTransferred, bool wait);

            [DllImport("kernel32.dll")]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
