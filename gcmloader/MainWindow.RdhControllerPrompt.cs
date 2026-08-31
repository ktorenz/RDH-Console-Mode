using System;
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

        private bool RdhAnyControllerConnected()
        {
            try { return _rdhXinput.Any(c => c.IsConnected); }
            catch { return false; }
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

                // Wait for Playnite's window so the prompt lands on the boot
                // video / login screen, never on a black boot.
                for (int i = 0; i < 150; i++)
                {
                    var p = Process.GetProcessesByName("Playnite.FullscreenApp").FirstOrDefault();
                    if (p != null && p.MainWindowHandle != IntPtr.Zero) break;
                    await Task.Delay(400);
                }
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

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
                        App.StartupTrace("RDH controller prompt: no controller, showing overlay.");
                        DispatcherQueue.TryEnqueue(RdhShowPairOverlay);
                        RdhStartBtAutoPair();
                        overlayShown = true;
                    }

                    await Task.Delay(2000);
                }
            }
            catch (Exception ex)
            {
                App.StartupTrace($"RDH controller prompt loop failed: {ex.Message}");
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

                var title = new TextBlock
                {
                    Text = text,
                    FontSize = 34,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                var subtitle = new TextBlock
                {
                    Text = subText,
                    FontSize = 22,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(210, 190, 205, 220)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 14, 0, 0)
                };
                var stack = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                stack.Children.Add(title);
                stack.Children.Add(subtitle);

                var panel = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(232, 6, 10, 16)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(18),
                    Padding = new Thickness(56, 34, 56, 34),
                    Child = stack
                };
                var root = new Grid();
                root.Children.Add(panel);
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

                int sw = GetScreenWidth();
                int sh = GetScreenHeight();
                int w = Math.Min(1000, sw - 120);
                int h = 200;
                _rdhPairAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                    (sw - w) / 2, sh - h - (int)(sh * 0.12), w, h));

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
