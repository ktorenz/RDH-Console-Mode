using System.Runtime.InteropServices;
﻿using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Devices.Enumeration;

namespace gcmloader
{
    // ========================================================================
    //  RDH patch: first-boot controller pairing flow (Playnite mode only).
    //
    //  A Playnite theme is XAML-only and cannot run code, so this lives in the
    //  shell. Once Playnite's window is up, if no XInput controller is
    //  connected, a lower-third overlay appears over the boot video / login
    //  screen: "Connect your wireless controller - hold HOME + minus".
    //  While the overlay is up, a Bluetooth watcher auto-accepts pairing for
    //  anything that looks like a gamepad. The moment an XInput slot goes
    //  live, the overlay dissolves and the Aniki login screen is beneath it.
    //
    //  settings.toml keys (all optional):
    //    controller_prompt       = false  -> disables the whole flow
    //    controller_prompt_text  = "..."  -> override the overlay text
    //    controller_prompt_delay = 8      -> seconds after Playnite's window
    //                                        appears before prompting
    // ========================================================================
    public sealed partial class MainWindow
    {
        private Window _rdhPairWindow;
        private AppWindow _rdhPairAppWindow;
        private DeviceWatcher _rdhBtWatcher;
        private volatile bool _rdhPromptDismissed;
        private readonly SharpDX.XInput.Controller[] _rdhXinput =
        {
            new SharpDX.XInput.Controller(SharpDX.XInput.UserIndex.One),
            new SharpDX.XInput.Controller(SharpDX.XInput.UserIndex.Two),
            new SharpDX.XInput.Controller(SharpDX.XInput.UserIndex.Three),
            new SharpDX.XInput.Controller(SharpDX.XInput.UserIndex.Four)
        };

        // Duration of the active Playnite theme's startup video, so the prompt
        // can land on the LOGIN SCREEN rather than on top of the intro
        // animation. Resolves the theme id from fullscreenConfig.json and reads
        // "Startup Video\Startup.mp4" - the file Aniki's theme options write the
        // chosen intro to. Returns zero when anything is missing; the override
        // key controller_prompt_video_wait (seconds) wins when set (0 disables
        // the wait entirely).
        private async Task<TimeSpan> RdhGetStartupVideoWaitAsync()
        {
            try
            {
                try
                {
                    int overrideSec = AppSettings.Load<int>("controller_prompt_video_wait");
                    if (overrideSec >= 0) return TimeSpan.FromSeconds(overrideSec);
                }
                catch { }

                string exe = AutoDetectLauncherPath("playnite");
                if (string.IsNullOrWhiteSpace(exe)) return TimeSpan.Zero;
                string root = Path.GetDirectoryName(exe);

                // portable installs keep config beside the exe; installed mode
                // keeps it under %AppData%\Playnite
                string appDataPn = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Playnite");
                string cfg = File.Exists(Path.Combine(root, "fullscreenConfig.json"))
                    ? Path.Combine(root, "fullscreenConfig.json")
                    : Path.Combine(appDataPn, "fullscreenConfig.json");
                if (!File.Exists(cfg)) return TimeSpan.Zero;

                string themeId = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(cfg))["Theme"]?.ToString();
                if (string.IsNullOrWhiteSpace(themeId)) return TimeSpan.Zero;

                string video = null;
                foreach (string baseDir in new[] { root, appDataPn })
                {
                    string candidate = Path.Combine(baseDir, "Themes", "Fullscreen", themeId, "Startup Video", "Startup.mp4");
                    if (File.Exists(candidate)) { video = candidate; break; }
                }
                if (video == null) return TimeSpan.Zero;

                var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(video);
                var props = await sf.Properties.GetVideoPropertiesAsync();
                return props.Duration;
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH startup-video duration check failed: {ex.Message}");
                return TimeSpan.Zero;
            }
        }

        private bool RdhAnyControllerConnected()
        {
            try { return _rdhXinput.Any(c => c.IsConnected); }
            catch { return false; }
        }

        private DateTime _rdhPairedCheckedUtc = DateTime.MinValue;
        private bool _rdhPairedCached;

