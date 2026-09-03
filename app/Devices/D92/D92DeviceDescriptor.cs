namespace ParsecDisplay.Devices.D92
{
    /// <summary>Registers the D92 as a known sidecar-screen device model
    /// (see SidecarDeviceRegistry.Known). Panel canvas shape here is the
    /// pre-rotation "landscape" capture canvas -- see WORK_SUMMARY.md §8.7
    /// in the parent repo: the official app authors content on a 1920x462
    /// canvas and rotates 270° before push, matching the panel's apparent
    /// native orientation.</summary>
    public sealed class D92DeviceDescriptor : ISidecarDeviceDescriptor
    {
        public string Name => "D92";
        public int VendorId => D92Device.VendorId;
        public int ProductId => D92Device.ProductId;
        public int CaptureWidth => 1920;
        public int CaptureHeight => 462;
        public int RotateDegrees => 270;

        public bool IsPresent() => D92Device.IsDeviceEnumerated();
        public ISidecarDevice Open() => D92Device.Open();
    }
}
