using System;

namespace ParsecDisplay.Devices
{
    /// <summary>
    /// One open session against a specific connected sidecar-screen device.
    /// StreamWorker holds exactly one at a time (obtained from an
    /// ISidecarDeviceDescriptor.Open()), disposes it on Stop()/failure, and
    /// asks the descriptor for a fresh one to recover -- see D92Device's
    /// class-level doc comment for the operating constraints any
    /// implementation of this needs to honor (open once, never let the
    /// stream go idle, etc.) since those aren't enforced by the interface
    /// itself.
    /// </summary>
    public interface ISidecarDevice : IDisposable
    {
        /// <summary>Best-effort device-specific "make sure the panel is
        /// actually showing something" sequence to run right after opening,
        /// before streaming starts. No-op for devices that don't need one.
        /// Throwing here is treated as "couldn't open this session" by
        /// StreamWorker.</summary>
        void WakeUp();

        /// <summary>Push one already-encoded frame. Return value is purely
        /// informational (e.g. chunk count) for logging. Throws on failure --
        /// StreamWorker treats that as a dropped session and attempts
        /// recovery.</summary>
        int SendFrame(byte[] frameBytes);
    }

    /// <summary>
    /// Describes a supported sidecar-screen device model: how to detect it's
    /// plugged in and how to open a session against it, plus the panel shape
    /// StreamWorker needs to know before any session exists (to size the
    /// virtual display and the VDD custom-mode preset). Register new device
    /// models in SidecarDeviceRegistry.Known.
    /// </summary>
    public interface ISidecarDeviceDescriptor
    {
        string Name { get; }
        int VendorId { get; }
        int ProductId { get; }

        /// <summary>Pre-rotation capture canvas shape (before RotateDegrees
        /// is applied), in the same width/height sense System.Drawing.Bitmap
        /// uses -- at 1:1 with the virtual display's actual mode, the
        /// letterbox-fit in StreamWorker becomes a no-op instead of scaling.</summary>
        int CaptureWidth { get; }
        int CaptureHeight { get; }

        /// <summary>Rotation applied after letterbox-fitting the capture,
        /// before encoding -- matches how this panel is physically mounted
        /// relative to the desktop it mirrors.</summary>
        int RotateDegrees { get; }

        /// <summary>Presence-only check (e.g. SetupDi enumeration) -- must
        /// never open a handle, so it's safe to call anytime, including
        /// while a session from this same descriptor is already open
        /// elsewhere in this process.</summary>
        bool IsPresent();

        /// <summary>Opens a session. Returns null if the device isn't
        /// present or couldn't be opened (e.g. held exclusively by another
        /// process) -- never throws for that case.</summary>
        ISidecarDevice Open();
    }
}