        // Is a gamepad already BONDED to this machine? A paired controller that
        // is simply switched off must NOT trigger the prompt - the customer just
        // turns it on and Windows reconnects it. The prompt is only for a console
        // that has never been paired with a controller at all.
        // Result cached briefly so the 2s poll does not hammer device enumeration.
        private async Task<bool> RdhAnyGamepadPairedAsync()
        {
            if ((DateTime.UtcNow - _rdhPairedCheckedUtc).TotalSeconds < 15)
            {
                return _rdhPairedCached;
            }

            try
            {
                // MEASURED: querying association endpoints by ProtocolId triggers
                // a Bluetooth INQUIRY SCAN and takes ~30 SECONDS - that was the
                // entire delay between Playnite appearing and this prompt. An
                // inquiry is right for DISCOVERING a device to pair (the watcher
                // still uses it), but asking "is one already bonded" only needs
                // the local bond database. Measured on real hardware:
                //   AEP ProtocolId query ....... 30153 ms
                //   paired-state selector ......     8 ms
                var found = await DeviceInformation.FindAllAsync(
                    Windows.Devices.Bluetooth.BluetoothDevice.GetDeviceSelectorFromPairingState(true));

                bool any = found.Any(di =>
                {
                    string n = di.Name ?? string.Empty;
                    return n.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("gamepad", StringComparison.OrdinalIgnoreCase) >= 0;
                });

                _rdhPairedCached = any;
                _rdhPairedCheckedUtc = DateTime.UtcNow;
                return any;
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH paired-gamepad check failed: {ex.Message}");
                // On failure assume paired, so a broken check cannot spam the
                // prompt at a customer who already has a working controller.
                _rdhPairedCached = true;
                _rdhPairedCheckedUtc = DateTime.UtcNow;
                return true;
            }
        }

        private async Task RdhControllerPromptLoopAsync()
        {
            try
            {
                bool enabled = true;
                try { enabled = AppSettings.Load<bool>("controller_prompt"); } catch { }
                if (!enabled || !RdhDirectPlayniteMode()) return;

                int delaySeconds = 8;
                try { delaySeconds = Math.Max(0, AppSettings.Load<int>("controller_prompt_delay")); } catch { }

                // Start the paired-device check IMMEDIATELY, concurrently with
                // waiting for Playnite. Device enumeration takes a moment, and
                // doing it after the wait added that cost on top of Playnite's
                // own ~9s startup. By the time Playnite's window exists the
                // answer is already known.
                Task<bool> pairedCheck = RdhAnyGamepadPairedAsync();

                // Wait for Playnite's window so the prompt lands on the boot
                // video / login screen, never on a black boot. Polled tightly
                // so the overlay appears the moment the window exists.
                for (int i = 0; i < 400; i++)
                {
                    var p = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
                    if (p != null && p.MainWindowHandle != IntPtr.Zero) break;
                    await Task.Delay(150);
                }

                bool alreadyPaired;
                try { alreadyPaired = await pairedCheck; }
                catch { alreadyPaired = true; }

                if (alreadyPaired)
                {
                    // Nothing to instruct - the configured delay applies only to
                    // this fallback case.
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                }
                else
                {
                    // Let the theme's intro video finish so the prompt lands on
                    // the login screen, not on top of the animation.
                    TimeSpan videoWait = await RdhGetStartupVideoWaitAsync();
                    if (videoWait > TimeSpan.Zero)
                    {
                        App.StartupTrace($"RDH controller prompt: waiting {videoWait.TotalSeconds:F1}s for the startup video to finish.");
                        await Task.Delay(videoWait + TimeSpan.FromMilliseconds(1200));
                    }
                    App.StartupTrace("RDH controller prompt: nothing paired - prompting at the login screen.");
                }

                bool overlayShown = false;
                while (!_rdhPromptDismissed)
                {
                    bool connected = RdhAnyControllerConnected();

                    if (connected && overlayShown)
                    {
                        RdhHidePairOverlay();
                        RdhStopBtAutoPair();
                        overlayShown = false;
                        App.StartupTrace("RDH controller prompt: controller connected, overlay hidden.");
                    }
                    else if (!connected && !overlayShown)
                    {
                        // Only prompt when NOTHING is bonded. A paired controller
                        // that is merely switched off needs no instructions.
                        if (await RdhAnyGamepadPairedAsync())
                        {
                            await Task.Delay(3000);
                            continue;
                        }

                        App.StartupTrace("RDH controller prompt: no controller paired, showing overlay.");
                        DispatcherQueue.TryEnqueue(RdhShowPairOverlay);
                        RdhStartBtAutoPair();
                        overlayShown = true;

                        // Showing a top-most window from this process can pull
                        // GCM's main window forward with it. Playnite must stay
                        // the visible app - the overlay sits over IT, not over
                        // the GCM shell UI.
                        await RdhKeepPlayniteInFrontAsync();
                    }

                    await Task.Delay(2000);
                }
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH controller prompt loop failed: {ex.Message}");
            }
        }

