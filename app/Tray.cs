using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ParsecDisplay
{
    internal class Tray : ApplicationContext
    {
        public static Tray Instance { get; private set; }
        static IWin32Window Owner => new Helper.ArbitraryWindow(MainWindow.Handle);

        NotifyIcon TrayIcon;
        Thread GuiThread;

        ToolStripMenuItem MI_RunOnStartup;
        ToolStripMenuItem MI_KeepScreenOn;
        ToolStripMenuItem MI_AutoStartStreaming;

        // Polls for the D92 USB device showing up while AutoStartStreaming
        // is on, so plugging the panel in later (app already running) also
        // auto-starts streaming, not just the check done once at launch.
        System.Windows.Forms.Timer AutoStreamPollTimer;

        // Windows fires multiple resume events (RESUMESUSPEND, RESUMEAUTOMATIC,
        // possibly RESUMESTANDBY) within ~100ms. We only want to run the
        // resume path once per cycle. Reset by the next suspend.
        int ResumeHandled;

        // Whether D92 streaming was active when PBT_APMSUSPEND fired, so
        // OnResume knows whether to restart it. Single-purpose replacement
        // for the generic multi-display suspend/resume snapshot this build
        // no longer carries (see D92_NOTES.md — the app is locked to exactly
        // one D92-shaped virtual display now, not user-managed ones).
        bool WasStreamingBeforeSuspend;

        // D92 panel streaming (see D92/StreamWorker.cs). Not localized (t_*)
        // yet — Text is set directly in code, see UpdateD92MenuText.
        D92.StreamWorker D92Worker;
        ToolStripMenuItem MI_D92;
        int InD92Toggle;

        // Suppresses AutoStartStreaming from immediately undoing a manual
        // "Stop" click (the poll timer would otherwise restart it within
        // AutoStreamPollTimer's interval, since the device is still plugged
        // in). Set on manual stop, cleared once the device is actually seen
        // unplugged -- so a later replug still auto-starts as expected, and
        // any *non*-manual stop (a real disconnect/failure) is never
        // suppressed, since only the Stop menu click sets this.
        bool ManuallyStoppedD92;

        //  D92 Streaming v{version}
        //  ______________
        //  D92 Streaming
        //  --------------
        //  Options        >   Run on startup
        //                 |   Keep screen on
        //  Language       >   {lang_1}
        //                 |   {lang_2}
        //                 |   ...
        //  --------------
        //  Exit

        public Tray()
        {
            Log.Info("Tray initializing");
            Instance = this;
            Vdd.Controller.Start();

            EnsureD92CustomMode();

            D92Worker = new D92.StreamWorker();
            D92Worker.StatusChanged += OnD92StatusChanged;

            GuiThread = new Thread(App.Main);
            GuiThread.IsBackground = true;
            GuiThread.SetApartmentState(ApartmentState.STA);
            GuiThread.Start();

            var appName = $"{Program.AppName} v{Program.AppVersion}";
            var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);

            TrayIcon = new NotifyIcon()
            {
                Text = appName,
                Icon = appIcon,
                Visible = true,
                ContextMenuStrip = new ContextMenuStrip()
                {
                    Items =
                    {
                        new ToolStripMenuItem(appName, appIcon.ToBitmap(), QueryDriver),
                        new ToolStripSeparator(),
                        (MI_D92 = new ToolStripMenuItem("D92 Streaming", null, ToggleD92Streaming)),
                        new ToolStripSeparator(),
                        new ToolStripMenuItem("t_options")
                        {
                            DropDownItems =
                            {
                                (MI_RunOnStartup = new ToolStripMenuItem("t_run_on_startup",
                                    null, OptionsCheck) { CheckOnClick = true, Checked = Config.RunOnStartup }),
                                (MI_KeepScreenOn = new ToolStripMenuItem("t_keep_screen_on",
                                    null, OptionsCheck) { CheckOnClick = true, Checked = Config.KeepScreenOn }),
                                (MI_AutoStartStreaming = new ToolStripMenuItem("t_auto_start_streaming",
                                    null, OptionsCheck) { CheckOnClick = true, Checked = Config.AutoStartStreaming }),
                            }
                        },
                        new ToolStripSeparator(),
                        new ToolStripMenuItem("t_exit", null, Exit),
                    }
                }
            };

            UpdateContent();
            UpdateD92MenuText();

            TrayIcon.Visible = true;

            PowerEvents.PowerModeChanged += OnPowerModeChanged;

            AutoCreateVirtualDisplay();
            TryAutoStartStreaming();

            // Catches the device showing up later -- app already running,
            // D92 plugged in afterward -- since there's no device-arrival
            // event wired up here, just a cheap periodic recheck. Also what
            // clears ManuallyStoppedD92 once the panel is actually unplugged
            // (see that field's comment) and what retries after a real
            // disconnect/failure while AutoStartStreaming is on.
            AutoStreamPollTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            AutoStreamPollTimer.Tick += (s, e) =>
            {
                if (!D92.D92Device.IsDeviceEnumerated())
                    ManuallyStoppedD92 = false;
                else
                    TryAutoStartStreaming();
            };
            AutoStreamPollTimer.Start();
        }

        /// <summary>Creates the D92-shaped virtual display as soon as the app
        /// launches, so it's there as an extended Windows monitor right away
        /// instead of only appearing once the user clicks "D92 Streaming".
        /// Deliberately does NOT start D92Worker -- pushing frames to the
        /// physical panel is still a separate, manual step from the tray
        /// menu. Logs instead of popping a MessageBox on failure -- an error
        /// dialog on every launch when the driver isn't ready yet would be
        /// annoying, and the user can still see/fix it via the tray menu.</summary>
        void AutoCreateVirtualDisplay()
        {
            try
            {
                if (!TryEnsureVirtualDisplay(out _, out var error))
                    Log.Warn("AutoCreateVirtualDisplay: couldn't set up virtual display: {0}", error);
            }
            catch (Exception ex)
            {
                Log.Error("AutoCreateVirtualDisplay threw: {0}", ex);
            }
        }

        /// <summary>Starts D92 streaming without user interaction when
        /// Config.AutoStartStreaming is on, the panel's USB device is
        /// present, nothing is streaming/recovering already, and this isn't
        /// right after a manual Stop (see ManuallyStoppedD92). Called at
        /// launch, whenever the option is turned on, and from
        /// AutoStreamPollTimer's tick. Mirrors ToggleD92Streaming's start
        /// path but logs instead of popping a MessageBox on failure, same
        /// reasoning as AutoCreateVirtualDisplay.</summary>
        void TryAutoStartStreaming()
        {
            if (!Config.AutoStartStreaming || ManuallyStoppedD92)
                return;
            if (D92Worker.CurrentStatus == D92.StreamWorker.Status.Streaming
                || D92Worker.CurrentStatus == D92.StreamWorker.Status.Recovering)
                return;
            if (!D92.D92Device.IsDeviceEnumerated())
                return;

            if (Interlocked.Exchange(ref InD92Toggle, 1) != 0)
                return; // a manual toggle (or another auto-start attempt) is already in flight

            try
            {
                if (!TryEnsureVirtualDisplay(out var display, out var error))
                {
                    Log.Warn("TryAutoStartStreaming: couldn't set up virtual display: {0}", error);
                    return;
                }

                if (!D92Worker.Start(display.DeviceName))
                    Log.Warn("TryAutoStartStreaming: D92 not found (unplugged, or held by another app)");
            }
            catch (Exception ex)
            {
                Log.Error("TryAutoStartStreaming threw: {0}", ex);
            }
            finally
            {
                Interlocked.Exchange(ref InD92Toggle, 0);
            }
        }

        private void WarnVddStatus(Device.Status status)
        {
            if (status == Device.Status.OK)
                return;

            string error = null;
            switch (status)
            {
                case Device.Status.RESTART_REQUIRED:
                    error = App.GetTranslation("t_msg_must_restart_pc");
                    break;
                case Device.Status.DISABLED:
                    error = App.GetTranslation("t_msg_driver_is_disabled", Vdd.Core.ADAPTER);
                    break;
                case Device.Status.NOT_INSTALLED:
                    error = App.GetTranslation("t_msg_please_install_driver");
                    break;
                default:
                    error = App.GetTranslation("t_msg_driver_status_not_ok", status);
                    break;
            }

            if (error != null)
                MessageBox.Show(Owner, error, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        public void HandleVddError(Exception ex)
        {
            string message = null;

            if (ex is Vdd.ErrorDriverStatus errStatus)
            {
                switch (errStatus.Status)
                {
                    case Device.Status.RESTART_REQUIRED:
                        message = App.GetTranslation("t_msg_must_restart_pc");
                        break;
                    case Device.Status.DISABLED:
                        message = App.GetTranslation("t_msg_driver_is_disabled", Vdd.Core.ADAPTER);
                        break;
                    case Device.Status.NOT_INSTALLED:
                        message = App.GetTranslation("t_msg_please_install_driver");
                        break;
                    default:
                        message = App.GetTranslation("t_msg_driver_status_not_ok", errStatus.Status);
                        break;
                }
            }
            else if (ex is Vdd.ErrorDeviceHandle)
            {
                message = App.GetTranslation("t_msg_failed_to_obtain_handle");
            }
            else if (ex is Vdd.ErrorExceededLimit errLimit)
            {
                message = App.GetTranslation("t_msg_exceeded_display_limit", errLimit.Limit);
            }
            else if (ex is Vdd.ErrorOperationFailed errOperation)
            {
                switch (errOperation.Type)
                {
                    case Vdd.ErrorOperationFailed.Operation.AddDisplay:
                        message = App.GetTranslation("t_msg_failed_to_add_display");
                        break;
                    case Vdd.ErrorOperationFailed.Operation.RemoveDisplay:
                        message = App.GetTranslation("t_msg_failed_to_remove_display");
                        break;
                }
            }
            else
            {
                message = ex.ToString();
            }

            if (message != null)
            {
                MessageBox.Show(Owner, message,
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnPowerModeChanged(object sender, PowerEvents.PowerBroadcastType type)
        {
            Log.Info("Power event: {0}", type);
            switch (type)
            {
                case PowerEvents.PowerBroadcastType.PBT_APMSUSPEND:
                case PowerEvents.PowerBroadcastType.PBT_APMSTANDBY:
                    Interlocked.Exchange(ref ResumeHandled, 0);
                    WasStreamingBeforeSuspend = D92Worker.CurrentStatus == D92.StreamWorker.Status.Streaming
                        || D92Worker.CurrentStatus == D92.StreamWorker.Status.Recovering;
                    D92Worker.Stop();
                    try { Vdd.Controller.Suspend(); }
                    catch (Exception ex) { Log.Warn("Suspend threw: {0}", ex.Message); }
                    break;

                case PowerEvents.PowerBroadcastType.PBT_APMRESUMEAUTOMATIC:
                case PowerEvents.PowerBroadcastType.PBT_APMRESUMESUSPEND:
                case PowerEvents.PowerBroadcastType.PBT_APMRESUMESTANDBY:
                case PowerEvents.PowerBroadcastType.PBT_APMRESUMECRITICAL:
                    // Coalesce: Windows fires several resume events back-to-back.
                    if (Interlocked.Exchange(ref ResumeHandled, 1) == 0)
                        Task.Run(OnResume);
                    else
                        Log.Debug("Resume event coalesced (already handled)");
                    break;
            }
        }

        void OnResume()
        {
            try
            {
                Log.Info("Resume: begin");
                Vdd.Controller.Resume();
                if (!Vdd.Controller.WaitForReady(10000))
                {
                    Log.Warn("Resume: timed out waiting for driver handle");
                    return;
                }

                if (!WasStreamingBeforeSuspend)
                {
                    Log.Info("Resume: was not streaming before suspend, nothing to do");
                    return;
                }

                if (TryEnsureVirtualDisplay(out var display, out var error))
                    D92Worker.Start(display.DeviceName);
                else
                    Log.Warn("Resume: failed to re-establish D92 display: {0}", error);
            }
            catch (Exception ex) { Log.Error("Resume threw: {0}", ex); }
        }

        // MainWindow.xaml.cs and Components/DisplayItem.xaml.cs still call
        // these two — kept as thin pass-throughs to Vdd.Controller purely so
        // those files (unreachable now that MainWindow is never shown, see
        // App.xaml.cs) keep compiling without being rewritten. Not reachable
        // from the tray menu or any other normal user flow in this build.
        public void AddDisplay(object sender, EventArgs e)
        {
            try { Vdd.Controller.AddDisplay(); }
            catch (Exception ex) { HandleVddError(ex); }
        }

        public void RemoveDisplay(int index)
        {
            try { Vdd.Controller.RemoveDisplay(index); }
            catch (Vdd.ErrorOperationFailed)
            {
                MessageBox.Show(Owner, App.GetTranslation("t_msg_failed_to_remove_display"),
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Start/stop D92 panel streaming. On start: ensure a Parsec virtual
        /// display exists (reusing one if already active, otherwise creating
        /// one and polling briefly for Windows to enumerate it), give it a
        /// normal desktop-usable mode, then hand its GDI device name to
        /// StreamWorker. See D92/StreamWorker.cs for the capture/encode/push
        /// pipeline and D92/D92Device.cs for the wire protocol.
        /// </summary>
        void ToggleD92Streaming(object sender, EventArgs e)
        {
            if (Interlocked.Exchange(ref InD92Toggle, 1) != 0)
                return;

            try
            {
                if (D92Worker.CurrentStatus == D92.StreamWorker.Status.Streaming
                    || D92Worker.CurrentStatus == D92.StreamWorker.Status.Recovering)
                {
                    D92Worker.Stop();
                    ManuallyStoppedD92 = true; // don't let AutoStartStreaming immediately undo this
                    return;
                }

                ManuallyStoppedD92 = false;

                if (!TryEnsureVirtualDisplay(out var display, out var error))
                {
                    MessageBox.Show(Owner, error, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!D92Worker.Start(display.DeviceName))
                {
                    MessageBox.Show(Owner, App.GetTranslation("t_msg_d92_not_found"),
                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ToggleD92Streaming threw: {0}", ex);
                if (ex is Vdd.ErrorDriverStatus || ex is Vdd.ErrorDeviceHandle
                    || ex is Vdd.ErrorExceededLimit || ex is Vdd.ErrorOperationFailed)
                {
                    HandleVddError(ex);
                }
                else
                {
                    MessageBox.Show(Owner, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                Interlocked.Exchange(ref InD92Toggle, 0);
            }
        }

        /// <summary>
        /// Writes the D92 panel's exact shape (1920x462 landscape, pre-rotation —
        /// see StreamWorker.CanvasH x CanvasW) into the VDD driver's custom-mode
        /// registry preset (HKLM\SOFTWARE\Parsec\vdd), so it shows up as a
        /// selectable mode at all — Windows can only ChangeMode into a resolution
        /// the driver actually enumerates, and by default VDD only offers its
        /// small set of common desktop presets (see docs/PARSEC_VDD_SPECS.md in
        /// this repo), none of which is anywhere near this panel's shape.
        /// Requires admin rights (see app.manifest) — called once at startup;
        /// if it throws (e.g. somehow launched unelevated), streaming will still
        /// work but ChangeMode below won't be able to reach exactly 1920x462.
        ///
        /// HKLM\SOFTWARE\Parsec\vdd is NOT this app's own key — it's the VDD
        /// driver's single system-wide custom-mode table (5 slots total, hard
        /// limit baked into the driver, see docs/PARSEC_VDD_RE.md §8), shared
        /// by anything on the machine using this same driver, including the
        /// real Parsec client if it's installed. Overwriting all 5 slots with
        /// just our one entry (the old behavior here) would silently wipe out
        /// whatever custom resolutions that other software had configured.
        /// So this reads what's already there first and only adds our entry
        /// if it's missing, instead of replacing the table outright.
        /// </summary>
        void EnsureD92CustomMode()
        {
            try
            {
                var target = new Display.Mode(D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, 60);
                var existing = Vdd.Utils.GetCustomDisplayModes();

                if (existing.Any(m => m.Width == target.Width && m.Height == target.Height && m.Hz == target.Hz))
                {
                    Log.Info("D92 custom mode preset already present: {0}x{1}@{2}",
                        target.Width, target.Height, target.Hz);
                    return;
                }

                var merged = new List<Display.Mode>(existing);
                if (merged.Count >= 5)
                {
                    // All 5 slots already taken by someone else's presets and
                    // ours isn't among them. There's no room to add without
                    // dropping one of theirs -- drop the oldest (slot 0)
                    // rather than silently failing to register ours, since
                    // D92 streaming being locked to the wrong resolution is
                    // the whole point of this function.
                    Log.Warn("D92 custom mode preset: all 5 VDD slots taken by other entries, evicting the oldest to make room");
                    merged.RemoveAt(0);
                }
                merged.Add(target);

                Vdd.Utils.SetCustomDisplayModes(merged);
                Log.Info("D92 custom mode preset written: {0}x{1}@{2} (kept {3} pre-existing entries)",
                    target.Width, target.Height, target.Hz, existing.Count);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to write D92 custom mode preset (need admin rights): {0}", ex.Message);
            }
        }

        /// <summary>Ensures a Parsec ("PSCCDD0") display exists at exactly the D92
        /// panel's shape. Always removes and re-adds any already-active one
        /// instead of reusing it as-is — a display created before
        /// EnsureD92CustomMode() ran (or before this session) won't have picked
        /// up the 1920x462 preset, and there's no API to refresh a live display's
        /// mode list short of recreating it.</summary>
        bool TryEnsureVirtualDisplay(out Display display, out string error)
        {
            display = null;
            error = null;

            Display FindActiveParsecDisplay()
            {
                foreach (var d in Display.GetAllDisplays())
                    if (d.Active && !string.IsNullOrEmpty(d.DeviceName)
                        && d.DisplayName.IndexOf("PSCCDD0", StringComparison.OrdinalIgnoreCase) >= 0)
                        return d;
                return null;
            }

            var existing = FindActiveParsecDisplay();

            // Reuse it as-is if it's already at the D92 shape — no need to
            // tear down and rebuild a perfectly good display on every single
            // Start(). Only remove+recreate when it's actually wrong (e.g. it
            // was created before EnsureD92CustomMode() ever ran, back when
            // the registry preset didn't exist yet).
            if (existing != null
                && existing.CurrentMode != null
                && existing.CurrentMode.Width == D92.StreamWorker.CanvasH
                && existing.CurrentMode.Height == D92.StreamWorker.CanvasW)
            {
                display = existing;
                return true;
            }

            if (existing != null)
            {
                Log.Info("TryEnsureVirtualDisplay: existing display index={0} is {1}x{2}, not {3}x{4} — recreating",
                    existing.DisplayIndex, existing.CurrentMode?.Width, existing.CurrentMode?.Height,
                    D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW);
                try { Vdd.Controller.RemoveDisplay(existing.DisplayIndex); }
                catch (Exception ex) { Log.Warn("Remove existing display failed: {0}", ex.Message); }
                Thread.Sleep(300); // let the removal settle before re-adding
            }

            Vdd.Controller.AddDisplay(); // throws Vdd.Error* on failure — let the caller handle it

            for (int i = 0; i < 30 && display == null; i++)
            {
                Thread.Sleep(100);
                display = FindActiveParsecDisplay();
            }

            if (display == null)
            {
                error = App.GetTranslation("t_msg_display_not_active");
                return false;
            }

            bool changed = false;
            try { changed = display.ChangeMode(D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, 60, Display.Orientation.Landscape); }
            catch (Exception ex) { Log.Warn("ChangeMode({0}x{1}) failed: {2}", D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, ex.Message); }

            if (!changed)
            {
                error = App.GetTranslation("t_msg_display_mode_failed",
                    D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, Program.AppName);
                return false;
            }

            return true;
        }

        void OnD92StatusChanged(D92.StreamWorker.Status status, string detail)
        {
            Invoke(() =>
            {
                UpdateD92MenuText();

                switch (status)
                {
                    case D92.StreamWorker.Status.Recovering:
                        TrayIcon.ShowBalloonTip(3000, Program.AppName,
                            App.GetTranslation("t_msg_d92_recovering", detail),
                            ToolTipIcon.Info);
                        break;

                    case D92.StreamWorker.Status.Streaming when detail == "recovered via soft replug":
                        TrayIcon.ShowBalloonTip(3000, Program.AppName,
                            App.GetTranslation("t_msg_d92_recovered"), ToolTipIcon.Info);
                        break;

                    case D92.StreamWorker.Status.Disconnected:
                    case D92.StreamWorker.Status.CaptureSourceGone:
                        // No physical replug needed here: the handle is just
                        // closed (StreamWorker.Stop() already ran internally
                        // after recovery gave up), same as after a normal
                        // Stop(). Clicking "Start D92 Streaming" again opens a
                        // fresh handle and replays the wake sequence
                        // (D92Device.WakeAndSetBrightness) before streaming —
                        // that's confirmed to revive a panel left black by
                        // this kind of reopen (WORK_SUMMARY.md §8.13 in the
                        // parent repo). A physical replug is only ever needed
                        // if the device stops enumerating entirely, which
                        // shows up as DeviceNotFound on the next Start(), not
                        // as this status.
                        TrayIcon.ShowBalloonTip(5000, Program.AppName,
                            App.GetTranslation("t_msg_d92_stopped", status, detail),
                            ToolTipIcon.Warning);
                        break;
                }
            });
        }

        void UpdateD92MenuText()
        {
            if (MI_D92 == null) return;

            switch (D92Worker.CurrentStatus)
            {
                case D92.StreamWorker.Status.Streaming:
                    MI_D92.Text = App.GetTranslation("t_d92_stop_streaming");
                    break;
                case D92.StreamWorker.Status.Recovering:
                    MI_D92.Text = App.GetTranslation("t_d92_recovering");
                    break;
                default:
                    MI_D92.Text = App.GetTranslation("t_d92_start_streaming");
                    break;
            }
        }

        public void QueryDriver(object sender, EventArgs e)
        {
            ShowApp();

            var status = Vdd.Core.QueryStatus(out var version);
            var caption = $"{Program.AppName} v{Program.AppVersion}";

            // D92.D92Device.IsDeviceEnumerated() only walks SetupDi (no
            // CreateFileW) -- safe to call here even while StreamWorker
            // already holds the one handle it's allowed to hold.
            var usbPresent = D92.D92Device.IsDeviceEnumerated();

            // CurrentStatus is whatever StreamWorker last landed on, not a
            // live re-check -- if streaming was never (re)started since the
            // device was last plugged in, this can still read DeviceNotFound
            // or Disconnected even though usbPresent is true right now (e.g.
            // right after a physical replug). That's not stale/wrong, just
            // unintuitive side by side with "USB: connected" -- spell out
            // why instead of leaving it looking contradictory.
            var streamingLine = D92Worker.CurrentStatus.ToString();
            if (usbPresent && (D92Worker.CurrentStatus == D92.StreamWorker.Status.DeviceNotFound
                             || D92Worker.CurrentStatus == D92.StreamWorker.Status.Disconnected))
            {
                streamingLine += App.GetTranslation("t_msg_streaming_status_stale");
            }

            MessageBox.Show(Owner,
                $"{App.GetTranslation("t_msg_vdd_driver")}\n\n" +
                $"- {App.GetTranslation("t_label_name")}: {Vdd.Core.ADAPTER}\n" +
                $"- {App.GetTranslation("t_label_version")}: {version}\n" +
                $"- {App.GetTranslation("t_msg_driver_status")}: {status}\n\n" +
                $"{App.GetTranslation("t_msg_d92_panel")}\n\n" +
                $"- {App.GetTranslation("t_label_usb")}: " +
                $"{(usbPresent ? App.GetTranslation("t_usb_connected") : App.GetTranslation("t_usb_not_found"))}\n" +
                $"- {App.GetTranslation("t_label_streaming")}: {streamingLine}",
                caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void OptionsCheck(object sender, EventArgs e)
        {
            if (sender == MI_RunOnStartup)
                Config.RunOnStartup = MI_RunOnStartup.Checked;
            else if (sender == MI_KeepScreenOn)
                Config.KeepScreenOn = MI_KeepScreenOn.Checked;
            else if (sender == MI_AutoStartStreaming)
            {
                Config.AutoStartStreaming = MI_AutoStartStreaming.Checked;
                if (Config.AutoStartStreaming)
                    TryAutoStartStreaming();
            }
        }

        // MainWindow (the generic display-management dashboard) is never
        // shown in this build — see App.xaml.cs — so there's nothing for
        // double-click / the tray "app name" item to bring to front. Kept as
        // a no-op rather than removed since Program.cs's single-instance
        // signal handler still calls Tray.Instance.ShowApp.
        public void ShowApp()
        {
        }

        public void UpdateContent()
        {
            void UpdateItem(ToolStripItem item, bool submenu)
            {
                if (item is ToolStripMenuItem mi)
                {
                    if (mi.Tag is string t) { }
                    else
                    {
                        t = mi.Text;
                        mi.Tag = t;
                    }

                    if (!string.IsNullOrEmpty(t) && t.StartsWith("t_"))
                    {
                        mi.Text = App.GetTranslation(t);

                        if (submenu && mi.HasDropDownItems)
                        {
                            foreach (ToolStripItem sub in mi.DropDownItems)
                            {
                                UpdateItem(sub, false);
                            }
                        }
                    }
                }
            }

            var items = TrayIcon.ContextMenuStrip.Items;
            for (int i = 0; i < items.Count; i++)
            {
                UpdateItem(items[i], true);
            }
        }

        void Exit(object sender, EventArgs e)
        {
            AutoStreamPollTimer?.Stop();
            AutoStreamPollTimer?.Dispose();

            D92Worker?.Stop();

            var displays = Vdd.Core.GetDisplays();
            Log.Info("Exit requested ({0} displays)", displays.Count);

            PowerEvents.PowerModeChanged -= OnPowerModeChanged;

            // This build owns exactly one D92-shaped virtual display end to
            // end (created on Start, meant to disappear on Exit) — no restore
            // config, no confirmation prompt, just clean up.
            // Best-effort explicit removal in reverse order (preserves Windows 10
            // Connectivity registry config). Per-display failures are swallowed —
            // closing the device handle in Controller.Stop triggers the driver's
            // keep-alive watchdog to auto-remove any stragglers within ~1 s.
            for (int i = displays.Count - 1; i >= 0; i--)
            {
                try { Vdd.Controller.RemoveDisplay(displays[i].DisplayIndex); }
                catch (Exception ex)
                {
                    Log.Warn("Exit: remove index {0} failed: {1}", displays[i].DisplayIndex, ex.Message);
                }
            }

            App.Current?.Dispatcher.Invoke(App.Current.Shutdown);
            GuiThread.Join();

            Vdd.Controller.Stop();

            TrayIcon.Visible = false;
            Application.Exit();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TrayIcon.Dispose();
            }

            base.Dispose(disposing);
        }

        public void Invoke(Action action)
        {
            TrayIcon.ContextMenuStrip.BeginInvoke(action);
        }
    }
}