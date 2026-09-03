using System.Linq;

namespace ParsecDisplay.Devices
{
    /// <summary>
    /// Every sidecar-screen device model this app knows how to drive. D92 is
    /// the only one today -- add new descriptors here (and nowhere else)
    /// when a second device is supported; nothing outside this class and
    /// Devices/&lt;model&gt;/ should need to know a specific device model's
    /// name.
    /// </summary>
    public static class SidecarDeviceRegistry
    {
        public static readonly ISidecarDeviceDescriptor[] Known =
        {
            new D92.D92DeviceDescriptor(),
        };

        /// <summary>The first known device model that's currently plugged
        /// in, or null if none are. Cheap presence-only check (see
        /// ISidecarDeviceDescriptor.IsPresent) -- safe to call often.</summary>
        public static ISidecarDeviceDescriptor FindPresent() =>
            Known.FirstOrDefault(d => d.IsPresent());
    }
}