        // Re-assert Playnite as the foreground window. Called after the overlay
        // appears, and again a moment later, because the z-order change settles
        // asynchronously.
        private async Task RdhKeepPlayniteInFrontAsync()
        {
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    await Task.Delay(400);
                    var pn = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
                    if (pn != null && pn.MainWindowHandle != IntPtr.Zero)
                    {
                        MakeSelfNonTopmost();
                        await ForcefullyBringToForeground(pn.MainWindowHandle);
                    }
                }
                // overlay must remain visible above Playnite
                DispatcherQueue.TryEnqueue(() => _rdhPairAppWindow?.Show(false));
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH keep-playnite-front failed: {ex.Message}");
            }
        }

        // --- overlay -------------------------------------------------------

        private void RdhShowPairOverlay()
        {
            try
            {
                if (_rdhPairWindow != null)
                {
                    _rdhPairAppWindow?.Show(false);
                    return;
                }

                string text = "Connect your wireless controller";
                string subText = "Hold  HOME + −  on the controller to pair";
                try
                {
                    var custom = AppSettings.Load<string>("controller_prompt_text");
                    if (!string.IsNullOrWhiteSpace(custom)) text = custom;
                }
                catch { }

                // Fullscreen art mode engages when the customer-facing images
                // exist in %APPDATA%\gcmsettings:
                //   pairprompt_gamepad.png  - the controller drawing (required
                //                             for fullscreen mode)
                //   pairprompt_home.png     - HOME button icon   (optional)
                //   pairprompt_minus.png    - minus button icon  (optional)
                // Without the gamepad image the compact lower-third banner is
                // used, so nothing breaks while art is in progress.
                string gamepadArt = RdhResolveCardImage("pairprompt_gamepad.png");
                string homeIcon = RdhResolveCardImage("pairprompt_home.png");
                string minusIcon = RdhResolveCardImage("pairprompt_minus.png");
                bool fullscreenArt = gamepadArt != null;

                int scrW = GetScreenWidth();
                int scrH = GetScreenHeight();

                var title = new TextBlock
                {
                    Text = text,
                    FontSize = fullscreenArt ? 44 : 34,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                // Instruction row: Press [HOME] + [-] to pair, with icon images
                // when supplied and text tokens otherwise.
                var instruction = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Spacing = 14,
                    Margin = new Thickness(0, fullscreenArt ? 26 : 14, 0, 0)
                };
                var instrBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(225, 200, 214, 228));
                double instrFont = fullscreenArt ? 30 : 22;
                double iconSize = fullscreenArt ? 64 : 40;

                void AddInstrText(string t)
                {
                    instruction.Children.Add(new TextBlock
                    {
                        Text = t,
                        FontSize = instrFont,
                        Foreground = instrBrush,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                void AddInstrIcon(string file, string fallback)
                {
                    if (file != null)
                    {
                        instruction.Children.Add(new Image
                        {
                            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(file)),
                            Height = iconSize,
                            Stretch = Stretch.Uniform,
                            VerticalAlignment = VerticalAlignment.Center
                        });
                    }
                    else
                    {
                        AddInstrText(fallback);
                    }
                }
                AddInstrText("Press");
                AddInstrIcon(homeIcon, "HOME");
                AddInstrText("+");
                AddInstrIcon(minusIcon, "-");
                AddInstrText("to pair");

                var stack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                if (fullscreenArt)
                {
                    stack.Children.Add(new Image
                    {
                        Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(gamepadArt)),
                        MaxHeight = scrH * 0.52,
                        MaxWidth = scrW * 0.62,
                        Stretch = Stretch.Uniform,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0, 0, 38)
                    });
                }
                stack.Children.Add(title);
                stack.Children.Add(instruction);

                FrameworkElement content;
                if (fullscreenArt)
                {
                    // console-style setup screen: opaque near-black, no card chrome
                    var fsGrid = new Grid
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 4, 7, 12))
                    };
                    fsGrid.Children.Add(stack);
                    content = fsGrid;
                }
                else
                {
                    content = new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(232, 6, 10, 16)),
                        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(18),
                        Padding = new Thickness(56, 34, 56, 34),
                        Child = stack
                    };
                }
                var root = new Grid();
                root.Children.Add(content);
                // gun / mouse tap dismisses for this session
                root.Tapped += (s, e) =>
                {
                    _rdhPromptDismissed = true;
                    RdhHidePairOverlay();
                    RdhStopBtAutoPair();
                };

                _rdhPairWindow = new Window();
                _rdhPairWindow.Content = root;
                _rdhPairWindow.SystemBackdrop = null;

                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_rdhPairWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                _rdhPairAppWindow = AppWindow.GetFromWindowId(id);

                var presenter = OverlappedPresenter.Create();
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
                _rdhPairAppWindow.SetPresenter(presenter);

                // Never let this window take activation. It is an instruction
                // banner, not something to interact with - and activating it
                // pulls the whole GCM process forward, which is the remaining
                // "brief flash of the GCM shell UI" the user reports.
                try
                {
                    int ex = RdhFg.GetWindowLong(hwnd, RdhFg.GWL_EXSTYLE);
                    RdhFg.SetWindowLong(hwnd, RdhFg.GWL_EXSTYLE,
                        ex | RdhFg.WS_EX_NOACTIVATE | RdhFg.WS_EX_TOOLWINDOW);
                }
                catch (Exception exStyle)
                {
                    App.StartupTrace($"RDH overlay no-activate failed: {exStyle.Message}");
                }

                if (fullscreenArt)
                {
                    _rdhPairAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(0, 0, scrW, scrH));
                }
                else
                {
                    int w = Math.Min(1000, scrW - 120);
                    int h = 200;
                    _rdhPairAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                        (scrW - w) / 2, scrH - h - (int)(scrH * 0.12), w, h));
                }

                // Show WITHOUT activating: focus stays with Playnite so the
                // login screen still receives input the moment a pad connects.
                _rdhPairAppWindow.Show(false);
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH pair overlay failed: {ex.Message}");
            }
        }

        private void RdhHidePairOverlay()
        {
            try { DispatcherQueue.TryEnqueue(() => _rdhPairAppWindow?.Hide()); }
            catch { }
        }

        // --- bluetooth auto-pair ------------------------------------------

        private void RdhStartBtAutoPair()
        {
            try
            {
                if (_rdhBtWatcher != null) return;

                // Pairing lives on ASSOCIATION ENDPOINTS, not device interfaces.
                // Using BluetoothDevice.GetDeviceSelectorFromPairingState here
                // returns entries whose Pairing.CanPair is always false, so the
                // CanPair check below rejected everything and nothing ever
                // paired. Verified live: with AssociationEndpoint enumeration,
                // pairable devices correctly report CanPair=True.
                // ProtocolId {e0cbf06c-...} = Bluetooth classic, {bb7bb05e-...} = LE.
                const string aqs =
                    "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
                    "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
                string[] aepProps =
                {
                    "System.Devices.Aep.DeviceAddress",
                    "System.Devices.Aep.IsPaired",
                    "System.Devices.Aep.IsConnected"
                };
                _rdhBtWatcher = DeviceInformation.CreateWatcher(
                    aqs, aepProps, DeviceInformationKind.AssociationEndpoint);

                _rdhBtWatcher.Added += async (w, di) =>
                {
                    try
                    {
                        if (RdhAnyControllerConnected()) return;
                        string name = di.Name ?? string.Empty;
                        bool looksGamepad =
                            name.IndexOf("xbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("controller", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("gamepad", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!looksGamepad) return;
                        if (di.Pairing == null || di.Pairing.IsPaired || !di.Pairing.CanPair)
                        {
                            App.StartupTrace($"RDH pairing: skipping '{name}' (paired={di.Pairing?.IsPaired}, canPair={di.Pairing?.CanPair}).");
                            return;
                        }

                        App.StartupTrace($"RDH pairing: attempting '{name}'...");
                        var custom = di.Pairing.Custom;
                        custom.PairingRequested += (s, a) => a.Accept();
                        var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly);
                        App.StartupTrace($"RDH pairing: '{name}' -> {result.Status}");
                    }
                    catch (Exception ex)
                    {
                        App.StartupTrace($"RDH pairing attempt failed: {ex.Message}");
                    }
                };
                // required handlers even if unused
                _rdhBtWatcher.Updated += (w, u) => { };
                _rdhBtWatcher.Removed += (w, u) => { };
                _rdhBtWatcher.Start();
                App.StartupTrace("RDH pairing: bluetooth watcher started.");
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH pairing watcher failed: {ex.Message}");
            }
        }

        private void RdhStopBtAutoPair()
        {
            try
            {
                _rdhBtWatcher?.Stop();
                _rdhBtWatcher = null;
                App.StartupTrace("RDH pairing: bluetooth watcher stopped.");
            }
            catch { _rdhBtWatcher = null; }
        }
    }
}
