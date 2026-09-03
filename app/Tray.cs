using System;
using System.Collections.Generic;
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

        ToolStripMenuItem MI_Language;

        ToolStripMenuItem MI_RunOnStartup;
        ToolStripMenuItem MI_KeepScreenOn;

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

        //  ParsecDisplay v{version}
        //  ______________
        //  D92 Streaming
        //  --------------
        //  Options        >   Run on startup
        //                 |   Keep screen on
        //  Language       >   {lang_1}
        //                 |   {lang_2}
        //                 |   ...
        //  Check update
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
            var translateIcon = (Image)Properties.Resources.ResourceManager.GetObject("translate_icon");

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
                        new ToolStripMenuItem("Recover D92 (power cycle)", null, ManualRecoverD92),
                        new ToolStripSeparator(),
                        new ToolStripMenuItem("t_options")
                        {
                            DropDownItems =
                            {
                                (MI_RunOnStartup = new ToolStripMenuItem("t_run_on_startup",
                                    null, OptionsCheck) { CheckOnClick = true, Checked = Config.RunOnStartup }),
                                (MI_KeepScreenOn = new ToolStripMenuItem("t_keep_screen_on",
                                    null, OptionsCheck) { CheckOnClick = true, Checked = Config.KeepScreenOn }),
                            }
                        },
                        (MI_Language = new ToolStripMenuItem("t_language", translateIcon)),
                        new ToolStripMenuItem("t_check_for_update", null, CheckUpdate),
                        new ToolStripSeparator(),
                        new ToolStripMenuItem("t_exit", null, Exit),
                    }
                }
            };

            var selectedLanguage = Config.Language;
            foreach (var lang in App.Languages)
            {
                var item = new ToolStripMenuItem(lang, null, SetLanguage);
                if (selectedLanguage == lang)
                    item.Checked = true;
                MI_Language.DropDownItems.Add(item);
            }

            UpdateContent();
            UpdateD92MenuText();

            TrayIcon.Visible = true;

            PowerEvents.PowerModeChanged += OnPowerModeChanged;

            Invoke(async () =>
            {
                await Task.Delay(1000);
                CheckUpdate(null, null);
            });
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
        /// <summary>Manual, on-demand version of the same soft-replug
        /// (UsbRecovery.TrySoftReplug) that StreamWorker tries automatically
        /// on a write failure. Added because the automatic path only fires
        /// when a write actually throws — it does nothing for the "writes
        /// keep succeeding but the panel's own display pipeline silently died"
        /// failure mode (WORK_SUMMARY.md §4.3 in the parent repo), which
        /// apparently still happens after streaming runs a while. This button
        /// is the manual fallback until that's diagnosed properly — it does
        /// NOT restart streaming afterward, click "D92 Streaming" separately.</summary>
        async void ManualRecoverD92(object sender, EventArgs e)
        {
            var menuItem = sender as ToolStripMenuItem;
            if (menuItem != null) menuItem.Enabled = false;

            try
            {
                D92Worker.Stop(); // don't fight over the handle with a live worker

                Log.Info("ManualRecoverD92: starting recovery (power cycle, falling back to PnP restart)");
                bool ok = await Task.Run(() => D92.UsbRecovery.TryFullRecover());

                MessageBox.Show(Owner,
                    ok ? "Recovery done (power cycle, or PnP restart fallback — check debug.log for which one worked). Click \"D92 Streaming\" to resume."
                       : "Recovery failed (both power cycle and PnP restart). Check debug.log — likely not running elevated, or the device wasn't found.",
                    Program.AppName, MessageBoxButtons.OK,
                    ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                Log.Error("ManualRecoverD92 threw: {0}", ex);
                MessageBox.Show(Owner, ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (menuItem != null) menuItem.Enabled = true;
            }
        }

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
                    return;
                }

                if (!TryEnsureVirtualDisplay(out var display, out var error))
                {
                    MessageBox.Show(Owner, error, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!D92Worker.Start(display.DeviceName))
                {
                    MessageBox.Show(Owner,
                        "D92 not found. Make sure it's plugged in and the official MiraBox software isn't holding it open.",
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
        /// </summary>
        void EnsureD92CustomMode()
        {
            try
            {
                Vdd.Utils.SetCustomDisplayModes(new List<Display.Mode>
                {
                    new Display.Mode(D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, 60),
                });
                Log.Info("D92 custom mode preset written: {0}x{1}@60",
                    D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW);
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
                error = "Added a virtual display but Windows hasn't reported it active yet. Try again in a moment.";
                return false;
            }

            bool changed = false;
            try { changed = display.ChangeMode(D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, 60, Display.Orientation.Landscape); }
            catch (Exception ex) { Log.Warn("ChangeMode({0}x{1}) failed: {2}", D92.StreamWorker.CanvasH, D92.StreamWorker.CanvasW, ex.Message); }

            if (!changed)
            {
                error = $"Couldn't set the virtual display to {D92.StreamWorker.CanvasH}x{D92.StreamWorker.CanvasW}. " +
                        "Make sure ParsecDisplay is running as Administrator (needed to write the custom-mode " +
                        "registry preset) and try again.";
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
                            $"D92 lost connection, attempting a soft replug... ({detail})",
                            ToolTipIcon.Info);
                        break;

                    case D92.StreamWorker.Status.Streaming when detail == "recovered via soft replug":
                        TrayIcon.ShowBalloonTip(3000, Program.AppName,
                            "D92 streaming recovered.", ToolTipIcon.Info);
                        break;

                    case D92.StreamWorker.Status.Disconnected:
                    case D92.StreamWorker.Status.CaptureSourceGone:
                        TrayIcon.ShowBalloonTip(5000, Program.AppName,
                            $"D92 streaming stopped: {status} ({detail}). " +
                            "Automatic soft-replug recovery was exhausted or is disabled — " +
                            "a physical replug is needed before restarting.",
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
                    MI_D92.Text = "Stop D92 Streaming";
                    break;
                case D92.StreamWorker.Status.Recovering:
                    MI_D92.Text = "D92 Streaming (recovering...)";
                    break;
                default:
                    MI_D92.Text = "Start D92 Streaming";
                    break;
            }
        }

        public void QueryDriver(object sender, EventArgs e)
        {
            ShowApp();

            var status = Vdd.Core.QueryStatus(out var version);
            var caption = $"{Program.AppName} v{Program.AppVersion}";

            MessageBox.Show(Owner,
                $"{Vdd.Core.ADAPTER}\n\n" +
                $"- Version: {version}\n" +
                $"- {App.GetTranslation("t_msg_driver_status")}: {status}",
                caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async void CheckUpdate(object sender, EventArgs e)
        {
            ToolStripMenuItem menuItem = null;
            if (sender is ToolStripMenuItem)
            {
                menuItem = (ToolStripMenuItem)sender;
                menuItem.Enabled = false;
            }

            var newVersion = await Updater.CheckUpdate()
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(newVersion))
            {
                var ret = MessageBox.Show(Owner, App.GetTranslation("t_msg_update_available", newVersion),
                    Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);

                if (ret == DialogResult.Yes)
                {
                    Helper.OpenLink(Updater.DOWNLOAD_URL);
                }
            }
            else if (sender != null)
            {
                MessageBox.Show(Owner, App.GetTranslation("t_msg_up_to_date"),
                    Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (menuItem != null)
            {
                menuItem.Enabled = true;
            }
        }

        void OptionsCheck(object sender, EventArgs e)
        {
            if (sender == MI_RunOnStartup)
                Config.RunOnStartup = MI_RunOnStartup.Checked;
            else if (sender == MI_KeepScreenOn)
                Config.KeepScreenOn = MI_KeepScreenOn.Checked;
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
            for (int i = 1; i < items.Count; i++)
            {
                UpdateItem(items[i], true);
            }
        }

        void Exit(object sender, EventArgs e)
        {
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

        private void SetLanguage(object sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem mi)
            {
                // Recheck options
                foreach (ToolStripMenuItem item in MI_Language.DropDownItems)
                    item.Checked = mi == item;

                // Update language
                var lang = mi.Text;
                App.SetLanguage(lang);
                UpdateContent();
            }
        }
    }
}