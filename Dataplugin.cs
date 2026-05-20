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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace User.PxnV12LiteLed
{
    [PluginName("PXN V12 Lite LED Controller")]
    [PluginDescription("PXN V12 Lite LED controller for RPM, limiter and race flags.")]
    [PluginAuthor("Snax")]
    public class PxnV12LiteLedPlugin : IPlugin, IDataPlugin, IWPFSettings
    {
        private const int DefaultVid = 0x11ff;
        private const int DefaultPid = 0x1112;

        private const int PhysicalVisibleLeds = 10;
        private const int PacketSlots = 19;
        private const int PacketLength = 63;

        private static readonly byte[] PacketHeader = new byte[] { 0x64, 0x41, 0xe0, 0x02 };

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

        private enum VisualPriority
        {
            None = 0,
            Idle = 1,
            Rpm = 2,
            Limiter = 3,
            YellowFlag = 4,
            RedFlag = 5
        }

        private sealed class TelemetrySnapshot
        {
            public bool GameRunning;
            public int Rpm;
            public int MaxRpm;
            public double RawRatio;
            public int Percent;
            public bool YellowFlag;
            public bool RedFlag;
            public string FlagSource = "";
        }

        private static readonly Rgb Black = new Rgb(0, 0, 0);
        private static readonly Rgb Green = new Rgb(0, 255, 0);
        private static readonly Rgb Orange = new Rgb(255, 120, 0);
        private static readonly Rgb Red = new Rgb(255, 0, 0);
        private static readonly Rgb Yellow = new Rgb(255, 220, 0);
        private static readonly Rgb Purple = new Rgb(170, 0, 255);
        private static readonly Rgb Blue = new Rgb(0, 80, 255);
        private static readonly Rgb White = new Rgb(255, 255, 255);
        private static readonly Rgb Cyan = new Rgb(0, 220, 255);
        private static readonly Rgb Pink = new Rgb(255, 45, 165);

        private readonly object _hidLock = new object();
        private readonly List<HidStream> _streams = new List<HidStream>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private PluginSettings _settings = PluginSettings.Load();

        private byte[] _lastPacket = null;
        private long _nextAllowedFrameTicks = 0;
        private long _nextReconnectTicks = 0;

        private bool _forceNextFrame = true;
        private VisualPriority _lastPriority = VisualPriority.None;

        private double? _smoothedRatio = null;
        private bool _limiterLatchActive = false;

        private int _currentRpm = 0;
        private int _currentMaxRpm = 8000;
        private int _currentPercent = 0;
        private string _currentStage = "Idle";
        private string _currentFlagSource = "";
        private int _currentOpenStreams = 0;
        private long _packetWriteCount = 0;
        private long _packetSkipCount = 0;
        private long _writeErrorCount = 0;
        private string _lastHidScanSummary = "No HID scan has been run yet.";

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
            ConnectWheel(true);
            SimHub.Logging.Current.Info("[PXN LED] Controller loaded.");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            if (pluginManager != null)
            {
                PluginManager = pluginManager;
            }

            if (!_settings.Enabled)
            {
                return;
            }

            EnsureConnected();

            TelemetrySnapshot telemetry = ReadTelemetry(pluginManager, ref data);

            _currentRpm = telemetry.Rpm;
            _currentMaxRpm = telemetry.MaxRpm;
            _currentPercent = telemetry.Percent;
            _currentFlagSource = telemetry.FlagSource;

            VisualPriority priority = DecidePriority(telemetry);

            if (!ShouldBuildFrame(priority))
            {
                return;
            }

            if (priority != _lastPriority)
            {
                _lastPacket = null;
                _forceNextFrame = true;

                if (priority == VisualPriority.Rpm && telemetry.RawRatio < 0.05)
                {
                    _smoothedRatio = telemetry.RawRatio;
                }
            }

            Rgb[] frame;
            string stage;

            if (priority == VisualPriority.RedFlag)
            {
                frame = BuildRedFlagFrame();
                stage = "RED FLAG";
            }
            else if (priority == VisualPriority.YellowFlag)
            {
                frame = BuildYellowFlagFrame();
                stage = "YELLOW FLAG";
            }
            else if (priority == VisualPriority.Limiter)
            {
                frame = BuildLimiterFrame();
                stage = "LIMITER";
            }
            else if (priority == VisualPriority.Rpm)
            {
                frame = BuildRpmFrame(telemetry.RawRatio, out stage);
            }
            else
            {
                frame = BuildIdleFrame();
                stage = "Idle";
            }

            _currentStage = stage;

            SendColours(frame, _forceNextFrame);
            _forceNextFrame = false;
            _lastPriority = priority;
        }

        public void End(PluginManager pluginManager)
        {
            try
            {
                if (_settings.ClearOnExit)
                {
                    SendColours(MakeAllBlack(), true);
                }
            }
            catch
            {
                // Do not throw during SimHub shutdown.
            }

            CloseWheel();
            SimHub.Logging.Current.Info("[PXN LED] Controller stopped.");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            PluginManager = pluginManager;

            var root = new StackPanel
            {
                Margin = new Thickness(14)
            };

            AddHeader(root);
            AddQuickActionsSection(root);
            AddDeviceSection(root);
            AddMainSection(root);
            AddRpmSection(root);
            AddAlertSection(root);
            AddIdleSection(root);
            AddTestSection(root);
            AddDiagnosticsSection(root);

            return new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        }

        private void AddHeader(StackPanel root)
        {
            var header = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "PXN V12 Lite LED Controller",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Built by Snax Developer",
                FontWeight = FontWeights.SemiBold,
                Opacity = 0.85,
                Margin = new Thickness(0, 0, 0, 6)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Clean RPM bar, early limiter alert, red/yellow flag LEDs, idle lighting and simple one-click presets.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Priority: Red flag > Yellow flag > Limiter > RPM > Idle. This stops RPM LEDs overlapping the limiter or race flags.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.82,
                Margin = new Thickness(0, 4, 0, 0)
            });

            header.Child = panel;
            root.Children.Add(header);
        }

        private void AddQuickActionsSection(StackPanel root)
        {
            AddTitle(root, "Quick actions");

            var grid = MakeButtonGrid(2);

            grid.Children.Add(MakeButton("Fast racing preset", delegate
            {
                _settings.UpdateRateMs = 16;
                _settings.AlertUpdateRateMs = 12;
                _settings.RpmSmoothingPercent = 2;
                _settings.LimiterStartPercent = 92;
                _settings.LimiterHoldBufferPercent = 3;
                _settings.LimiterLedCount = 4;
                _settings.LimiterMode = "Flash last LEDs";
                SaveAndForce();
                Preview("Fast preset", BuildLimiterFrame(true));
                MessageBox.Show("Fast racing preset applied. Limiter now starts earlier at 92%.", "PXN LED");
            }));

            grid.Children.Add(MakeButton("Stable USB preset", delegate
            {
                _settings.UpdateRateMs = 25;
                _settings.AlertUpdateRateMs = 20;
                _settings.RpmSmoothingPercent = 6;
                _settings.LimiterStartPercent = 93;
                _settings.LimiterHoldBufferPercent = 4;
                _settings.LimiterLedCount = 4;
                _settings.LimiterMode = "Flash last LEDs";
                SaveAndForce();
                Preview("Stable preset", BuildPreviewRpm(0.80));
                MessageBox.Show("Stable USB preset applied. Use this if the LEDs flicker or miss frames.", "PXN LED");
            }));

            grid.Children.Add(MakeButton("Enable flags", delegate
            {
                _settings.EnableGameFlags = true;
                _settings.EnableYellowFlag = true;
                _settings.EnableRedFlag = true;
                SaveAndForce();
                Preview("Flags enabled", BuildYellowFlagFrame());
            }));

            grid.Children.Add(MakeButton("Disable flags", delegate
            {
                _settings.EnableGameFlags = false;
                SaveAndForce();
                Preview("Flags disabled", BuildPreviewRpm(0.60));
            }));

            root.Children.Add(grid);
            AddSmallText(root, "Use Fast racing first. If the wheel misses LED updates, switch to Stable USB.");
            AddDivider(root);
        }

        private void AddDeviceSection(StackPanel root)
        {
            AddTitle(root, "1. Device setup");
            AddSmallText(root, "Default VID/PID are for the tested PXN V12 Lite. Use the scan button if your wheel uses a different firmware ID.");

            AddTextBox(root, "VID hex", _settings.DeviceVidHex, delegate (string value)
            {
                _settings.DeviceVidHex = value;
                SaveAndForce();
            });

            AddTextBox(root, "PID hex", _settings.DevicePidHex, delegate (string value)
            {
                _settings.DevicePidHex = value;
                SaveAndForce();
            });

            AddComboBox(root, "HID write target", new[]
            {
                "Interface 1",
                "Interface 0",
                "Interface 2",
                "Interface 3",
                "All interfaces"
            }, _settings.HidTarget, delegate (string value)
            {
                _settings.HidTarget = value;
                SaveAndForce();
                ConnectWheel(true);
            });

            AddSmallText(root, "Recommended: Interface 1. Use All interfaces only when the test buttons do not light the wheel.");

            var buttons = MakeButtonGrid(2);
            buttons.Children.Add(MakeButton("Scan HID devices", delegate
            {
                _lastHidScanSummary = ScanHidDevices();
                MessageBox.Show(_lastHidScanSummary, "PXN HID Device Scan");
            }));
            buttons.Children.Add(MakeButton("Reconnect wheel", delegate
            {
                ConnectWheel(true);
                SendColours(MakeAllBlack(), true);
            }));
            buttons.Children.Add(MakeButton("Open settings folder", delegate
            {
                Process.Start("explorer.exe", PluginSettings.GetSettingsFolder());
            }));
            buttons.Children.Add(MakeButton("Save settings now", delegate
            {
                _settings.Save();
                MessageBox.Show("Settings saved.", "PXN LED");
            }));
            root.Children.Add(buttons);

            AddDivider(root);
        }

        private void AddMainSection(StackPanel root)
        {
            AddTitle(root, "2. Main settings");

            AddCheckBox(root, "Enable plugin", _settings.Enabled, delegate (bool value)
            {
                _settings.Enabled = value;
                SaveAndForce();

                if (!value)
                {
                    SendColours(MakeAllBlack(), true);
                }
                else
                {
                    ConnectWheel(true);
                }
            });

            AddCheckBox(root, "Use game max RPM when available", _settings.UseGameMaxRpm, delegate (bool value)
            {
                _settings.UseGameMaxRpm = value;
                SaveAndForce();
            });

            AddCheckBox(root, "Reverse LED order", _settings.ReverseLedOrder, delegate (bool value)
            {
                _settings.ReverseLedOrder = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.60));
            });

            AddCheckBox(root, "Clear LEDs when SimHub closes", _settings.ClearOnExit, delegate (bool value)
            {
                _settings.ClearOnExit = value;
                SaveAndForce();
            });

            AddSlider(root, "Visible LEDs", 1, 10, 1, _settings.VisibleLedCount, delegate (int value)
            {
                _settings.VisibleLedCount = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.60));
            });

            AddSlider(root, "Brightness %", 5, 100, 5, _settings.BrightnessPercent, delegate (int value)
            {
                _settings.BrightnessPercent = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.60));
            });

            AddSlider(root, "Normal update rate ms", 8, 80, 1, _settings.UpdateRateMs, delegate (int value)
            {
                _settings.UpdateRateMs = value;
                SaveAndForce();
            });

            AddSlider(root, "Alert update rate ms", 8, 80, 1, _settings.AlertUpdateRateMs, delegate (int value)
            {
                _settings.AlertUpdateRateMs = value;
                SaveAndForce();
            });

            AddSmallText(root, "Tip: 16 to 25 ms feels responsive. If your wheel glitches, raise both update rates slightly.");

            AddDivider(root);
        }

        private void AddRpmSection(StackPanel root)
        {
            AddTitle(root, "3. RPM profile");

            AddSlider(root, "Fallback max RPM", 3000, 15000, 250, _settings.FallbackMaxRpm, delegate (int value)
            {
                _settings.FallbackMaxRpm = value;
                SaveAndForce();
            });

            AddSlider(root, "RPM smoothing %", 0, 60, 1, _settings.RpmSmoothingPercent, delegate (int value)
            {
                _settings.RpmSmoothingPercent = value;
                SaveAndForce();
            });

            AddSlider(root, "First LED turns on at RPM %", 0, 20, 1, _settings.FirstLedAtPercent, delegate (int value)
            {
                _settings.FirstLedAtPercent = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.20));
            });

            AddSlider(root, "Green zone ends at LED bar %", 10, 60, 1, _settings.GreenZoneEndPercent, delegate (int value)
            {
                _settings.GreenZoneEndPercent = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.60));
            });

            AddSlider(root, "Red zone starts at LED bar %", 45, 95, 1, _settings.RedZoneStartPercent, delegate (int value)
            {
                _settings.RedZoneStartPercent = value;
                SaveAndForce();
                Preview("RPM preview", BuildPreviewRpm(0.85));
            });

            AddSmallText(root, "This fixes the old “too much green” issue: green is only the early part of the LED bar, not half the wheel by default.");

            AddDivider(root);
        }

        private void AddAlertSection(StackPanel root)
        {
            AddTitle(root, "4. Limiter and game flags");

            AddCheckBox(root, "Enable game flag LEDs", _settings.EnableGameFlags, delegate (bool value)
            {
                _settings.EnableGameFlags = value;
                SaveAndForce();
            });

            AddCheckBox(root, "Enable yellow flag alert", _settings.EnableYellowFlag, delegate (bool value)
            {
                _settings.EnableYellowFlag = value;
                SaveAndForce();
            });

            AddCheckBox(root, "Enable red flag alert", _settings.EnableRedFlag, delegate (bool value)
            {
                _settings.EnableRedFlag = value;
                SaveAndForce();
            });

            AddSmallText(root, "Flags are optional because some games do not expose them through SimHub. Diagnostics shows the detected flag source when one is found.");

            AddSlider(root, "Limiter starts at RPM %", 80, 100, 1, _settings.LimiterStartPercent, delegate (int value)
            {
                _settings.LimiterStartPercent = value;
                SaveAndForce();
                Preview("Limiter preview", BuildLimiterFrame(true));
            });

            AddSlider(root, "Limiter hold buffer %", 0, 10, 1, _settings.LimiterHoldBufferPercent, delegate (int value)
            {
                _settings.LimiterHoldBufferPercent = value;
                SaveAndForce();
            });

            AddSmallText(root, "Recommended limiter start is 91–93%. Lower it if the game hits the limiter before the LEDs react. Hold buffer keeps it from flickering on/off.");

            AddComboBox(root, "Limiter mode", new[]
            {
                "Flash last LEDs",
                "Flash full bar",
                "Solid last LEDs",
                "Solid full bar",
                "Off"
            }, _settings.LimiterMode, delegate (string value)
            {
                _settings.LimiterMode = value;
                SaveAndForce();
                Preview("Limiter preview", BuildLimiterFrame(true));
            });

            AddSlider(root, "Limiter LED count", 1, 10, 1, _settings.LimiterLedCount, delegate (int value)
            {
                _settings.LimiterLedCount = value;
                SaveAndForce();
                Preview("Limiter preview", BuildLimiterFrame(true));
            });

            AddComboBox(root, "Limiter colour", new[]
            {
                "Purple",
                "Red",
                "Blue",
                "White",
                "Orange",
                "Cyan",
                "Pink",
                "Yellow"
            }, _settings.LimiterColour, delegate (string value)
            {
                _settings.LimiterColour = value;
                SaveAndForce();
                Preview("Limiter preview", BuildLimiterFrame(true));
            });

            AddSlider(root, "Limiter flash speed ms", 50, 500, 10, _settings.LimiterFlashMs, delegate (int value)
            {
                _settings.LimiterFlashMs = value;
                SaveAndForce();
            });

            AddSlider(root, "Flag flash speed ms", 80, 700, 10, _settings.FlagFlashMs, delegate (int value)
            {
                _settings.FlagFlashMs = value;
                SaveAndForce();
            });

            var flagButtons = MakeButtonGrid(2);
            flagButtons.Children.Add(MakeButton("Yellow flag preview", delegate { Preview("Manual: Yellow flag", BuildYellowFlagFrame()); }));
            flagButtons.Children.Add(MakeButton("Red flag preview", delegate { Preview("Manual: Red flag", BuildRedFlagFrame(true)); }));
            flagButtons.Children.Add(MakeButton("Enable both flags", delegate
            {
                _settings.EnableGameFlags = true;
                _settings.EnableYellowFlag = true;
                _settings.EnableRedFlag = true;
                SaveAndForce();
                Preview("Flags enabled", BuildYellowFlagFrame());
            }));
            flagButtons.Children.Add(MakeButton("Disable all flags", delegate
            {
                _settings.EnableGameFlags = false;
                SaveAndForce();
                Preview("Flags disabled", BuildPreviewRpm(0.70));
            }));
            root.Children.Add(flagButtons);

            AddDivider(root);
        }

        private void AddIdleSection(StackPanel root)
        {
            AddTitle(root, "5. Idle lights");

            AddComboBox(root, "Idle mode", new[]
            {
                "Off",
                "Dim green",
                "Dim purple",
                "Soft white",
                "Breathing purple",
                "Chase"
            }, _settings.IdleMode, delegate (string value)
            {
                _settings.IdleMode = value;
                SaveAndForce();
                Preview("Idle preview", BuildIdleFrame());
            });

            AddSlider(root, "Idle brightness %", 1, 35, 1, _settings.IdleBrightnessPercent, delegate (int value)
            {
                _settings.IdleBrightnessPercent = value;
                SaveAndForce();
                Preview("Idle preview", BuildIdleFrame());
            });

            var idleButtons = MakeButtonGrid(2);
            idleButtons.Children.Add(MakeButton("Idle preview", delegate { Preview("Manual: Idle", BuildIdleFrame()); }));
            idleButtons.Children.Add(MakeButton("Idle off", delegate
            {
                _settings.IdleMode = "Off";
                SaveAndForce();
                Preview("Manual: Idle off", MakeAllBlack());
            }));
            root.Children.Add(idleButtons);

            AddSmallText(root, "Idle only runs when SimHub has no active game data. Keep it dim so it does not distract between sessions.");
            AddDivider(root);
        }

        private void AddTestSection(StackPanel root)
        {
            AddTitle(root, "6. Test buttons");

            var grid = MakeButtonGrid(2);

            grid.Children.Add(MakeButton("Clear / Off", delegate { Preview("Manual: Off", MakeAllBlack()); }));
            grid.Children.Add(MakeButton("All green", delegate { Preview("Manual: Green", MakeSolid(Green)); }));
            grid.Children.Add(MakeButton("Idle preview", delegate { Preview("Manual: Idle", BuildIdleFrame()); }));
            grid.Children.Add(MakeButton("25% RPM", delegate { Preview("Manual: 25% RPM", BuildPreviewRpm(0.25)); }));
            grid.Children.Add(MakeButton("50% RPM", delegate { Preview("Manual: 50% RPM", BuildPreviewRpm(0.50)); }));
            grid.Children.Add(MakeButton("80% RPM", delegate { Preview("Manual: 80% RPM", BuildPreviewRpm(0.80)); }));
            grid.Children.Add(MakeButton("Limiter preview", delegate { Preview("Manual: Limiter", BuildLimiterFrame(true)); }));
            grid.Children.Add(MakeButton("Yellow flag preview", delegate { Preview("Manual: Yellow flag", BuildYellowFlagFrame()); }));
            grid.Children.Add(MakeButton("Red flag preview", delegate { Preview("Manual: Red flag", BuildRedFlagFrame(true)); }));
            grid.Children.Add(MakeButton("Reset defaults", delegate
            {
                _settings = new PluginSettings();
                _settings.Save();
                _forceNextFrame = true;
                _lastPacket = null;
                MessageBox.Show("Defaults restored. Reopen this settings page to refresh all slider positions.", "PXN LED");
            }));
            grid.Children.Add(MakeButton("Reconnect + clear", delegate
            {
                ConnectWheel(true);
                Preview("Manual: Reconnected", MakeAllBlack());
            }));

            root.Children.Add(grid);
            AddDivider(root);
        }

        private void AddDiagnosticsSection(StackPanel root)
        {
            AddTitle(root, "7. Live diagnostics");

            var diagnosticsText = new TextBlock
            {
                Text = GetDiagnosticsText(),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 4, 0, 8)
            };

            root.Children.Add(diagnosticsText);

            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };

            timer.Tick += delegate
            {
                diagnosticsText.Text = GetDiagnosticsText();
            };

            timer.Start();

            AddSmallText(root, "If RPM works but flags do not, the game may not expose those flag states through SimHub for that title.");
        }

        private TelemetrySnapshot ReadTelemetry(PluginManager pluginManager, ref GameData data)
        {
            var snapshot = new TelemetrySnapshot();

            object newData = null;

            try
            {
                snapshot.GameRunning = data.GameRunning && data.NewData != null;
                newData = data.NewData;
            }
            catch
            {
                snapshot.GameRunning = false;
            }

            int rpm = 0;
            int maxRpm = _settings.FallbackMaxRpm;

            if (snapshot.GameRunning)
            {
                rpm = ReadInt(newData, pluginManager, new[]
                {
                    "Rpms",
                    "Rpm",
                    "RPM"
                }, new[]
                {
                    "DataCorePlugin.GameData.NewData.Rpms",
                    "DataCorePlugin.GameData.Rpms",
                    "GameData.Rpms"
                }, 0);

                if (_settings.UseGameMaxRpm)
                {
                    int gameMaxRpm = ReadInt(newData, pluginManager, new[]
                    {
                        "MaxRpm",
                        "MaxRPM",
                        "MaxRpms",
                        "CarSettings_MaxRPM"
                    }, new[]
                    {
                        "DataCorePlugin.GameData.NewData.MaxRpm",
                        "DataCorePlugin.GameData.MaxRpm",
                        "GameData.MaxRpm",
                        "DataCorePlugin.GameData.CarSettings_MaxRPM"
                    }, 0);

                    if (gameMaxRpm >= 1000)
                    {
                        maxRpm = gameMaxRpm;
                    }
                }

                if (_settings.EnableGameFlags && _settings.EnableRedFlag)
                {
                    snapshot.RedFlag = ReadFlagState(newData, pluginManager, new[]
                    {
                        "Flag_Red",
                        "FlagRed",
                        "RedFlag",
                        "Flag_ColorRed",
                        "IsRedFlag"
                    }, new[]
                    {
                        "DataCorePlugin.GameData.Flag_Red",
                        "DataCorePlugin.GameData.NewData.Flag_Red",
                        "DataCorePlugin.GameData.NewData.Flag_ColorRed",
                        "GameData.Flag_Red",
                        "Flag_Red"
                    }, new[]
                    {
                        "red flag",
                        "red"
                    }, out snapshot.FlagSource);

                }

                if (_settings.EnableGameFlags && _settings.EnableYellowFlag)
                {
                    string yellowSource;
                    snapshot.YellowFlag = ReadFlagState(newData, pluginManager, new[]
                    {
                        "Flag_Yellow",
                        "FlagYellow",
                        "YellowFlag",
                        "Flag_ColorYellow",
                        "IsYellowFlag",
                        "FullCourseYellow",
                        "Code60"
                    }, new[]
                    {
                        "DataCorePlugin.GameData.Flag_Yellow",
                        "DataCorePlugin.GameData.NewData.Flag_Yellow",
                        "DataCorePlugin.GameData.NewData.Flag_ColorYellow",
                        "GameData.Flag_Yellow",
                        "Flag_Yellow"
                    }, new[]
                    {
                        "yellow flag",
                        "full course yellow",
                        "code 60",
                        "yellow",
                        "fcy"
                    }, out yellowSource);

                    if (string.IsNullOrWhiteSpace(snapshot.FlagSource))
                    {
                        snapshot.FlagSource = yellowSource;
                    }
                }
            }

            maxRpm = Clamp(maxRpm, 1000, 30000);
            rpm = Clamp(rpm, 0, 30000);

            snapshot.Rpm = rpm;
            snapshot.MaxRpm = maxRpm;
            snapshot.RawRatio = maxRpm > 0 ? Clamp01((double)rpm / (double)maxRpm) : 0.0;
            snapshot.Percent = (int)Math.Round(snapshot.RawRatio * 100.0);

            return snapshot;
        }

        private VisualPriority DecidePriority(TelemetrySnapshot telemetry)
        {
            if (!telemetry.GameRunning)
            {
                _limiterLatchActive = false;
                return VisualPriority.Idle;
            }

            if (_settings.EnableGameFlags && _settings.EnableRedFlag && telemetry.RedFlag)
            {
                return VisualPriority.RedFlag;
            }

            if (_settings.EnableGameFlags && _settings.EnableYellowFlag && telemetry.YellowFlag)
            {
                return VisualPriority.YellowFlag;
            }

            if (IsLimiterActive(telemetry.RawRatio))
            {
                return VisualPriority.Limiter;
            }

            return VisualPriority.Rpm;
        }

        private bool IsLimiterActive(double rawRatio)
        {
            if (_settings.LimiterMode == "Off")
            {
                _limiterLatchActive = false;
                return false;
            }

            double enter = Clamp01(_settings.LimiterStartPercent / 100.0);
            double holdBuffer = Clamp(_settings.LimiterHoldBufferPercent, 0, 10) / 100.0;
            double exit = Clamp01(enter - holdBuffer);

            if (_limiterLatchActive)
            {
                if (rawRatio <= exit)
                {
                    _limiterLatchActive = false;
                }
            }
            else if (rawRatio >= enter)
            {
                _limiterLatchActive = true;
            }

            return _limiterLatchActive;
        }

        private bool ShouldBuildFrame(VisualPriority priority)
        {
            long now = _clock.ElapsedTicks;

            if (_forceNextFrame || priority != _lastPriority)
            {
                _nextAllowedFrameTicks = now + MsToTicks(GetUpdateRateForPriority(priority));
                return true;
            }

            if (now < _nextAllowedFrameTicks)
            {
                return false;
            }

            _nextAllowedFrameTicks = now + MsToTicks(GetUpdateRateForPriority(priority));
            return true;
        }

        private int GetUpdateRateForPriority(VisualPriority priority)
        {
            if (priority == VisualPriority.RedFlag ||
                priority == VisualPriority.YellowFlag ||
                priority == VisualPriority.Limiter)
            {
                return Math.Min(_settings.AlertUpdateRateMs, _settings.UpdateRateMs);
            }

            if (priority == VisualPriority.Idle)
            {
                if (_settings.IdleMode == "Breathing purple" || _settings.IdleMode == "Chase")
                {
                    return Math.Max(35, _settings.UpdateRateMs);
                }

                return Math.Max(100, _settings.UpdateRateMs);
            }

            return _settings.UpdateRateMs;
        }

        private long MsToTicks(int ms)
        {
            ms = Clamp(ms, 1, 1000);
            return (long)((double)Stopwatch.Frequency * ((double)ms / 1000.0));
        }

        private Rgb[] BuildRpmFrame(double rawRatio, out string stage)
        {
            double ratio = ApplySmoothing(rawRatio);
            ratio = Clamp01(ratio);

            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            int activeLeds = GetActiveLedCount(ratio, ledCount);

            var colours = MakeAllBlack();

            double greenEnd = _settings.GreenZoneEndPercent / 100.0;
            double redStart = _settings.RedZoneStartPercent / 100.0;

            if (redStart <= greenEnd)
            {
                redStart = Math.Min(0.95, greenEnd + 0.10);
            }

            if (ratio >= redStart)
            {
                stage = "RPM RED";
            }
            else if (ratio >= greenEnd)
            {
                stage = "RPM ORANGE";
            }
            else
            {
                stage = "RPM GREEN";
            }

            for (int i = 0; i < activeLeds; i++)
            {
                double ledPosition = (i + 1) / (double)ledCount;

                if (ledPosition <= greenEnd)
                {
                    colours[i] = Green;
                }
                else if (ledPosition < redStart)
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

        private double ApplySmoothing(double rawRatio)
        {
            double smoothing = Clamp(_settings.RpmSmoothingPercent, 0, 60) / 100.0;

            if (_smoothedRatio == null || smoothing <= 0.0 || rawRatio < 0.03)
            {
                _smoothedRatio = rawRatio;
                return rawRatio;
            }

            _smoothedRatio = (_smoothedRatio.Value * smoothing) + (rawRatio * (1.0 - smoothing));
            return _smoothedRatio.Value;
        }

        private int GetActiveLedCount(double ratio, int ledCount)
        {
            double firstLedAt = Clamp(_settings.FirstLedAtPercent, 0, 20) / 100.0;

            if (ratio <= firstLedAt)
            {
                return 0;
            }

            double normalized = (ratio - firstLedAt) / Math.Max(0.01, 1.0 - firstLedAt);
            int active = (int)Math.Ceiling(normalized * ledCount);

            return Clamp(active, 0, ledCount);
        }

        private Rgb[] BuildLimiterFrame(bool forceOn = false)
        {
            var colours = MakeAllBlack();

            if (_settings.LimiterMode == "Off")
            {
                return colours;
            }

            bool flashMode =
                _settings.LimiterMode == "Flash last LEDs" ||
                _settings.LimiterMode == "Flash full bar";

            if (!forceOn && flashMode && !BlinkOn(_settings.LimiterFlashMs))
            {
                return colours;
            }

            bool fullBar =
                _settings.LimiterMode == "Flash full bar" ||
                _settings.LimiterMode == "Solid full bar";

            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            int limiterLedCount = fullBar ? ledCount : Clamp(_settings.LimiterLedCount, 1, ledCount);

            Rgb colour = GetColourByName(_settings.LimiterColour, Purple);

            if (fullBar)
            {
                for (int i = 0; i < ledCount; i++)
                {
                    colours[i] = colour;
                }

                return colours;
            }

            for (int i = ledCount - limiterLedCount; i < ledCount; i++)
            {
                if (i >= 0 && i < PhysicalVisibleLeds)
                {
                    colours[i] = colour;
                }
            }

            return colours;
        }

        private Rgb[] BuildYellowFlagFrame()
        {
            var colours = MakeAllBlack();
            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            bool phase = BlinkOn(_settings.FlagFlashMs);

            for (int i = 0; i < ledCount; i++)
            {
                bool leftHalf = i < (ledCount / 2);

                if ((phase && leftHalf) || (!phase && !leftHalf))
                {
                    colours[i] = Yellow;
                }
                else
                {
                    colours[i] = RGB(90, 75, 0);
                }
            }

            return colours;
        }

        private Rgb[] BuildRedFlagFrame(bool forceOn = false)
        {
            if (!forceOn && !BlinkOn(_settings.FlagFlashMs))
            {
                return MakeAllBlack();
            }

            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            var colours = MakeAllBlack();

            for (int i = 0; i < ledCount; i++)
            {
                colours[i] = Red;
            }

            return colours;
        }

        private Rgb[] BuildIdleFrame()
        {
            int ledCount = Clamp(_settings.VisibleLedCount, 1, PhysicalVisibleLeds);
            int brightness = Clamp(_settings.IdleBrightnessPercent, 1, 35);
            var colours = MakeAllBlack();

            if (_settings.IdleMode == "Off")
            {
                return colours;
            }

            if (_settings.IdleMode == "Dim green")
            {
                FillVisible(colours, ledCount, Dim(Green, brightness));
                return colours;
            }

            if (_settings.IdleMode == "Dim purple")
            {
                FillVisible(colours, ledCount, Dim(Purple, brightness));
                return colours;
            }

            if (_settings.IdleMode == "Soft white")
            {
                FillVisible(colours, ledCount, Dim(White, Math.Max(1, brightness / 2)));
                return colours;
            }

            if (_settings.IdleMode == "Breathing purple")
            {
                double wave = (Math.Sin(_clock.ElapsedMilliseconds / 360.0) + 1.0) / 2.0;
                int level = Clamp((int)Math.Round(brightness * (0.25 + (wave * 0.75))), 1, 35);
                FillVisible(colours, ledCount, Dim(Purple, level));
                return colours;
            }

            if (_settings.IdleMode == "Chase")
            {
                int index = (int)((_clock.ElapsedMilliseconds / 115) % Math.Max(1, ledCount));
                colours[index] = Dim(Purple, brightness);

                int trail = index - 1;

                if (trail < 0)
                {
                    trail = ledCount - 1;
                }

                colours[trail] = Dim(Blue, Math.Max(1, brightness / 2));
                return colours;
            }

            return colours;
        }

        private void FillVisible(Rgb[] colours, int ledCount, Rgb colour)
        {
            for (int i = 0; i < ledCount && i < colours.Length; i++)
            {
                colours[i] = colour;
            }
        }

        private Rgb Dim(Rgb colour, int percent)
        {
            percent = Clamp(percent, 0, 100);
            double factor = percent / 100.0;

            return RGB(
                (int)Math.Round(colour.R * factor),
                (int)Math.Round(colour.G * factor),
                (int)Math.Round(colour.B * factor)
            );
        }

        private Rgb[] BuildPreviewRpm(double ratio)
        {
            string unused;
            return BuildRpmFrame(ratio, out unused);
        }

        private bool BlinkOn(int speedMs)
        {
            speedMs = Clamp(speedMs, 40, 2000);
            long interval = MsToTicks(speedMs);

            if (interval <= 0)
            {
                interval = MsToTicks(200);
            }

            return ((_clock.ElapsedTicks / interval) % 2) == 0;
        }

        private void Preview(string stage, Rgb[] colours)
        {
            _currentStage = stage;
            _lastPacket = null;
            _forceNextFrame = true;
            _lastPriority = VisualPriority.None;
            SendColours(colours, true);
        }

        private void SaveAndForce()
        {
            _settings.Save();
            _settings.Normalize();
            _lastPacket = null;
            _forceNextFrame = true;
        }

        private void EnsureConnected()
        {
            if (_streams.Count > 0)
            {
                return;
            }

            long now = _clock.ElapsedTicks;

            if (now < _nextReconnectTicks)
            {
                return;
            }

            _nextReconnectTicks = now + MsToTicks(1200);
            ConnectWheel(false);
        }

        private void ConnectWheel(bool logNotFound)
        {
            lock (_hidLock)
            {
                CloseWheel();

                try
                {
                    int vid = GetActiveVid();
                    int pid = GetActivePid();

                    List<HidDevice> devices = DeviceList.Local.GetHidDevices(vid, pid).ToList();

                    if (devices.Count == 0)
                    {
                        _currentOpenStreams = 0;

                        if (logNotFound)
                        {
                            SimHub.Logging.Current.Info("[PXN LED] No HID device found for VID/PID 0x" + vid.ToString("X4") + "/0x" + pid.ToString("X4"));
                        }

                        return;
                    }

                    foreach (HidDevice device in devices)
                    {
                        try
                        {
                            HidStream stream;

                            if (device.TryOpen(out stream))
                            {
                                stream.WriteTimeout = 20;
                                _streams.Add(stream);
                            }
                        }
                        catch
                        {
                            // Some HID interfaces are not writable. Ignore and try the rest.
                        }
                    }

                    _currentOpenStreams = _streams.Count;
                    _lastPacket = null;
                    _forceNextFrame = true;

                    SimHub.Logging.Current.Info("[PXN LED] Connected. HID interfaces opened: " + _streams.Count);
                }
                catch (Exception ex)
                {
                    _currentOpenStreams = 0;
                    SimHub.Logging.Current.Error("[PXN LED] Connection failed: " + ex.Message);
                }
            }
        }

        private void CloseWheel()
        {
            foreach (HidStream stream in _streams)
            {
                try
                {
                    stream.Close();
                    stream.Dispose();
                }
                catch
                {
                    // Ignore close errors.
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
                int activeVid = GetActiveVid();
                int activePid = GetActivePid();

                List<HidDevice> devices = DeviceList.Local.GetHidDevices().ToList();
                int count = 0;

                sb.AppendLine("PXN / V12 / matching HID devices");
                sb.AppendLine("--------------------------------");
                sb.AppendLine("Current target VID/PID: 0x" + activeVid.ToString("X4") + " / 0x" + activePid.ToString("X4"));
                sb.AppendLine();

                foreach (HidDevice device in devices)
                {
                    string manufacturer = SafeDeviceString(delegate { return device.GetManufacturer(); });
                    string product = SafeDeviceString(delegate { return device.GetProductName(); });

                    bool likely =
                        device.VendorID == activeVid ||
                        device.ProductID == activePid ||
                        ContainsIgnoreCase(manufacturer, "PXN") ||
                        ContainsIgnoreCase(product, "PXN") ||
                        ContainsIgnoreCase(product, "V12");

                    if (!likely)
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
                    sb.AppendLine("Try reconnecting the wheel, then press Scan again.");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Scan failed:");
                sb.AppendLine(ex.Message);
            }

            return sb.ToString();
        }

        private void SendColours(Rgb[] physicalColours, bool force)
        {
            if (physicalColours == null || physicalColours.Length != PhysicalVisibleLeds)
            {
                return;
            }

            EnsureConnected();

            if (_streams.Count == 0)
            {
                return;
            }

            Rgb[] mappedColours = ApplyMappingAndBrightness(physicalColours);
            byte[] packet = BuildPacket(mappedColours);

            if (!force && _lastPacket != null && packet.SequenceEqual(_lastPacket))
            {
                _packetSkipCount++;
                return;
            }

            List<int> targets = GetTargetStreamIndexes();

            if (targets.Count == 0)
            {
                return;
            }

            bool wroteAny = false;
            int failedWrites = 0;

            lock (_hidLock)
            {
                foreach (int index in targets)
                {
                    if (index < 0 || index >= _streams.Count)
                    {
                        continue;
                    }

                    try
                    {
                        _streams[index].Write(packet);
                        wroteAny = true;
                    }
                    catch
                    {
                        failedWrites++;
                        _writeErrorCount++;
                    }
                }
            }

            if (wroteAny)
            {
                _lastPacket = packet;
                _packetWriteCount++;
            }
            else if (failedWrites > 0)
            {
                CloseWheel();
            }
        }

        private byte[] BuildPacket(Rgb[] visibleColours)
        {
            byte[] packet = new byte[PacketLength];

            packet[0] = PacketHeader[0];
            packet[1] = PacketHeader[1];
            packet[2] = PacketHeader[2];
            packet[3] = PacketHeader[3];

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

        private Rgb[] ApplyMappingAndBrightness(Rgb[] physicalColours)
        {
            var result = MakeAllBlack();
            double brightness = Clamp(_settings.BrightnessPercent, 0, 100) / 100.0;

            for (int i = 0; i < PhysicalVisibleLeds; i++)
            {
                int target = _settings.ReverseLedOrder ? PhysicalVisibleLeds - 1 - i : i;

                result[target] = RGB(
                    (int)Math.Round(physicalColours[i].R * brightness),
                    (int)Math.Round(physicalColours[i].G * brightness),
                    (int)Math.Round(physicalColours[i].B * brightness)
                );
            }

            return result;
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

            if (!string.IsNullOrWhiteSpace(_settings.HidTarget) && _settings.HidTarget.StartsWith("Interface "))
            {
                string numberText = _settings.HidTarget.Replace("Interface ", "").Trim();

                int index;

                if (int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                {
                    if (index >= 0 && index < _streams.Count)
                    {
                        targets.Add(index);
                    }
                }
            }

            if (targets.Count == 0 && _streams.Count > 0)
            {
                targets.Add(Math.Min(1, _streams.Count - 1));
            }

            return targets;
        }

        private int ReadInt(object dataObject, PluginManager pluginManager, string[] objectProperties, string[] simHubProperties, int fallback)
        {
            object value;

            foreach (string name in objectProperties)
            {
                if (TryReadObjectProperty(dataObject, name, out value))
                {
                    int parsed;

                    if (TryConvertInt(value, out parsed))
                    {
                        return parsed;
                    }
                }
            }

            foreach (string property in simHubProperties)
            {
                if (TryReadSimHubProperty(pluginManager, property, out value))
                {
                    int parsed;

                    if (TryConvertInt(value, out parsed))
                    {
                        return parsed;
                    }
                }
            }

            return fallback;
        }

        private bool ReadFlagState(
            object dataObject,
            PluginManager pluginManager,
            string[] objectProperties,
            string[] simHubProperties,
            string[] textTerms,
            out string source)
        {
            source = "";
            object value;

            foreach (string name in objectProperties)
            {
                if (TryReadObjectProperty(dataObject, name, out value))
                {
                    if (LooksLikeActiveFlag(value, textTerms))
                    {
                        source = "NewData." + name;
                        return true;
                    }
                }
            }

            foreach (string property in simHubProperties)
            {
                if (TryReadSimHubProperty(pluginManager, property, out value))
                {
                    if (LooksLikeActiveFlag(value, textTerms))
                    {
                        source = property;
                        return true;
                    }
                }
            }

            string[] genericObjectNames =
            {
                "Flag",
                "Flags",
                "FlagName",
                "Flag_Name",
                "FlagColor",
                "Flag_Color",
                "SessionFlag",
                "CurrentFlag"
            };

            foreach (string name in genericObjectNames)
            {
                if (TryReadObjectProperty(dataObject, name, out value))
                {
                    if (LooksLikeActiveFlag(value, textTerms))
                    {
                        source = "NewData." + name;
                        return true;
                    }
                }
            }

            string[] genericPaths =
            {
                "DataCorePlugin.GameData.Flag_Name",
                "DataCorePlugin.GameData.NewData.Flag_Name",
                "DataCorePlugin.GameData.Flag",
                "DataCorePlugin.GameData.NewData.Flag",
                "GameData.Flag_Name",
                "GameData.Flag"
            };

            foreach (string property in genericPaths)
            {
                if (TryReadSimHubProperty(pluginManager, property, out value))
                {
                    if (LooksLikeActiveFlag(value, textTerms))
                    {
                        source = property;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryReadObjectProperty(object obj, string propertyName, out object value)
        {
            value = null;

            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(propertyName))
                {
                    return false;
                }

                var prop = obj.GetType().GetProperty(propertyName);

                if (prop == null)
                {
                    return false;
                }

                value = prop.GetValue(obj, null);
                return value != null;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private bool TryReadSimHubProperty(PluginManager pluginManager, string propertyName, out object value)
        {
            value = null;

            try
            {
                if (pluginManager == null || string.IsNullOrWhiteSpace(propertyName))
                {
                    return false;
                }

                var method = pluginManager.GetType().GetMethod("GetPropertyValue", new[] { typeof(string) });

                if (method == null)
                {
                    return false;
                }

                value = method.Invoke(pluginManager, new object[] { propertyName });
                return value != null;
            }
            catch
            {
                value = null;
                return false;
            }
        }

        private bool TryConvertInt(object value, out int result)
        {
            result = 0;

            try
            {
                if (value == null)
                {
                    return false;
                }

                if (value is int)
                {
                    result = (int)value;
                    return true;
                }

                if (value is double)
                {
                    result = (int)Math.Round((double)value);
                    return true;
                }

                if (value is float)
                {
                    result = (int)Math.Round((float)value);
                    return true;
                }

                if (value is decimal)
                {
                    result = (int)Math.Round((decimal)value);
                    return true;
                }

                string text = Convert.ToString(value, CultureInfo.InvariantCulture);

                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }
            catch
            {
                result = 0;
                return false;
            }
        }

        private bool LooksLikeActiveFlag(object value, string[] textTerms)
        {
            if (value == null)
            {
                return false;
            }

            if (value is bool)
            {
                return (bool)value;
            }

            if (value is int)
            {
                return (int)value > 0;
            }

            if (value is double)
            {
                return (double)value > 0.5;
            }

            if (value is float)
            {
                return (float)value > 0.5f;
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture);

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim();

            if (text == "1" ||
                text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string lowered = text.ToLowerInvariant();

            for (int i = 0; i < textTerms.Length; i++)
            {
                if (lowered.Contains(textTerms[i].ToLowerInvariant()))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetDiagnosticsText()
        {
            return
                "RPM: " + _currentRpm + " / " + _currentMaxRpm + Environment.NewLine +
                "RPM %: " + _currentPercent + "%" + Environment.NewLine +
                "Stage: " + _currentStage + Environment.NewLine +
                "Limiter trigger: " + _settings.LimiterStartPercent + "% / buffer " + _settings.LimiterHoldBufferPercent + "%" + Environment.NewLine +
                "Flags enabled: " + (_settings.EnableGameFlags ? "yes" : "no") + " | yellow " + (_settings.EnableYellowFlag ? "on" : "off") + " | red " + (_settings.EnableRedFlag ? "on" : "off") + Environment.NewLine +
                "Flag source: " + (string.IsNullOrWhiteSpace(_currentFlagSource) ? "none / not detected" : _currentFlagSource) + Environment.NewLine +
                "Open HID streams: " + _currentOpenStreams + Environment.NewLine +
                "HID target: " + _settings.HidTarget + Environment.NewLine +
                "VID/PID: 0x" + GetActiveVid().ToString("X4") + " / 0x" + GetActivePid().ToString("X4") + Environment.NewLine +
                "Packets written: " + _packetWriteCount + Environment.NewLine +
                "Duplicate packets skipped: " + _packetSkipCount + Environment.NewLine +
                "Write errors: " + _writeErrorCount + Environment.NewLine +
                "Settings: " + PluginSettings.GetSettingsPath();
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

        private static string SafeDeviceString(Func<string> getter)
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

        private static bool ContainsIgnoreCase(string value, string search)
        {
            if (value == null || search == null)
            {
                return false;
            }

            return value.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
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

        private Rgb GetColourByName(string name, Rgb fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return fallback;
            }

            if (name.Equals("Green", StringComparison.OrdinalIgnoreCase)) return Green;
            if (name.Equals("Orange", StringComparison.OrdinalIgnoreCase)) return Orange;
            if (name.Equals("Red", StringComparison.OrdinalIgnoreCase)) return Red;
            if (name.Equals("Yellow", StringComparison.OrdinalIgnoreCase)) return Yellow;
            if (name.Equals("Purple", StringComparison.OrdinalIgnoreCase)) return Purple;
            if (name.Equals("Blue", StringComparison.OrdinalIgnoreCase)) return Blue;
            if (name.Equals("White", StringComparison.OrdinalIgnoreCase)) return White;
            if (name.Equals("Cyan", StringComparison.OrdinalIgnoreCase)) return Cyan;
            if (name.Equals("Pink", StringComparison.OrdinalIgnoreCase)) return Pink;

            return fallback;
        }

        private static Rgb RGB(int r, int g, int b)
        {
            return new Rgb((byte)Clamp(r, 0, 255), (byte)Clamp(g, 0, 255), (byte)Clamp(b, 0, 255));
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private UniformGrid MakeButtonGrid(int columns)
        {
            return new UniformGrid
            {
                Columns = columns,
                Margin = new Thickness(0, 8, 0, 0)
            };
        }

        private Button MakeButton(string text, Action onClick)
        {
            var button = new Button
            {
                Content = text,
                Margin = new Thickness(4),
                Padding = new Thickness(10, 8, 10, 8),
                MinHeight = 34
            };

            button.Click += delegate
            {
                try
                {
                    onClick();
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Error("[PXN LED] Button failed: " + ex.Message);
                    MessageBox.Show(ex.Message, "PXN LED Error");
                }
            };

            return button;
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

            box.Checked += delegate { onChange(true); };
            box.Unchecked += delegate { onChange(false); };

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
                Value = Clamp(currentValue, min, max),
                TickFrequency = tick,
                IsSnapToTickEnabled = true
            };

            slider.ValueChanged += delegate
            {
                int value = (int)Math.Round(slider.Value);

                value = Clamp(value, min, max);
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

            for (int i = 0; i < options.Length; i++)
            {
                combo.Items.Add(options[i]);
            }

            int selectedIndex = Array.IndexOf(options, currentValue);

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            combo.SelectedIndex = selectedIndex;

            combo.SelectionChanged += delegate
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

            box.LostFocus += delegate
            {
                onChange(box.Text.Trim());
            };

            box.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    onChange(box.Text.Trim());
                }
            };

            panel.Children.Add(box);
            root.Children.Add(panel);
        }

        private sealed class PluginSettings
        {
            public string DeviceVidHex = "11FF";
            public string DevicePidHex = "1112";

            public bool Enabled = true;
            public bool UseGameMaxRpm = true;
            public bool EnableGameFlags = true;
            public bool EnableYellowFlag = true;
            public bool EnableRedFlag = true;
            public bool ReverseLedOrder = false;
            public bool ClearOnExit = true;

            public int VisibleLedCount = 10;
            public int BrightnessPercent = 85;
            public int UpdateRateMs = 20;
            public int AlertUpdateRateMs = 16;
            public int RpmSmoothingPercent = 5;
            public int FallbackMaxRpm = 8000;
            public int FirstLedAtPercent = 8;
            public int GreenZoneEndPercent = 35;
            public int RedZoneStartPercent = 72;

            public string HidTarget = "Interface 1";

            public int LimiterStartPercent = 92;
            public int LimiterHoldBufferPercent = 3;
            public string LimiterMode = "Flash last LEDs";
            public int LimiterLedCount = 4;
            public string LimiterColour = "Purple";
            public int LimiterFlashMs = 120;
            public int FlagFlashMs = 180;

            public string IdleMode = "Off";
            public int IdleBrightnessPercent = 12;

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
                get { return Path.Combine(SettingsFolder, "settings.ini"); }
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

                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];

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
                    // Defaults are safer than throwing inside SimHub.
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
                        "EnableGameFlags=" + EnableGameFlags,
                        "EnableYellowFlag=" + EnableYellowFlag,
                        "EnableRedFlag=" + EnableRedFlag,
                        "ReverseLedOrder=" + ReverseLedOrder,
                        "ClearOnExit=" + ClearOnExit,
                        "VisibleLedCount=" + VisibleLedCount,
                        "BrightnessPercent=" + BrightnessPercent,
                        "UpdateRateMs=" + UpdateRateMs,
                        "AlertUpdateRateMs=" + AlertUpdateRateMs,
                        "RpmSmoothingPercent=" + RpmSmoothingPercent,
                        "FallbackMaxRpm=" + FallbackMaxRpm,
                        "FirstLedAtPercent=" + FirstLedAtPercent,
                        "GreenZoneEndPercent=" + GreenZoneEndPercent,
                        "RedZoneStartPercent=" + RedZoneStartPercent,
                        "HidTarget=" + HidTarget,
                        "LimiterStartPercent=" + LimiterStartPercent,
                        "LimiterHoldBufferPercent=" + LimiterHoldBufferPercent,
                        "LimiterMode=" + LimiterMode,
                        "LimiterLedCount=" + LimiterLedCount,
                        "LimiterColour=" + LimiterColour,
                        "LimiterFlashMs=" + LimiterFlashMs,
                        "FlagFlashMs=" + FlagFlashMs,
                        "IdleMode=" + IdleMode,
                        "IdleBrightnessPercent=" + IdleBrightnessPercent
                    };

                    File.WriteAllLines(SettingsPath, lines.ToArray());
                }
                catch
                {
                    // Ignore settings write errors.
                }
            }

            private void Apply(string key, string value)
            {
                if (key == "DeviceVidHex") DeviceVidHex = value;
                else if (key == "DevicePidHex") DevicePidHex = value;
                else if (key == "Enabled") Enabled = ParseBool(value, Enabled);
                else if (key == "UseGameMaxRpm") UseGameMaxRpm = ParseBool(value, UseGameMaxRpm);
                else if (key == "EnableGameFlags") EnableGameFlags = ParseBool(value, EnableGameFlags);
                else if (key == "EnableYellowFlag") EnableYellowFlag = ParseBool(value, EnableYellowFlag);
                else if (key == "EnableRedFlag") EnableRedFlag = ParseBool(value, EnableRedFlag);
                else if (key == "ReverseLedOrder") ReverseLedOrder = ParseBool(value, ReverseLedOrder);
                else if (key == "ClearOnExit") ClearOnExit = ParseBool(value, ClearOnExit);
                else if (key == "VisibleLedCount") VisibleLedCount = ParseInt(value, VisibleLedCount);
                else if (key == "BrightnessPercent") BrightnessPercent = ParseInt(value, BrightnessPercent);
                else if (key == "UpdateRateMs") UpdateRateMs = ParseInt(value, UpdateRateMs);
                else if (key == "AlertUpdateRateMs") AlertUpdateRateMs = ParseInt(value, AlertUpdateRateMs);
                else if (key == "RpmSmoothingPercent") RpmSmoothingPercent = ParseInt(value, RpmSmoothingPercent);
                else if (key == "FallbackMaxRpm") FallbackMaxRpm = ParseInt(value, FallbackMaxRpm);
                else if (key == "FirstLedAtPercent") FirstLedAtPercent = ParseInt(value, FirstLedAtPercent);
                else if (key == "GreenZoneEndPercent") GreenZoneEndPercent = ParseInt(value, GreenZoneEndPercent);
                else if (key == "RedZoneStartPercent") RedZoneStartPercent = ParseInt(value, RedZoneStartPercent);
                else if (key == "HidTarget") HidTarget = value;
                else if (key == "LimiterStartPercent") LimiterStartPercent = ParseInt(value, LimiterStartPercent);
                else if (key == "LimiterHoldBufferPercent") LimiterHoldBufferPercent = ParseInt(value, LimiterHoldBufferPercent);
                else if (key == "LimiterMode") LimiterMode = MapOldLimiterMode(value);
                else if (key == "LimiterLedCount") LimiterLedCount = ParseInt(value, LimiterLedCount);
                else if (key == "LimiterColour") LimiterColour = value;
                else if (key == "LimiterFlashMs") LimiterFlashMs = ParseInt(value, LimiterFlashMs);
                else if (key == "FlagFlashMs") FlagFlashMs = ParseInt(value, FlagFlashMs);
                else if (key == "IdleMode") IdleMode = MapOldIdleMode(value);
                else if (key == "IdleBrightnessPercent") IdleBrightnessPercent = ParseInt(value, IdleBrightnessPercent);

                // Compatibility with the older build:
                else if (key == "OrangeStartPercent") GreenZoneEndPercent = ParseInt(value, GreenZoneEndPercent);
                else if (key == "RedStartPercent") RedZoneStartPercent = ParseInt(value, RedZoneStartPercent);
            }

            public void Normalize()
            {
                if (string.IsNullOrWhiteSpace(DeviceVidHex)) DeviceVidHex = "11FF";
                if (string.IsNullOrWhiteSpace(DevicePidHex)) DevicePidHex = "1112";

                VisibleLedCount = ClampSetting(VisibleLedCount, 1, 10);
                BrightnessPercent = ClampSetting(BrightnessPercent, 5, 100);
                UpdateRateMs = ClampSetting(UpdateRateMs, 8, 80);
                AlertUpdateRateMs = ClampSetting(AlertUpdateRateMs, 8, 80);
                RpmSmoothingPercent = ClampSetting(RpmSmoothingPercent, 0, 60);
                FallbackMaxRpm = ClampSetting(FallbackMaxRpm, 3000, 15000);
                FirstLedAtPercent = ClampSetting(FirstLedAtPercent, 0, 20);

                GreenZoneEndPercent = ClampSetting(GreenZoneEndPercent, 10, 60);
                RedZoneStartPercent = ClampSetting(RedZoneStartPercent, 45, 95);

                if (RedZoneStartPercent <= GreenZoneEndPercent + 5)
                {
                    RedZoneStartPercent = GreenZoneEndPercent + 10;
                }

                RedZoneStartPercent = ClampSetting(RedZoneStartPercent, 45, 95);

                LimiterStartPercent = ClampSetting(LimiterStartPercent, 80, 100);
                LimiterHoldBufferPercent = ClampSetting(LimiterHoldBufferPercent, 0, 10);
                LimiterLedCount = ClampSetting(LimiterLedCount, 1, 10);
                LimiterFlashMs = ClampSetting(LimiterFlashMs, 50, 500);
                FlagFlashMs = ClampSetting(FlagFlashMs, 80, 700);
                IdleBrightnessPercent = ClampSetting(IdleBrightnessPercent, 1, 35);

                if (!IsOneOf(HidTarget, new[] { "Interface 1", "Interface 0", "Interface 2", "Interface 3", "All interfaces" }))
                {
                    HidTarget = "Interface 1";
                }

                if (!IsOneOf(LimiterMode, new[] { "Flash last LEDs", "Flash full bar", "Solid last LEDs", "Solid full bar", "Off" }))
                {
                    LimiterMode = "Flash last LEDs";
                }

                if (!IsOneOf(LimiterColour, new[] { "Purple", "Red", "Blue", "White", "Orange", "Cyan", "Pink", "Yellow" }))
                {
                    LimiterColour = "Purple";
                }

                if (!IsOneOf(IdleMode, new[] { "Off", "Dim green", "Dim purple", "Soft white", "Breathing purple", "Chase" }))
                {
                    IdleMode = "Off";
                }
            }

            private static string MapOldLimiterMode(string value)
            {
                if (value == "Flash limiter LEDs") return "Flash last LEDs";
                if (value == "Solid limiter LEDs") return "Solid last LEDs";
                return value;
            }

            private static string MapOldIdleMode(string value)
            {
                if (value == "Dim green") return "Dim green";
                if (value == "Keep last colour") return "Off";
                if (value == "Rainbow idle") return "Chase";
                if (value == "Custom individual LEDs") return "Off";
                return value;
            }

            private static bool IsOneOf(string value, string[] options)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    if (string.Equals(value, options[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool ParseBool(string value, bool fallback)
            {
                bool parsed;

                if (bool.TryParse(value, out parsed))
                {
                    return parsed;
                }

                if (value == "1") return true;
                if (value == "0") return false;

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
