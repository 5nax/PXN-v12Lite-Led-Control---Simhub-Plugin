using GameReaderCommon;
using HidSharp;
using SimHub;
using SimHub.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.PxnV12LiteLed
{
    [PluginName("PXN V12 Lite LED Controller")]
    [PluginDescription("Controls PXN V12 Lite built-in LEDs using SimHub RPM data.")]
    [PluginAuthor("Himal / Raze")]
    public class PxnV12LiteLedPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private const int DefaultVid = 0x11ff;
        private const int DefaultPid = 0x1112;

        private const int PacketSlots = 19;
        private const int PhysicalVisibleLeds = 10;

        private readonly byte[] Header = new byte[] { 0x64, 0x41, 0xe0, 0x02 };

        private struct Rgb
        {
            public byte R;
            public byte G;
            public byte B;

            public Rgb(byte r, byte g, byte b)
            {
                R = r;
                G = g;
                B = b;
            }
        }

        private static readonly Rgb Black = new Rgb(0, 0, 0);
        private static readonly Rgb Green = new Rgb(0, 255, 0);
        private static readonly Rgb Orange = new Rgb(255, 120, 0);
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Blue = new Rgb(0, 0, 255);
        private static readonly Rgb White = new Rgb(255, 255, 255);
        private static readonly Rgb Purple = new Rgb(180, 0, 255);
        private static readonly Rgb Cyan = new Rgb(0, 255, 255);
        private static readonly Rgb Yellow = new Rgb(255, 255, 0);
        private static readonly Rgb Pink = new Rgb(255, 40, 160);

        private readonly List<HidStream> _streams = new List<HidStream>();

        private PluginSettings _settings = PluginSettings.Load();

        private byte[] _lastPacket = null;
        private DateTime _lastSendTime = DateTime.MinValue;

        private double? _smoothedRatio = null;
        private bool _wasLimiterActive = false;
        private string _lastNormalStage = "";

        private int _idleRainbowStep = 0;

        private int _currentRpm = 0;
        private int _currentMaxRpm = 8000;
        private int _currentPercent = 0;
        private string _currentStage = "Idle";
        private int _currentOpenStreams = 0;
        private long _packetWriteCount = 0;
        private string _lastHidScanSummary = "No scan run yet.";

        public PluginManager PluginManager { get; set; }

        public string LeftMenuTitle
        {
            get { return "PXN V12 Lite LED"; }
        }

        public ImageSource PictureIcon
        {
            get { return null; }
        }

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            ConnectWheel();

            SimHub.Logging.Current.Info("[PXN LED] Plugin loaded.");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (!_settings.Enabled)
            {
                return;
            }

            if (_streams.Count == 0)
            {
                ConnectWheel();

                if (_streams.Count == 0)
                {
                    return;
                }
            }

            if (DateTime.Now - _lastSendTime < TimeSpan.FromMilliseconds(_settings.UpdateRateMs))
            {
                return;
            }

            bool gameRunning = false;
            int rpm = 0;
            int maxRpm = _settings.FallbackMaxRpm;

            try
            {
                gameRunning = data.GameRunning && data.NewData != null;

                if (gameRunning)
                {
                    rpm = GetIntFromObject(data.NewData, "Rpms", 0);

                    if (rpm <= 0)
                    {
                        rpm = GetSimHubPropertyInt(pluginManager, "DataCorePlugin.GameData.NewData.Rpms", 0);
                    }

                    if (_settings.UseGameMaxRpm)
                    {
                        int gameMaxRpm = GetIntFromObject(data.NewData, "MaxRpm", 0);

                        if (gameMaxRpm <= 0)
                        {
                            gameMaxRpm = GetSimHubPropertyInt(pluginManager, "DataCorePlugin.GameData.NewData.MaxRpm", 0);
                        }

                        if (gameMaxRpm >= 1000)
                        {
                            maxRpm = gameMaxRpm;
                        }
                    }
                }
            }
            catch
            {
                rpm = 0;
                maxRpm = _settings.FallbackMaxRpm;
            }

            if (maxRpm < 1000)
            {
                maxRpm = _settings.FallbackMaxRpm;
            }

            if (maxRpm < 1000)
            {
                maxRpm = 8000;
            }

            _currentRpm = rpm;
            _currentMaxRpm = maxRpm;

            if (!gameRunning)
            {
                _currentStage = "Idle";

                if (_settings.IdleMode == "Keep last colour")
                {
                    _lastSendTime = DateTime.Now;
                    return;
                }

                SendColours(GetIdleColours(), false);
                _lastSendTime = DateTime.Now;
                return;
            }

            double rawRatio = Clamp01((double)rpm / maxRpm);

            double smoothing = Clamp(_settings.RpmSmoothingPercent, 0, 90) / 100.0;

            if (_smoothedRatio == null)
            {
                _smoothedRatio = rawRatio;
            }
            else
            {
                _smoothedRatio = (_smoothedRatio.Value * smoothing) + (rawRatio * (1.0 - smoothing));
            }

            double displayRatio = Clamp01(_smoothedRatio.Value);

            _currentPercent = (int)Math.Round(rawRatio * 100.0);

            double limiterStart = Clamp01(_settings.LimiterStartPercent / 100.0);
            bool limiterActive = rawRatio >= limiterStart;

            // IMPORTANT: hard limiter override. Normal RPM LEDs do not run underneath limiter flashing.
            if (limiterActive)
            {
                if (!_wasLimiterActive && _settings.ClearBeforeLimiterEntry)
                {
                    SendColours(MakeAllBlack(), true);

                    if (_settings.PreClearDelayMs > 0)
                    {
                        Thread.Sleep(_settings.PreClearDelayMs);
                    }

                    _lastPacket = null;
                }

                bool flashOn = IsFlashOn(_settings.LimiterFlashMs);
                Rgb[] limiterFrame = BuildLimiterOnlyFrame(flashOn);

                _currentStage = flashOn ? "LIMITER ON" : "LIMITER OFF";

                SendColours(limiterFrame, false);

                _wasLimiterActive = true;
                _lastSendTime = DateTime.Now;
                return;
            }

            if (_wasLimiterActive && _settings.ClearAfterLimiterExit)
            {
                SendColours(MakeAllBlack(), true);

                if (_settings.PreClearDelayMs > 0)
                {
                    Thread.Sleep(_settings.PreClearDelayMs);
                }

                _lastPacket = null;
            }

            _wasLimiterActive = false;

            string normalStage;
            Rgb[] rpmFrame = BuildNormalRpmFrame(displayRatio, out normalStage);

            if (_settings.ClearBeforeNormalStageChange &&
                !string.IsNullOrWhiteSpace(_lastNormalStage) &&
                _lastNormalStage != normalStage)
            {
                SendColours(MakeAllBlack(), true);

                if (_settings.PreClearDelayMs > 0)
                {
                    Thread.Sleep(_settings.PreClearDelayMs);
                }

                _lastPacket = null;
            }

            _lastNormalStage = normalStage;
            _currentStage = normalStage;

            SendColours(rpmFrame, false);

            _lastSendTime = DateTime.Now;
        }

        public void End(PluginManager pluginManager)
        {
            try
            {
                if (_settings.ClearOnExit)
                {
                    SendColours(MakeAllBlack(), true);
                    Thread.Sleep(50);
                }
            }
            catch
            {
                // Ignore shutdown errors.
            }

            CloseWheel();

            SimHub.Logging.Current.Info("[PXN LED] Plugin stopped.");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            var root = new StackPanel
            {
                Margin = new Thickness(14)
            };

            AddTitle(root, "PXN V12 Lite LED Controller");
            AddSmallText(root, "Production-style control panel for PXN V12 Lite built-in LEDs.");
            AddSmallText(root, "Limiter is a hard override: when limiter is active, normal RPM LEDs stop completely.");

            AddDivider(root);

            AddTitle(root, "Device setup");
            AddSmallText(root, "Default VID/PID are based on the tested PXN V12 Lite. Other PXN firmware versions may differ.");

            AddTextBox(root, "VID hex", _settings.DeviceVidHex, value =>
            {
                _settings.DeviceVidHex = value;
                _settings.Save();
            });

            AddTextBox(root, "PID hex", _settings.DevicePidHex, value =>
            {
                _settings.DevicePidHex = value;
                _settings.Save();
            });

            var deviceButtons = new UniformGrid
            {
                Columns = 2,
                Margin = new Thickness(0, 8, 0, 0)
            };

            deviceButtons.Children.Add(MakeButton("Scan HID devices", () =>
            {
                _lastHidScanSummary = ScanHidDevices();
                MessageBox.Show(_lastHidScanSummary, "PXN HID Device Scan");
            }));

            deviceButtons.Children.Add(MakeButton("Apply VID/PID + reconnect", () =>
            {
                ConnectWheel();
                SendColours(MakeAllBlack(), true);
            }));

            deviceButtons.Children.Add(MakeButton("Save settings now", () =>
            {
                _settings.Save();
                MessageBox.Show("Settings saved.", "PXN LED");
            }));

            deviceButtons.Children.Add(MakeButton("Open settings folder", () =>
            {
                Process.Start("explorer.exe", PluginSettings.GetSettingsFolder());
            }));

            root.Children.Add(deviceButtons);

            AddDivider(root);
            AddTitle(root, "Main settings");

            AddCheckBox(root, "Enable plugin", _settings.Enabled, value =>
            {
                _settings.Enabled = value;
                _settings.Save();

                if (!value)
                {
                    SendColours(MakeAllBlack(), true);
                }
                else
                {
                    ConnectWheel();
                }
            });

            AddCheckBox(root, "Use game max RPM when available", _settings.UseGameMaxRpm, value =>
            {
                _settings.UseGameMaxRpm = value;
                _settings.Save();
            });

            AddCheckBox(root, "Reverse LED order", _settings.ReverseLedOrder, value =>
            {
                _settings.ReverseLedOrder = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.50), true);
            });

            AddCheckBox(root, "Clear LEDs when SimHub closes", _settings.ClearOnExit, value =>
            {
                _settings.ClearOnExit = value;
                _settings.Save();
            });

            AddSlider(root, "Visible LEDs", 1, 10, 1, _settings.VisibleLedCount, value =>
            {
                _settings.VisibleLedCount = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.50), true);
            });

            AddSlider(root, "Brightness %", 5, 100, 5, _settings.BrightnessPercent, value =>
            {
                _settings.BrightnessPercent = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.50), true);
            });

            AddSlider(root, "USB update rate ms", 25, 200, 5, _settings.UpdateRateMs, value =>
            {
                _settings.UpdateRateMs = value;
                _settings.Save();
            });

            AddSlider(root, "RPM smoothing %", 0, 90, 5, _settings.RpmSmoothingPercent, value =>
            {
                _settings.RpmSmoothingPercent = value;
                _settings.Save();
            });

            AddSlider(root, "Fallback max RPM", 3000, 15000, 250, _settings.FallbackMaxRpm, value =>
            {
                _settings.FallbackMaxRpm = value;
                _settings.Save();
            });

            AddComboBox(root, "HID write target", new[]
            {
                "Interface 1",
                "Interface 0",
                "Interface 2",
                "Interface 3",
                "All interfaces"
            }, _settings.HidTarget, value =>
            {
                _settings.HidTarget = value;
                _settings.Save();
                ConnectWheel();
            });

            AddSmallText(root, "Recommended: Interface 1. Use All interfaces only if test buttons do not work.");

            AddDivider(root);
            AddTitle(root, "RPM behaviour");

            AddComboBox(root, "RPM display mode", new[]
            {
                "Bar zones",
                "Whole bar stage"
            }, _settings.RpmMode, value =>
            {
                _settings.RpmMode = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.75), true);
            });

            AddSlider(root, "Orange starts at RPM %", 20, 90, 5, _settings.OrangeStartPercent, value =>
            {
                _settings.OrangeStartPercent = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.65), true);
            });

            AddSlider(root, "Red starts at RPM %", 40, 98, 5, _settings.RedStartPercent, value =>
            {
                _settings.RedStartPercent = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.85), true);
            });

            AddCheckBox(root, "Clear before normal RPM stage change", _settings.ClearBeforeNormalStageChange, value =>
            {
                _settings.ClearBeforeNormalStageChange = value;
                _settings.Save();
            });

            AddSmallText(root, "Normal stage clear helps if PXN leaves a colour ghost when changing green to orange to red.");

            AddDivider(root);
            AddTitle(root, "Limiter settings");

            AddSlider(root, "Limiter starts at RPM %", 70, 100, 1, _settings.LimiterStartPercent, value =>
            {
                _settings.LimiterStartPercent = value;
                _settings.Save();
                SendColours(BuildRpmPreview(value / 100.0), true);
            });

            AddComboBox(root, "Limiter behaviour", new[]
            {
                "Flash limiter LEDs",
                "Solid limiter LEDs",
                "Flash full bar",
                "Solid full bar",
                "Off"
            }, _settings.LimiterMode, value =>
            {
                _settings.LimiterMode = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddSlider(root, "Limiter LED count", 1, 10, 1, _settings.LimiterLedCount, value =>
            {
                _settings.LimiterLedCount = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddComboBox(root, "Limiter colour", new[]
            {
                "Red",
                "Purple",
                "Blue",
                "White",
                "Orange",
                "Cyan",
                "Pink",
                "Yellow",
                "Custom"
            }, _settings.LimiterColour, value =>
            {
                _settings.LimiterColour = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddSlider(root, "Custom limiter red", 0, 255, 5, _settings.CustomLimiterR, value =>
            {
                _settings.CustomLimiterR = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddSlider(root, "Custom limiter green", 0, 255, 5, _settings.CustomLimiterG, value =>
            {
                _settings.CustomLimiterG = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddSlider(root, "Custom limiter blue", 0, 255, 5, _settings.CustomLimiterB, value =>
            {
                _settings.CustomLimiterB = value;
                _settings.Save();
                SendColours(BuildRpmPreview(0.98), true);
            });

            AddSlider(root, "Limiter flash speed ms", 100, 800, 50, _settings.LimiterFlashMs, value =>
            {
                _settings.LimiterFlashMs = value;
                _settings.Save();
            });

            AddCheckBox(root, "Clear once when entering limiter", _settings.ClearBeforeLimiterEntry, value =>
            {
                _settings.ClearBeforeLimiterEntry = value;
                _settings.Save();
            });

            AddCheckBox(root, "Clear once when exiting limiter", _settings.ClearAfterLimiterExit, value =>
            {
                _settings.ClearAfterLimiterExit = value;
                _settings.Save();
            });

            AddSlider(root, "Clear-frame delay ms", 0, 30, 1, _settings.PreClearDelayMs, value =>
            {
                _settings.PreClearDelayMs = value;
                _settings.Save();
            });

            AddSmallText(root, "Limiter is hard override. Normal RPM LEDs are not sent while limiter is active.");

            AddDivider(root);
            AddTitle(root, "Idle mode");

            AddComboBox(root, "Idle behaviour when no game is running", new[]
            {
                "Off",
                "Dim green",
                "Rainbow idle",
                "Custom individual LEDs",
                "Keep last colour"
            }, _settings.IdleMode, value =>
            {
                _settings.IdleMode = value;
                _settings.Save();
                SendColours(GetIdleColours(), true);
            });

            AddSmallText(root, "Custom individual LEDs only apply when Idle mode is set to Custom individual LEDs.");

            for (int i = 0; i < PhysicalVisibleLeds; i++)
            {
                AddIdleLedRow(root, i);
            }

            AddDivider(root);
            AddTitle(root, "Test buttons");

            var buttonGrid = new UniformGrid
            {
                Columns = 2,
                Margin = new Thickness(0, 8, 0, 0)
            };

            buttonGrid.Children.Add(MakeButton("Reconnect wheel", () => ConnectWheel()));
            buttonGrid.Children.Add(MakeButton("Clear / Off", () => SendColours(MakeAllBlack(), true)));
            buttonGrid.Children.Add(MakeButton("All green", () => SendColours(MakeSolid(Green), true)));
            buttonGrid.Children.Add(MakeButton("All red", () => SendColours(MakeSolid(Red), true)));
            buttonGrid.Children.Add(MakeButton("All purple", () => SendColours(MakeSolid(Purple), true)));
            buttonGrid.Children.Add(MakeButton("Idle preview", () => SendColours(GetIdleColours(), true)));
            buttonGrid.Children.Add(MakeButton("25% RPM", () => SendColours(BuildRpmPreview(0.25), true)));
            buttonGrid.Children.Add(MakeButton("50% RPM", () => SendColours(BuildRpmPreview(0.50), true)));
            buttonGrid.Children.Add(MakeButton("80% RPM", () => SendColours(BuildRpmPreview(0.80), true)));
            buttonGrid.Children.Add(MakeButton("Limiter ON preview", () => SendColours(BuildLimiterOnlyFrame(true), true)));
            buttonGrid.Children.Add(MakeButton("Limiter OFF preview", () => SendColours(BuildLimiterOnlyFrame(false), true)));
            buttonGrid.Children.Add(MakeButton("Clear then limiter", () =>
            {
                SendColours(MakeAllBlack(), true);

                if (_settings.PreClearDelayMs > 0)
                {
                    Thread.Sleep(_settings.PreClearDelayMs);
                }

                SendColours(BuildLimiterOnlyFrame(true), true);
            }));

            buttonGrid.Children.Add(MakeButton("Reset defaults", () =>
            {
                _settings = new PluginSettings();
                _settings.Save();
                MessageBox.Show("Defaults restored. Restart SimHub or reopen this settings page.", "PXN LED");
            }));

            buttonGrid.Children.Add(MakeButton("Save settings", () =>
            {
                _settings.Save();
                MessageBox.Show("Settings saved.", "PXN LED");
            }));

            root.Children.Add(buttonGrid);

            AddDivider(root);
            AddTitle(root, "Live telemetry / diagnostics");

            var diagnosticsText = new TextBlock
            {
                Text = GetDiagnosticsText(),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 4, 0, 8)
            };

            root.Children.Add(diagnosticsText);

            var diagnosticsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            diagnosticsTimer.Tick += (s, e) =>
            {
                diagnosticsText.Text = GetDiagnosticsText();
            };

            diagnosticsTimer.Start();

            AddDivider(root);
            AddSmallText(root, "Performance tip: keep HID target on Interface 1 and update rate around 40 to 70 ms. PXN does not like being spammed.");

            return new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private int GetActiveVid()
        {
            return ParseHex(_settings.DeviceVidHex, DefaultVid);
        }

        private int GetActivePid()
        {
            return ParseHex(_settings.DevicePidHex, DefaultPid);
        }

        private static int ParseHex(string value, int fallback)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return fallback;
                }

                value = value.Trim().Replace("0x", "").Replace("0X", "");
                return int.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private void ConnectWheel()
        {
            CloseWheel();

            try
            {
                int vid = GetActiveVid();
                int pid = GetActivePid();

                var devices = DeviceList.Local.GetHidDevices(vid, pid).ToList();

                if (devices.Count == 0)
                {
                    _currentOpenStreams = 0;
                    SimHub.Logging.Current.Info("[PXN LED] PXN V12 Lite not found for VID/PID 0x" + vid.ToString("X4") + "/0x" + pid.ToString("X4"));
                    return;
                }

                SimHub.Logging.Current.Info("[PXN LED] PXN HID interfaces found: " + devices.Count);

                foreach (var device in devices)
                {
                    try
                    {
                        HidStream stream;

                        if (device.TryOpen(out stream))
                        {
                            stream.WriteTimeout = 30;
                            _streams.Add(stream);
                        }
                    }
                    catch
                    {
                        // Some HID interfaces may not open. That is okay.
                    }
                }

                _currentOpenStreams = _streams.Count;

                SimHub.Logging.Current.Info("[PXN LED] Open streams: " + _streams.Count);

                if (_streams.Count > 0)
                {
                    SendColours(MakeAllBlack(), true);
                }
            }
            catch (Exception ex)
            {
                _currentOpenStreams = 0;
                SimHub.Logging.Current.Error("[PXN LED] Connection failed: " + ex.Message);
            }
        }

        private void CloseWheel()
        {
            foreach (var stream in _streams)
            {
                try
                {
                    stream.Close();
                    stream.Dispose();
                }
                catch
                {
                    // Ignore.
                }
            }

            _streams.Clear();
            _currentOpenStreams = 0;
            _lastPacket = null;
        }

        private string ScanHidDevices()
        {
            var sb = new StringBuilder();

            try
            {
                var devices = DeviceList.Local.GetHidDevices().ToList();

                sb.AppendLine("Likely PXN / matching HID devices:");
                sb.AppendLine();

                int activeVid = GetActiveVid();
                int activePid = GetActivePid();

                int count = 0;

                foreach (var device in devices)
                {
                    string manufacturer = SafeDeviceString(() => device.GetManufacturer());
                    string product = SafeDeviceString(() => device.GetProductName());

                    bool likelyMatch =
                        device.VendorID == activeVid ||
                        device.ProductID == activePid ||
                        manufacturer.ToUpperInvariant().Contains("PXN") ||
                        product.ToUpperInvariant().Contains("PXN") ||
                        product.ToUpperInvariant().Contains("V12");

                    if (!likelyMatch)
                    {
                        continue;
                    }

                    count++;

                    sb.AppendLine("Device " + count);
                    sb.AppendLine("VID: 0x" + device.VendorID.ToString("X4"));
                    sb.AppendLine("PID: 0x" + device.ProductID.ToString("X4"));
                    sb.AppendLine("Manufacturer: " + manufacturer);
                    sb.AppendLine("Product: " + product);
                    sb.AppendLine();
                }

                if (count == 0)
                {
                    sb.AppendLine("No likely PXN devices found.");
                    sb.AppendLine();
                    sb.AppendLine("Current target:");
                    sb.AppendLine("VID: 0x" + activeVid.ToString("X4"));
                    sb.AppendLine("PID: 0x" + activePid.ToString("X4"));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Scan failed:");
                sb.AppendLine(ex.Message);
            }

            return sb.ToString();
        }

        private string SafeDeviceString(Func<string> getter)
        {
            try
            {
                string value = getter();

                if (string.IsNullOrWhiteSpace(value))
                {
                    return "";
                }

                return value;
            }
            catch
            {
                return "";
            }
        }

        private string GetDiagnosticsText()
        {
            return
                "RPM: " + _currentRpm + " / " + _currentMaxRpm + Environment.NewLine +
                "RPM %: " + _currentPercent + "%" + Environment.NewLine +
                "Stage: " + _currentStage + Environment.NewLine +
                "Open HID streams: " + _currentOpenStreams + Environment.NewLine +
                "HID target: " + _settings.HidTarget + Environment.NewLine +
                "VID/PID: 0x" + GetActiveVid().ToString("X4") + " / 0x" + GetActivePid().ToString("X4") + Environment.NewLine +
                "Packets written: " + _packetWriteCount + Environment.NewLine +
                "Settings file: " + PluginSettings.GetSettingsPath();
        }

        private byte[] BuildPacket(Rgb[] visibleColours)
        {
            if (visibleColours.Length != PhysicalVisibleLeds)
            {
                throw new Exception("Expected exactly 10 visible LED colours.");
            }

            byte[] packet = new byte[63];

            packet[0] = Header[0];
            packet[1] = Header[1];
            packet[2] = Header[2];
            packet[3] = Header[3];

            int offset = 4;

            for (int i = 0; i < PacketSlots; i++)
            {
                byte r = 0;
                byte g = 0;
                byte b = 0;

                if (i < PhysicalVisibleLeds)
                {
                    r = visibleColours[i].R;
                    g = visibleColours[i].G;
                    b = visibleColours[i].B;
                }

                packet[offset++] = r;
                packet[offset++] = g;
                packet[offset++] = b;
            }

            packet[61] = 0x00;
            packet[62] = 0x00;

            return packet;
        }

        private void SendColours(Rgb[] physicalColours, bool force)
        {
            if (_streams.Count == 0)
            {
                ConnectWheel();

                if (_streams.Count == 0)
                {
                    return;
                }
            }

            Rgb[] wheelColours = ApplyWheelMappingAndBrightness(physicalColours);
            byte[] packet = BuildPacket(wheelColours);

            if (!force && _lastPacket != null && packet.SequenceEqual(_lastPacket))
            {
                return;
            }

            List<int> targets = GetTargetStreamIndexes();

            if (targets.Count == 0)
            {
                return;
            }

            bool wrote = false;

            foreach (int index in targets)
            {
                if (index < 0 || index >= _streams.Count)
                {
                    continue;
                }

                try
                {
                    _streams[index].Write(packet);
                    wrote = true;
                }
                catch
                {
                    // Ignore individual stream failure.
                }
            }

            if (wrote)
            {
                _lastPacket = packet;
                _packetWriteCount++;
            }
        }

        private List<int> GetTargetStreamIndexes()
        {
            var targets = new List<int>();

            if (_settings.HidTarget == "All interfaces")
            {
                for (int i = 0; i < _streams.Count; i++)
                {
                    targets.Add(i);
                }

                return targets;
            }

            if (_settings.HidTarget.StartsWith("Interface "))
            {
                string numberText = _settings.HidTarget.Replace("Interface ", "").Trim();

                int target;

                if (int.TryParse(numberText, out target))
                {
                    if (target >= 0 && target < _streams.Count)
                    {
                        targets.Add(target);
                    }
                }
            }

            if (targets.Count == 0 && _streams.Count > 0)
            {
                int fallback = Math.Min(1, _streams.Count - 1);
                targets.Add(fallback);
            }

            return targets;
        }

        private Rgb[] ApplyWheelMappingAndBrightness(Rgb[] physicalColours)
        {
            var result = MakeAllBlack();

            double brightness = Clamp(_settings.BrightnessPercent, 0, 100) / 100.0;

            for (int i = 0; i < PhysicalVisibleLeds; i++)
            {
                int targetIndex = _settings.ReverseLedOrder ? PhysicalVisibleLeds - 1 - i : i;

                result[targetIndex] = RGB(
                    (int)(physicalColours[i].R * brightness),
                    (int)(physicalColours[i].G * brightness),
                    (int)(physicalColours[i].B * brightness)
                );
            }

            return result;
        }

        private Rgb[] BuildRpmPreview(double ratio)
        {
            double limiterStart = Clamp01(_settings.LimiterStartPercent / 100.0);

            if (ratio >= limiterStart)
            {
                return BuildLimiterOnlyFrame(true);
            }

            string stage;
            return BuildNormalRpmFrame(ratio, out stage);
        }

        private Rgb[] BuildNormalRpmFrame(double ratio, out string stage)
        {
            ratio = Clamp01(ratio);

            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            int activeLeds = (int)Math.Round(ratio * ledCount);

            var colours = MakeAllBlack();

            double orangeStart = Clamp01(_settings.OrangeStartPercent / 100.0);
            double redStart = Clamp01(_settings.RedStartPercent / 100.0);

            if (redStart <= orangeStart)
            {
                redStart = orangeStart + 0.05;
            }

            if (ratio >= redStart)
            {
                stage = "RED";
            }
            else if (ratio >= orangeStart)
            {
                stage = "ORANGE";
            }
            else
            {
                stage = "GREEN";
            }

            if (_settings.RpmMode == "Whole bar stage")
            {
                Rgb stageColour = Green;

                if (ratio >= redStart)
                {
                    stageColour = Red;
                }
                else if (ratio >= orangeStart)
                {
                    stageColour = Orange;
                }

                for (int i = 0; i < activeLeds; i++)
                {
                    colours[i] = stageColour;
                }

                return colours;
            }

            for (int i = 0; i < activeLeds; i++)
            {
                double position = i / (double)Math.Max(1, ledCount - 1);

                if (position < orangeStart)
                {
                    colours[i] = Green;
                }
                else if (position < redStart)
                {
                    colours[i] = Orange;
                }
                else
                {
                    colours[i] = Red;
                }
            }

            return colours;
        }

        private Rgb[] BuildLimiterOnlyFrame(bool flashOn)
        {
            var colours = MakeAllBlack();

            if (_settings.LimiterMode == "Off")
            {
                return colours;
            }

            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            int limiterLedCount = Clamp(_settings.LimiterLedCount, 1, ledCount);

            Rgb limiterColour = GetLimiterColour();

            if (_settings.LimiterMode == "Flash limiter LEDs" && !flashOn)
            {
                return colours;
            }

            if (_settings.LimiterMode == "Flash full bar" && !flashOn)
            {
                return colours;
            }

            if (_settings.LimiterMode == "Solid full bar" || _settings.LimiterMode == "Flash full bar")
            {
                for (int i = 0; i < ledCount; i++)
                {
                    colours[i] = limiterColour;
                }

                return colours;
            }

            for (int i = ledCount - limiterLedCount; i < ledCount; i++)
            {
                if (i >= 0 && i < PhysicalVisibleLeds)
                {
                    colours[i] = limiterColour;
                }
            }

            return colours;
        }

        private Rgb[] GetIdleColours()
        {
            if (_settings.IdleMode == "Keep last colour")
            {
                return MakeAllBlack();
            }

            if (_settings.IdleMode == "Dim green")
            {
                return MakeSolid(RGB(0, 30, 0));
            }

            if (_settings.IdleMode == "Rainbow idle")
            {
                var colours = MakeAllBlack();

                int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);

                for (int i = 0; i < ledCount; i++)
                {
                    double hue = ((double)i / Math.Max(1, ledCount) + (_idleRainbowStep / 100.0)) % 1.0;
                    colours[i] = HsvToRgb(hue, 1.0, 0.35);
                }

                _idleRainbowStep += 2;
                return colours;
            }

            if (_settings.IdleMode == "Custom individual LEDs")
            {
                var colours = MakeAllBlack();

                for (int i = 0; i < PhysicalVisibleLeds; i++)
                {
                    if (_settings.IdleLedEnabled[i])
                    {
                        colours[i] = GetColourByName(_settings.IdleLedColours[i]);
                    }
                }

                return colours;
            }

            return MakeAllBlack();
        }

        private Rgb GetLimiterColour()
        {
            if (_settings.LimiterColour == "Custom")
            {
                return RGB(_settings.CustomLimiterR, _settings.CustomLimiterG, _settings.CustomLimiterB);
            }

            return GetColourByName(_settings.LimiterColour);
        }

        private bool IsFlashOn(int speedMs)
        {
            speedMs = Clamp(speedMs, 50, 2000);

            long interval = TimeSpan.FromMilliseconds(speedMs).Ticks;

            if (interval <= 0)
            {
                interval = TimeSpan.FromMilliseconds(250).Ticks;
            }

            return ((DateTime.Now.Ticks / interval) % 2) == 0;
        }

        private int GetIntFromObject(object obj, string propertyName, int fallback)
        {
            try
            {
                if (obj == null)
                {
                    return fallback;
                }

                var prop = obj.GetType().GetProperty(propertyName);

                if (prop == null)
                {
                    return fallback;
                }

                object value = prop.GetValue(obj, null);

                if (value == null)
                {
                    return fallback;
                }

                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private int GetSimHubPropertyInt(PluginManager pluginManager, string propertyName, int fallback)
        {
            try
            {
                if (pluginManager == null)
                {
                    return fallback;
                }

                var method = pluginManager.GetType().GetMethod("GetPropertyValue", new[] { typeof(string) });

                if (method == null)
                {
                    return fallback;
                }

                object value = method.Invoke(pluginManager, new object[] { propertyName });

                if (value == null)
                {
                    return fallback;
                }

                return Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private void AddTitle(StackPanel root, string text)
        {
            root.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        private void AddSmallText(StackPanel root, string text)
        {
            root.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }

        private void AddDivider(StackPanel root)
        {
            root.Children.Add(new Border
            {
                Height = 1,
                Background = Brushes.Gray,
                Opacity = 0.35,
                Margin = new Thickness(0, 12, 0, 12)
            });
        }

        private void AddCheckBox(StackPanel root, string label, bool currentValue, Action<bool> onChange)
        {
            var box = new CheckBox
            {
                Content = label,
                IsChecked = currentValue,
                Margin = new Thickness(0, 4, 0, 4)
            };

            box.Checked += (s, e) => onChange(true);
            box.Unchecked += (s, e) => onChange(false);

            root.Children.Add(box);
        }

        private void AddSlider(StackPanel root, string label, int min, int max, int tick, int currentValue, Action<int> onChange)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 8)
            };

            var valueText = new TextBlock
            {
                Text = label + ": " + currentValue,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            };

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = currentValue,
                TickFrequency = tick,
                IsSnapToTickEnabled = true
            };

            slider.ValueChanged += (s, e) =>
            {
                int value = (int)Math.Round(slider.Value);

                if (value < min)
                {
                    value = min;
                }

                if (value > max)
                {
                    value = max;
                }

                valueText.Text = label + ": " + value;
                onChange(value);
            };

            panel.Children.Add(valueText);
            panel.Children.Add(slider);

            root.Children.Add(panel);
        }

        private void AddComboBox(StackPanel root, string label, string[] options, string currentValue, Action<string> onChange)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 8)
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            });

            var combo = new ComboBox();

            foreach (string option in options)
            {
                combo.Items.Add(option);
            }

            int selectedIndex = Array.IndexOf(options, currentValue);

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            combo.SelectedIndex = selectedIndex;

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem != null)
                {
                    onChange(combo.SelectedItem.ToString());
                }
            };

            panel.Children.Add(combo);
            root.Children.Add(panel);
        }

        private void AddTextBox(StackPanel root, string label, string currentValue, Action<string> onChange)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 8)
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 3)
            });

            var box = new TextBox
            {
                Text = currentValue,
                Width = 180,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            box.LostFocus += (s, e) =>
            {
                onChange(box.Text.Trim());
            };

            box.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    onChange(box.Text.Trim());
                }
            };

            panel.Children.Add(box);
            root.Children.Add(panel);
        }

        private void AddIdleLedRow(StackPanel root, int ledIndex)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3)
            };

            var checkbox = new CheckBox
            {
                Content = "Idle LED " + (ledIndex + 1),
                IsChecked = _settings.IdleLedEnabled[ledIndex],
                Width = 110
            };

            var combo = new ComboBox
            {
                Width = 130
            };

            string[] colours =
            {
                "Red",
                "Green",
                "Blue",
                "Purple",
                "Cyan",
                "Pink",
                "Orange",
                "Yellow",
                "White"
            };

            foreach (string colour in colours)
            {
                combo.Items.Add(colour);
            }

            int selectedIndex = Array.IndexOf(colours, _settings.IdleLedColours[ledIndex]);

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            combo.SelectedIndex = selectedIndex;

            checkbox.Checked += (s, e) =>
            {
                _settings.IdleLedEnabled[ledIndex] = true;
                _settings.Save();
                SendColours(GetIdleColours(), true);
            };

            checkbox.Unchecked += (s, e) =>
            {
                _settings.IdleLedEnabled[ledIndex] = false;
                _settings.Save();
                SendColours(GetIdleColours(), true);
            };

            combo.SelectionChanged += (s, e) =>
            {
                if (combo.SelectedItem != null)
                {
                    _settings.IdleLedColours[ledIndex] = combo.SelectedItem.ToString();
                    _settings.Save();
                    SendColours(GetIdleColours(), true);
                }
            };

            row.Children.Add(checkbox);
            row.Children.Add(combo);

            root.Children.Add(row);
        }

        private Button MakeButton(string text, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(4),
                Padding = new Thickness(8, 6, 8, 6)
            };

            button.Click += (s, e) =>
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[PXN LED] Button action failed: " + ex.Message);
                    MessageBox.Show(ex.Message, "PXN LED Error");
                }
            };

            return button;
        }

        private Rgb[] MakeAllBlack()
        {
            return MakeSolid(Black);
        }

        private Rgb[] MakeSolid(Rgb colour)
        {
            var colours = new Rgb[PhysicalVisibleLeds];

            for (int i = 0; i < PhysicalVisibleLeds; i++)
            {
                colours[i] = colour;
            }

            return colours;
        }

        private Rgb GetColourByName(string name)
        {
            if (name == "Red") return Red;
            if (name == "Green") return Green;
            if (name == "Blue") return Blue;
            if (name == "Purple") return Purple;
            if (name == "Cyan") return Cyan;
            if (name == "Pink") return Pink;
            if (name == "Orange") return Orange;
            if (name == "Yellow") return Yellow;
            if (name == "White") return White;

            return Red;
        }

        private static Rgb RGB(int r, int g, int b)
        {
            return new Rgb((byte)Clamp(r), (byte)Clamp(g), (byte)Clamp(b));
        }

        private static Rgb HsvToRgb(double h, double s, double v)
        {
            int i = (int)(h * 6);
            double f = h * 6 - i;
            double p = v * (1 - s);
            double q = v * (1 - f * s);
            double t = v * (1 - (1 - f) * s);

            i = i % 6;

            double r;
            double g;
            double b;

            if (i == 0)
            {
                r = v;
                g = t;
                b = p;
            }
            else if (i == 1)
            {
                r = q;
                g = v;
                b = p;
            }
            else if (i == 2)
            {
                r = p;
                g = v;
                b = t;
            }
            else if (i == 3)
            {
                r = p;
                g = q;
                b = v;
            }
            else if (i == 4)
            {
                r = t;
                g = p;
                b = v;
            }
            else
            {
                r = v;
                g = p;
                b = q;
            }

            return RGB((int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        private static double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private static int Clamp(int value)
        {
            return Clamp(value, 0, 255);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private class PluginSettings
        {
            public string DeviceVidHex = "11FF";
            public string DevicePidHex = "1112";

            public bool Enabled = true;
            public bool UseGameMaxRpm = true;
            public bool ReverseLedOrder = false;
            public bool ClearOnExit = true;

            public int VisibleLedCount = 10;
            public int BrightnessPercent = 85;
            public int UpdateRateMs = 50;
            public int RpmSmoothingPercent = 20;
            public int FallbackMaxRpm = 8000;

            public string HidTarget = "Interface 1";

            public string RpmMode = "Bar zones";
            public int OrangeStartPercent = 55;
            public int RedStartPercent = 80;

            public bool ClearBeforeNormalStageChange = false;

            public int LimiterStartPercent = 95;
            public string LimiterMode = "Flash limiter LEDs";
            public int LimiterLedCount = 2;
            public string LimiterColour = "Purple";
            public int CustomLimiterR = 180;
            public int CustomLimiterG = 0;
            public int CustomLimiterB = 255;
            public int LimiterFlashMs = 250;

            public bool ClearBeforeLimiterEntry = true;
            public bool ClearAfterLimiterExit = true;
            public int PreClearDelayMs = 6;

            public string IdleMode = "Off";

            public bool[] IdleLedEnabled = new bool[10];
            public string[] IdleLedColours = new string[10];

            public PluginSettings()
            {
                for (int i = 0; i < 10; i++)
                {
                    IdleLedEnabled[i] = false;
                    IdleLedColours[i] = "Purple";
                }
            }

            private static string SettingsFolder
            {
                get
                {
                    string folder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "SimHub",
                        "PxnV12LiteLed"
                    );

                    if (!Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    return folder;
                }
            }

            private static string SettingsPath
            {
                get
                {
                    return Path.Combine(SettingsFolder, "settings.ini");
                }
            }

            public static string GetSettingsFolder()
            {
                return SettingsFolder;
            }

            public static string GetSettingsPath()
            {
                return SettingsPath;
            }

            public static PluginSettings Load()
            {
                var settings = new PluginSettings();

                try
                {
                    if (!File.Exists(SettingsPath))
                    {
                        settings.Save();
                        return settings;
                    }

                    string[] lines = File.ReadAllLines(SettingsPath);

                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || !line.Contains("="))
                        {
                            continue;
                        }

                        string[] parts = line.Split(new[] { '=' }, 2);
                        string key = parts[0].Trim();
                        string value = parts[1].Trim();

                        settings.Apply(key, value);
                    }

                    settings.Normalize();
                }
                catch
                {
                    // Use defaults.
                }

                return settings;
            }

            public void Save()
            {
                Normalize();

                try
                {
                    var lines = new List<string>
                    {
                        "DeviceVidHex=" + DeviceVidHex,
                        "DevicePidHex=" + DevicePidHex,
                        "Enabled=" + Enabled,
                        "UseGameMaxRpm=" + UseGameMaxRpm,
                        "ReverseLedOrder=" + ReverseLedOrder,
                        "ClearOnExit=" + ClearOnExit,
                        "VisibleLedCount=" + VisibleLedCount,
                        "BrightnessPercent=" + BrightnessPercent,
                        "UpdateRateMs=" + UpdateRateMs,
                        "RpmSmoothingPercent=" + RpmSmoothingPercent,
                        "FallbackMaxRpm=" + FallbackMaxRpm,
                        "HidTarget=" + HidTarget,
                        "RpmMode=" + RpmMode,
                        "OrangeStartPercent=" + OrangeStartPercent,
                        "RedStartPercent=" + RedStartPercent,
                        "ClearBeforeNormalStageChange=" + ClearBeforeNormalStageChange,
                        "LimiterStartPercent=" + LimiterStartPercent,
                        "LimiterMode=" + LimiterMode,
                        "LimiterLedCount=" + LimiterLedCount,
                        "LimiterColour=" + LimiterColour,
                        "CustomLimiterR=" + CustomLimiterR,
                        "CustomLimiterG=" + CustomLimiterG,
                        "CustomLimiterB=" + CustomLimiterB,
                        "LimiterFlashMs=" + LimiterFlashMs,
                        "ClearBeforeLimiterEntry=" + ClearBeforeLimiterEntry,
                        "ClearAfterLimiterExit=" + ClearAfterLimiterExit,
                        "PreClearDelayMs=" + PreClearDelayMs,
                        "IdleMode=" + IdleMode,
                        "IdleLedEnabled=" + string.Join(",", IdleLedEnabled.Select(x => x ? "1" : "0")),
                        "IdleLedColours=" + string.Join(",", IdleLedColours)
                    };

                    File.WriteAllLines(SettingsPath, lines.ToArray());
                }
                catch
                {
                    // Ignore save errors.
                }
            }

            private void Apply(string key, string value)
            {
                if (key == "DeviceVidHex") DeviceVidHex = value;
                else if (key == "DevicePidHex") DevicePidHex = value;
                else if (key == "Enabled") Enabled = ParseBool(value, Enabled);
                else if (key == "UseGameMaxRpm") UseGameMaxRpm = ParseBool(value, UseGameMaxRpm);
                else if (key == "ReverseLedOrder") ReverseLedOrder = ParseBool(value, ReverseLedOrder);
                else if (key == "ClearOnExit") ClearOnExit = ParseBool(value, ClearOnExit);
                else if (key == "VisibleLedCount") VisibleLedCount = ParseInt(value, VisibleLedCount);
                else if (key == "BrightnessPercent") BrightnessPercent = ParseInt(value, BrightnessPercent);
                else if (key == "UpdateRateMs") UpdateRateMs = ParseInt(value, UpdateRateMs);
                else if (key == "RpmSmoothingPercent") RpmSmoothingPercent = ParseInt(value, RpmSmoothingPercent);
                else if (key == "FallbackMaxRpm") FallbackMaxRpm = ParseInt(value, FallbackMaxRpm);
                else if (key == "HidTarget") HidTarget = value;
                else if (key == "RpmMode") RpmMode = value;
                else if (key == "OrangeStartPercent") OrangeStartPercent = ParseInt(value, OrangeStartPercent);
                else if (key == "RedStartPercent") RedStartPercent = ParseInt(value, RedStartPercent);
                else if (key == "ClearBeforeNormalStageChange") ClearBeforeNormalStageChange = ParseBool(value, ClearBeforeNormalStageChange);
                else if (key == "LimiterStartPercent") LimiterStartPercent = ParseInt(value, LimiterStartPercent);
                else if (key == "LimiterMode") LimiterMode = value;
                else if (key == "LimiterLedCount") LimiterLedCount = ParseInt(value, LimiterLedCount);
                else if (key == "LimiterColour") LimiterColour = value;
                else if (key == "CustomLimiterR") CustomLimiterR = ParseInt(value, CustomLimiterR);
                else if (key == "CustomLimiterG") CustomLimiterG = ParseInt(value, CustomLimiterG);
                else if (key == "CustomLimiterB") CustomLimiterB = ParseInt(value, CustomLimiterB);
                else if (key == "LimiterFlashMs") LimiterFlashMs = ParseInt(value, LimiterFlashMs);
                else if (key == "ClearBeforeLimiterEntry") ClearBeforeLimiterEntry = ParseBool(value, ClearBeforeLimiterEntry);
                else if (key == "ClearAfterLimiterExit") ClearAfterLimiterExit = ParseBool(value, ClearAfterLimiterExit);
                else if (key == "PreClearDelayMs") PreClearDelayMs = ParseInt(value, PreClearDelayMs);
                else if (key == "IdleMode") IdleMode = value;
                else if (key == "IdleLedEnabled") ParseIdleEnabled(value);
                else if (key == "IdleLedColours") ParseIdleColours(value);
            }

            private void Normalize()
            {
                if (string.IsNullOrWhiteSpace(DeviceVidHex)) DeviceVidHex = "11FF";
                if (string.IsNullOrWhiteSpace(DevicePidHex)) DevicePidHex = "1112";

                VisibleLedCount = ClampSetting(VisibleLedCount, 1, 10);
                BrightnessPercent = ClampSetting(BrightnessPercent, 5, 100);
                UpdateRateMs = ClampSetting(UpdateRateMs, 25, 200);
                RpmSmoothingPercent = ClampSetting(RpmSmoothingPercent, 0, 90);
                FallbackMaxRpm = ClampSetting(FallbackMaxRpm, 3000, 15000);

                OrangeStartPercent = ClampSetting(OrangeStartPercent, 20, 90);
                RedStartPercent = ClampSetting(RedStartPercent, 40, 98);

                if (RedStartPercent <= OrangeStartPercent)
                {
                    RedStartPercent = OrangeStartPercent + 5;
                }

                RedStartPercent = ClampSetting(RedStartPercent, 40, 98);
                LimiterStartPercent = ClampSetting(LimiterStartPercent, 70, 100);

                if (LimiterStartPercent <= RedStartPercent)
                {
                    LimiterStartPercent = RedStartPercent + 1;
                }

                LimiterStartPercent = ClampSetting(LimiterStartPercent, 70, 100);
                LimiterLedCount = ClampSetting(LimiterLedCount, 1, 10);
                CustomLimiterR = ClampSetting(CustomLimiterR, 0, 255);
                CustomLimiterG = ClampSetting(CustomLimiterG, 0, 255);
                CustomLimiterB = ClampSetting(CustomLimiterB, 0, 255);
                LimiterFlashMs = ClampSetting(LimiterFlashMs, 100, 800);
                PreClearDelayMs = ClampSetting(PreClearDelayMs, 0, 30);

                if (IdleLedEnabled == null || IdleLedEnabled.Length != 10)
                {
                    IdleLedEnabled = new bool[10];
                }

                if (IdleLedColours == null || IdleLedColours.Length != 10)
                {
                    IdleLedColours = new string[10];

                    for (int i = 0; i < 10; i++)
                    {
                        IdleLedColours[i] = "Purple";
                    }
                }

                for (int i = 0; i < 10; i++)
                {
                    if (string.IsNullOrWhiteSpace(IdleLedColours[i]))
                    {
                        IdleLedColours[i] = "Purple";
                    }
                }
            }

            private void ParseIdleEnabled(string value)
            {
                string[] parts = value.Split(',');

                for (int i = 0; i < Math.Min(10, parts.Length); i++)
                {
                    IdleLedEnabled[i] = parts[i].Trim() == "1";
                }
            }

            private void ParseIdleColours(string value)
            {
                string[] parts = value.Split(',');

                for (int i = 0; i < Math.Min(10, parts.Length); i++)
                {
                    IdleLedColours[i] = parts[i].Trim();
                }
            }

            private static bool ParseBool(string value, bool fallback)
            {
                bool parsed;

                if (bool.TryParse(value, out parsed))
                {
                    return parsed;
                }

                return fallback;
            }

            private static int ParseInt(string value, int fallback)
            {
                int parsed;

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                {
                    return parsed;
                }

                return fallback;
            }

            private static int ClampSetting(int value, int min, int max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }
        }
    }
}
