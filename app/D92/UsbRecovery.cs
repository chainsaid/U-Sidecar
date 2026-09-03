using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ParsecDisplay.D92
{
    /// <summary>
    /// Software equivalent of a physical unplug/replug for the D92 device,
    /// via Windows' device-manager "Disable device" then "Enable device"
    /// (SetupDiCallClassInstaller / DIF_PROPERTYCHANGE) — the same operation
    /// as right-clicking the device in Device Manager and toggling it off
    /// and back on. Requires admin rights (see app.manifest).
    ///
    /// Why this exists / what it's NOT: earlier attempts at "recovering" a
    /// dead D92 session by reopening the HID handle, or by sending it a
    /// CRT\0\0CONNECT command, never worked (see the parent repo's
    /// WORK_SUMMARY.md §4.3/§4.7) — those only ever act at the application/
    /// HID-report level, and the stuck state lives deeper than that (likely
    /// the device's own display/decode pipeline, not its USB peripheral
    /// controller). A live capture of the official MiraBox software recovering
    /// a dead panel (same repo, WORK_SUMMARY.md, the recovery-capture
    /// analysis) showed it doing a burst of GET_DESCRIPTOR control requests —
    /// the signature of Windows re-enumerating the device — right before the
    /// panel came back. This class reproduces that at the Windows PnP level
    /// instead of guessing at more HID bytes to send.
    ///
    /// This is deliberately NOT the same thing as "reopening the handle after
    /// a write failure" that's banned elsewhere in this codebase — reopening
    /// a plain HID handle with no underlying reset just gives you a handle
    /// that writes into a still-stuck device. This forces an actual
    /// enumeration-level reset first; only reopen the HID handle (via
    /// D92Device.Open()) after this returns true and the device has
    /// re-enumerated.
    /// </summary>
    static class UsbRecovery
    {
        const uint DIF_PROPERTYCHANGE = 0x12;
        const uint DICS_ENABLE = 1;
        const uint DICS_DISABLE = 2;
        const uint DICS_FLAG_GLOBAL = 1;

        /// <summary>
        /// Tries the real fix first — a USB hub port power cycle
        /// (TryPowerCyclePort, electrically equivalent to unplug/replug) —
        /// and falls back to the PnP disable/enable (TrySoftReplug) only if
        /// that IOCTL isn't supported by this system's hub driver. Confirmed
        /// tonight (user testing) that TrySoftReplug alone does NOT bring the
        /// panel back even though it does visibly cycle the device in Device
        /// Manager — it restarts the driver stack but never actually cuts
        /// power to the port, so it can't fix whatever needs a real power-on
        /// reset. Use this method, not TrySoftReplug directly, from now on.
        /// </summary>
        public static bool TryFullRecover()
        {
            if (TryPowerCyclePort(out var reason))
            {
                Log.Info("UsbRecovery: power cycle succeeded");
                return true;
            }
            Log.Warn("UsbRecovery: power cycle unavailable/failed ({0}), falling back to PnP disable/enable", reason);
            return TrySoftReplug();
        }

        /// <summary>
        /// Real power cycle: walks up to the D92's parent USB hub, reads its
        /// port number (the device's CM_DRP_ADDRESS), opens the hub, and
        /// issues IOCTL_USB_HUB_CYCLE_PORT — this briefly cuts and restores
        /// VBUS on that specific port, same as a physical unplug/replug,
        /// without touching any other port on the hub. NOT guaranteed to be
        /// supported: this IOCTL is legacy and some hub drivers (particularly
        /// some USB 3.0 host controller / root hub stacks) reject it.
        /// </summary>
        public static bool TryPowerCyclePort(out string reason)
        {
            reason = null;

            var hidGuid = HidGuid();
            IntPtr hidInfoSet = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (hidInfoSet == Native.InvalidHandle)
            {
                reason = "SetupDiGetClassDevs(HID) failed";
                return false;
            }

            uint devInst;
            try
            {
                if (!FindDeviceInfo(hidInfoSet, out var devInfoData))
                {
                    reason = "D92 not currently enumerated";
                    return false;
                }
                devInst = devInfoData.DevInst;
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(hidInfoSet);
            }

            // devInst here is the HID *function* devnode (one of possibly several
            // interfaces under this device). Walk up the devnode tree until we
            // find an ancestor that resolves to an actual GUID_DEVINTERFACE_USB_HUB
            // interface — the port number (CM_DRP_ADDRESS) belongs to whichever
            // child sits directly under that hub, not necessarily devInst itself.
            // First cut of this method assumed exactly one hop (HID function ->
            // composite device -> hub) and read CM_DRP_ADDRESS off the wrong
            // node; it ended up resolving "the hub" as the D92 composite device
            // itself (confirmed in debug.log: "hub USB\VID_5548&PID_1011\..."),
            // so the IOCTL path silently never fired. This walks as many levels
            // as it takes instead of hardcoding a depth.
            uint child = devInst;
            uint hubDevInst = 0;
            bool foundHub = false;
            for (int hop = 0; hop < 6; hop++)
            {
                if (Native.CM_Get_Parent(out uint parent, child, 0) != Native.CR_SUCCESS)
                    break;

                if (TryOpenHubByDevInst(parent, out _))
                {
                    hubDevInst = parent;
                    foundHub = true;
                    break;
                }

                child = parent;
            }

            if (!foundHub)
            {
                reason = "walked up the devnode tree without finding a USB hub ancestor";
                return false;
            }

            if (!TryGetUlongProperty(child, Native.CM_DRP_ADDRESS, out uint portNumber))
            {
                reason = "CM_DRP_ADDRESS not available on the hub's direct child node";
                return false;
            }

            TryGetDeviceId(hubDevInst, out string hubInstanceId);
            Log.Info("UsbRecovery: D92 is on port {0} of hub {1}", portNumber, hubInstanceId ?? "?");

            if (!TryOpenHubByDevInst(hubDevInst, out string hubPath))
            {
                reason = "couldn't resolve the parent hub's device interface path (second lookup)";
                return false;
            }

            var hubHandle = Native.CreateFileW(hubPath,
                Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

            if (hubHandle == Native.InvalidHandle)
            {
                reason = $"CreateFile(hub) failed, err={Marshal.GetLastWin32Error()}";
                return false;
            }

            try
            {
                var portBuf = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(portBuf, (int)portNumber);
                    bool ok = Native.DeviceIoControl(hubHandle, Native.IOCTL_USB_HUB_CYCLE_PORT,
                        portBuf, 4, IntPtr.Zero, 0, out _, IntPtr.Zero);

                    if (!ok)
                    {
                        reason = $"IOCTL_USB_HUB_CYCLE_PORT failed, err={Marshal.GetLastWin32Error()}";
                        return false;
                    }

                    Log.Info("UsbRecovery: IOCTL_USB_HUB_CYCLE_PORT succeeded on port {0}", portNumber);
                    // The port needs a moment to actually drop and re-establish
                    // power/enumeration before D92Device.Open() would see anything.
                    Thread.Sleep(1500);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(portBuf);
                }
            }
            finally
            {
                Native.CloseHandle(hubHandle);
            }
        }

        static bool TryGetUlongProperty(uint devInst, uint property, out uint value)
        {
            value = 0;
            int len = 4;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                uint regType;
                var cr = Native.CM_Get_DevNode_Registry_Property(devInst, property, out regType, buf, ref len, 0);
                if (cr != Native.CR_SUCCESS)
                    return false;
                value = (uint)Marshal.ReadInt32(buf);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        static bool TryGetDeviceId(uint devInst, out string id)
        {
            var buf = new StringBuilder(512);
            var cr = Native.CM_Get_Device_ID(devInst, buf, buf.Capacity, 0);
            id = cr == Native.CR_SUCCESS ? buf.ToString() : null;
            return id != null;
        }

        /// <summary>Finds the parent hub's device interface path (GUID_DEVINTERFACE_USB_HUB)
        /// by matching each candidate interface's own devnode against parentDevInst.</summary>
        static bool TryOpenHubByDevInst(uint parentDevInst, out string hubPath)
        {
            hubPath = null;
            var hubGuid = new Guid("f18a0e88-c30c-11d0-8815-00a0c906bed8"); // GUID_DEVINTERFACE_USB_HUB

            IntPtr hubInfoSet = Native.SetupDiGetClassDevs(ref hubGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
            if (hubInfoSet == Native.InvalidHandle)
                return false;

            try
            {
                var ifData = new Native.SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);

                for (uint i = 0; Native.SetupDiEnumDeviceInterfaces(hubInfoSet, IntPtr.Zero, ref hubGuid, i, ref ifData); i++)
                {
                    int requiredSize = 0;
                    Native.SetupDiGetDeviceInterfaceDetail(hubInfoSet, ref ifData, IntPtr.Zero, 0, ref requiredSize, IntPtr.Zero);
                    if (requiredSize == 0)
                        continue;

                    var detailBuffer = Marshal.AllocHGlobal(requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                        var candidate = new Native.SP_DEVINFO_DATA();
                        candidate.cbSize = Marshal.SizeOf(candidate);

                        if (!Native.SetupDiGetDeviceInterfaceDetail(hubInfoSet, ref ifData, detailBuffer, requiredSize, ref requiredSize, ref candidate))
                            continue;

                        if (candidate.DevInst == parentDevInst)
                        {
                            hubPath = Marshal.PtrToStringUni(detailBuffer + 4);
                            return !string.IsNullOrEmpty(hubPath);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuffer);
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(hubInfoSet);
            }

            return false;
        }

        static Guid HidGuid()
        {
            Native.HidD_GetHidGuid(out var g);
            return g;
        }

        /// <summary>Disable then re-enable the D92 device node. Returns false if the
        /// device isn't currently enumerated at all, or if either step fails
        /// (commonly: not running elevated). Only restarts the driver stack —
        /// does NOT cut power to the port. Kept as TryFullRecover's fallback;
        /// prefer TryFullRecover / TryPowerCyclePort directly.</summary>
        public static bool TrySoftReplug()
        {
            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

            IntPtr deviceInfoSet = Native.SetupDiGetClassDevs(
                ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);

            if (deviceInfoSet == Native.InvalidHandle)
            {
                Log.Warn("UsbRecovery: SetupDiGetClassDevs failed, err={0}", Marshal.GetLastWin32Error());
                return false;
            }

            try
            {
                if (!FindDeviceInfo(deviceInfoSet, out var devInfoData))
                {
                    Log.Warn("UsbRecovery: D92 not currently enumerated, nothing to cycle");
                    return false;
                }

                Log.Info("UsbRecovery: disabling device node (DevInst={0})", devInfoData.DevInst);
                if (!SetPropertyChange(deviceInfoSet, ref devInfoData, DICS_DISABLE))
                {
                    Log.Warn("UsbRecovery: disable failed, err={0} (are we elevated?)", Marshal.GetLastWin32Error());
                    return false;
                }

                Thread.Sleep(800);

                Log.Info("UsbRecovery: re-enabling device node");
                if (!SetPropertyChange(deviceInfoSet, ref devInfoData, DICS_ENABLE))
                {
                    Log.Warn("UsbRecovery: enable failed, err={0}", Marshal.GetLastWin32Error());
                    return false;
                }

                return true;
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        static bool SetPropertyChange(IntPtr deviceInfoSet, ref Native.SP_DEVINFO_DATA devInfoData, uint stateChange)
        {
            var parms = new Native.SP_PROPCHANGE_PARAMS();
            parms.ClassInstallHeader.cbSize = Marshal.SizeOf<Native.SP_CLASSINSTALL_HEADER>();
            parms.ClassInstallHeader.InstallFunction = DIF_PROPERTYCHANGE;
            parms.StateChange = stateChange;
            parms.Scope = DICS_FLAG_GLOBAL;
            parms.HwProfile = 0;

            if (!Native.SetupDiSetClassInstallParams(deviceInfoSet, ref devInfoData, ref parms, Marshal.SizeOf<Native.SP_PROPCHANGE_PARAMS>()))
                return false;

            return Native.SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, deviceInfoSet, ref devInfoData);
        }

        /// <summary>Walk the HID interface list for a device whose path matches
        /// D92Device's VID/PID, returning its SP_DEVINFO_DATA (the device node,
        /// not the interface).</summary>
        static bool FindDeviceInfo(IntPtr deviceInfoSet, out Native.SP_DEVINFO_DATA devInfoData)
        {
            devInfoData = default;

            Guid hidGuid;
            Native.HidD_GetHidGuid(out hidGuid);

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
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

                    var candidate = new Native.SP_DEVINFO_DATA();
                    candidate.cbSize = Marshal.SizeOf(candidate);

                    if (!Native.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref ifData, detailBuffer, requiredSize, ref requiredSize, ref candidate))
                        continue;

                    string devicePath = Marshal.PtrToStringUni(detailBuffer + 4);
                    if (string.IsNullOrEmpty(devicePath))
                        continue;

                    string lower = devicePath.ToLowerInvariant();
                    if (lower.Contains($"vid_{D92Device.VendorId:x4}") && lower.Contains($"pid_{D92Device.ProductId:x4}"))
                    {
                        devInfoData = candidate;
                        return true;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }

            return false;
        }

        static class Native
        {
            public const uint DIGCF_PRESENT = 0x2;
            public const uint DIGCF_DEVICEINTERFACE = 0x10;
            public static readonly IntPtr InvalidHandle = new IntPtr(-1);

            public const int CR_SUCCESS = 0;
            public const uint CM_DRP_ADDRESS = 0x1D;

            public const uint GENERIC_WRITE = 0x40000000;
            public const uint FILE_SHARE_READ = 0x1;
            public const uint FILE_SHARE_WRITE = 0x2;
            public const uint OPEN_EXISTING = 3;

            // CTL_CODE(FILE_DEVICE_USB=0x22, USB_HUB_CYCLE_PORT=273, METHOD_BUFFERED=0, FILE_ANY_ACCESS=0)
            public const uint IOCTL_USB_HUB_CYCLE_PORT = 0x220444;

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVICE_INTERFACE_DATA
            {
                public int cbSize;
                public Guid InterfaceClassGuid;
                public int Flags;
                public IntPtr Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_DEVINFO_DATA
            {
                public int cbSize;
                public Guid ClassGuid;
                public uint DevInst;
                public IntPtr Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_CLASSINSTALL_HEADER
            {
                public int cbSize;
                public uint InstallFunction;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SP_PROPCHANGE_PARAMS
            {
                public SP_CLASSINSTALL_HEADER ClassInstallHeader;
                public uint StateChange;
                public uint Scope;
                public uint HwProfile;
            }

            [DllImport("hid.dll")]
            public static extern void HidD_GetHidGuid(out Guid hidGuid);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, IntPtr deviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, ref int requiredSize, ref SP_DEVINFO_DATA deviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiSetClassInstallParams(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, ref SP_PROPCHANGE_PARAMS classInstallParams, int classInstallParamsSize);

            [DllImport("setupapi.dll", SetLastError = true)]
            public static extern bool SetupDiCallClassInstaller(uint installFunction, IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData);

            [DllImport("cfgmgr32.dll")]
            public static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, int ulFlags);

            [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW", CharSet = CharSet.Unicode)]
            public static extern int CM_Get_DevNode_Registry_Property(uint dnDevInst, uint ulProperty, out uint pulRegDataType, IntPtr buffer, ref int pulLength, int ulFlags);

            [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW", CharSet = CharSet.Unicode)]
            public static extern int CM_Get_Device_ID(uint dnDevInst, StringBuilder buffer, int bufferLen, int ulFlags);

            [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateFileW(string filename, uint access, uint share, IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);

            [DllImport("kernel32.dll", SetLastError = true)]
            public static extern bool DeviceIoControl(IntPtr handle, uint ioControlCode,
                IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize,
                out int bytesReturned, IntPtr overlapped);

            [DllImport("kernel32.dll")]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
