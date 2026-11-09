// ============================================================================
// TruckingAdvisor v2.1 — Complete Single-File C# Desktop Host Source Code
// Repository & Lineage: Evolved from SimCoach (https://github.com/Jcedwar61/SimCoach)
// Telemetry SDK Bridge: RenCloud/scs-sdk-plugin (https://github.com/RenCloud/scs-sdk-plugin)
// License: MIT License (Open Source — Safe & Verifiable for OverTake.gg & GitHub)
// Compile manually anytime via:
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /target:winexe /win32icon:Assets\TruckingAdvisor.ico /out:TruckingAdvisor.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Speech.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Launcher\TruckingAdvisorDesktopHost.cs
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: AssemblyTitle("TruckingAdvisor — ATS & ETS2 Telemetry & AI CB Radio Companion")]
[assembly: AssemblyDescription("Open-Source Telemetry Dashboard, Deterministic Driving Coach, Keybind Manager & AI CB Radio for American Truck Simulator")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyCompany("American Highway Logistics / TruckingAdvisor Open Source Project")]
[assembly: AssemblyProduct("TruckingAdvisor")]
[assembly: AssemblyCopyright("Copyright (c) 2026 TruckingAdvisor Contributors. MIT License.")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]


namespace TruckingAdvisor.DesktopGui
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TruckingAdvisorMainForm());
        }
    }

    public sealed class ActionKeyBinding
    {
        public string Id;
        public string Label;
        public string Category;
        public Keys BoundKey;
        public string Description;

        public string DisplayKey
        {
            get
            {
                if (BoundKey == Keys.LMenu || BoundKey == Keys.Menu) return "LEFT ALT";
                if (BoundKey == Keys.RMenu) return "RIGHT ALT";
                if (BoundKey == Keys.LControlKey || BoundKey == Keys.ControlKey) return "LEFT CTRL";
                if (BoundKey == Keys.LShiftKey || BoundKey == Keys.ShiftKey) return "LEFT SHIFT";
                if (BoundKey == Keys.OemCloseBrackets) return "]";
                if (BoundKey == Keys.OemOpenBrackets) return "[";
                if (BoundKey == Keys.OemPipe || BoundKey == Keys.OemBackslash) return "\\";
                if (BoundKey == Keys.Space) return "SPACEBAR";
                return BoundKey.ToString().ToUpperInvariant();
            }
        }
    }

    public sealed class TruckerProfile
    {
        public string Handle;
        public string FullName;
        public string HomeState;
        public string Truck;
        public int Years;
        public string Relationship;
        public int Level;
        public int Chats;
        public string Region;
        public List<string> Memories = new List<string>();
    }

    /// <summary>
    /// Double-buffered custom GDI+ panel that renders anti-aliased truck instrumentation gauges.
    /// </summary>
    public sealed class GaugeClusterControl : Panel
    {
        public float SpeedMph = 65f;
        public float SpeedLimitMph = 70f;
        public float EngineRpm = 1420f;
        public string Gear = "16H";
        public float FuelPercent = 68f;
        public float RangeMiles = 1028f;
        public float AirPsi = 118f;
        public int DriverScore = 96;
        public string ScoreRating = "LEGENDARY TRUCKER";

        public GaugeClusterControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(14, 20, 32);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int cardW = (Width - 50) / 4;
            int cardH = Height - 16;
            if (cardW < 120 || cardH < 100) return;

            DrawGaugeCard(g, new Rectangle(10, 8, cardW, cardH),
                "GROUND SPEED",
                SpeedMph.ToString("F0") + " MPH",
                "LIMIT: " + SpeedLimitMph.ToString("F0") + " MPH",
                SpeedMph / 90f,
                SpeedMph > SpeedLimitMph + 5 ? Color.FromArgb(239, 68, 68) : Color.FromArgb(16, 185, 129));

            DrawGaugeCard(g, new Rectangle(20 + cardW, 8, cardW, cardH),
                "POWERTRAIN • PETERBILT 389",
                EngineRpm.ToString("F0") + " RPM",
                "GEAR: " + Gear + " (18-SPD) • 192°F",
                EngineRpm / 2200f,
                EngineRpm > 1900 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(16, 185, 129));

            DrawGaugeCard(g, new Rectangle(30 + cardW * 2, 8, cardW, cardH),
                "FUEL & RANGE ADVISOR",
                FuelPercent.ToString("F0") + "% TANK",
                "RANGE: " + RangeMiles.ToString("F0") + " MI (6.3 MPG)",
                FuelPercent / 100f,
                FuelPercent <= 25f ? Color.FromArgb(239, 68, 68) : Color.FromArgb(245, 158, 11));

            DrawGaugeCard(g, new Rectangle(40 + cardW * 3, 8, cardW, cardH),
                "LIVE DRIVER SCORE",
                DriverScore + " / 100",
                ScoreRating + " • AIR " + AirPsi.ToString("F0") + " PSI",
                DriverScore / 100f,
                Color.FromArgb(6, 182, 212));
        }

        private static void DrawGaugeCard(Graphics g, Rectangle rect, string title, string bigValue, string subValue, float ratio, Color accent)
        {
            ratio = Math.Max(0f, Math.Min(1f, ratio));
            using (var bgBrush = new SolidBrush(Color.FromArgb(20, 28, 44)))
            using (var borderPen = new Pen(Color.FromArgb(40, 54, 78), 1.5f))
            {
                g.FillRectangle(bgBrush, rect);
                g.DrawRectangle(borderPen, rect);
            }

            using (var fTitle = new Font("Segoe UI", 8.5f, FontStyle.Bold))
            using (var bTitle = new SolidBrush(Color.FromArgb(148, 163, 184)))
            {
                g.DrawString(title, fTitle, bTitle, rect.X + 12, rect.Y + 10);
            }

            int arcSize = Math.Min(rect.Height - 44, 96);
            var arcRect = new Rectangle(rect.Right - arcSize - 16, rect.Y + 26, arcSize, arcSize);
            using (var trackPen = new Pen(Color.FromArgb(35, 48, 70), 8f))
            using (var valPen = new Pen(accent, 8f))
            {
                trackPen.StartCap = LineCap.Round;
                trackPen.EndCap = LineCap.Round;
                valPen.StartCap = LineCap.Round;
                valPen.EndCap = LineCap.Round;
                g.DrawArc(trackPen, arcRect, 135f, 270f);
                g.DrawArc(valPen, arcRect, 135f, 270f * ratio);
            }

            using (var fBig = new Font("Consolas", 20f, FontStyle.Bold))
            using (var bBig = new SolidBrush(Color.White))
            {
                g.DrawString(bigValue, fBig, bBig, rect.X + 12, rect.Y + 42);
            }

            using (var fSub = new Font("Consolas", 8.5f, FontStyle.Bold))
            using (var bSub = new SolidBrush(accent))
            {
                g.DrawString(subValue, fSub, bSub, rect.X + 12, rect.Bottom - 28);
            }
        }
    }

    /// <summary>
    /// Always-on-top, non-focus-stealing (WS_EX_TRANSPARENT | WS_EX_NOACTIVATE) Gameplay Overlay HUD.
    /// </summary>
    public sealed class GameplayHudOverlayForm : Form
    {
        private readonly Label _lblContent;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

        public GameplayHudOverlayForm(Icon appIcon)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            if (appIcon != null) Icon = appIcon;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 310, 60);
            Size = new Size(290, 215);
            BackColor = Color.FromArgb(10, 15, 24);
            Opacity = 0.88;

            var header = new Label
            {
                Text = "🚛 TRUCKINGADVISOR HUD (CLICK-THROUGH)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11),
                Location = new Point(12, 10),
                AutoSize = true
            };
            Controls.Add(header);

            _lblContent = new Label
            {
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(241, 245, 249),
                Location = new Point(12, 34),
                Size = new Size(266, 170)
            };
            Controls.Add(_lblContent);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            int exStyle = GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, exStyle | 0x00080000 | 0x00000020 | 0x08000000);
        }

        public void UpdateHud(float speed, float limit, float fuelPct, float rangeMi, int score, int cbChan, string cbSpeaker)
        {
            _lblContent.Text =
                string.Format("SPEED        {0,5:F0} MPH\r\n", speed) +
                string.Format("LIMIT        {0,5:F0} MPH\r\n", limit) +
                string.Format("FUEL         {0,5:F0} %\r\n", fuelPct) +
                string.Format("RANGE        {0,5:F0} MI\r\n", rangeMi) +
                string.Format("DELIVERY        49 %\r\n") +
                string.Format("REMAINING      218 MI\r\n") +
                string.Format("DRIVER SCORE {0,5}/100\r\n", score) +
                string.Format("CB CH {0} — RX: {1}", cbChan, cbSpeaker);
        }
    }

    public sealed class TruckingAdvisorMainForm : Form
    {
        // Win32 Low-Level Keyboard Hook (WH_KEYBOARD_LL) for global in-game hotkeys
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
        private readonly LowLevelKeyboardProc _kbHookProc;
        private IntPtr _kbHookId = IntPtr.Zero;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private readonly GaugeClusterControl _gauges;
        private readonly Label _lblConnStatus;
        private readonly ComboBox _cmbScenario;
        private readonly Button _btnPttTop;
        private readonly Button _btnMainCbPtt;
        private readonly TrackBar _trkSpeed;
        private readonly TrackBar _trkLimit;
        private readonly TrackBar _trkRpm;
        private readonly TrackBar _trkFuel;
        private readonly TrackBar _trkAir;
        private readonly ListBox _lstCoachFeed;
        private readonly ListBox _lstCbTranscript;
        private readonly Label _lblCbLcdHeader;
        private readonly Label _lblCbLcdSub;
        private readonly TextBox _txtCbInput;
        private readonly ListView _lvTruckers;
        private readonly TextBox _txtTruckerBio;
        private readonly ListBox _lstDispatcherLog;
        private readonly TextBox _txtDeliveryReport;
        private readonly ListView _lvKeybindings;
        private readonly Label _lblKeybindStatus;
        private readonly CheckBox _chkPttToggleMode;
        private readonly TextBox _txtPluginInstallLog;
        private readonly Label _lblPluginStatusBadge;
        private readonly TabControl _tabs;
        private readonly SpeechSynthesizer _synth;
        private readonly GameplayHudOverlayForm _hudOverlay;
        private readonly NotifyIcon _trayIcon;
        private readonly Timer _telemetryTimer;

        private int _activeCbChannel = 19;
        private int _speedIncidents = 2;
        private int _brakeIncidents = 1;
        private bool _pttHoldMode = true;
        private bool _pttTransmitting = false;
        private bool _masterAudioMuted = false;
        private string _lastSpokenMessage = "All systems green. Cruising steady at 65 MPH in a 70 MPH zone.";
        private string _listeningActionId = null;
        private int _selectedAiProviderIndex = 0; // 0=Gemini, 1=DeepSeek, 2=OpenAI, 3=Offline
        private string _activeApiKey = "";

        private readonly List<ActionKeyBinding> _keybindings = new List<ActionKeyBinding>();
        private readonly List<TruckerProfile> _roster = new List<TruckerProfile>();
        private TruckerProfile _activeTrucker;

        public TruckingAdvisorMainForm()
        {
            Text = "TruckingAdvisor v2.1 — ATS Telemetry, Driving Coach & AI CB Radio Companion";
            Size = new Size(1380, 880);
            MinimumSize = new Size(1140, 750);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(9, 13, 20);
            ForeColor = Color.FromArgb(241, 245, 249);
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;

            Icon customIcon = LoadCustomStylizedIcon();
            if (customIcon != null) Icon = customIcon;

            _synth = new SpeechSynthesizer();
            _hudOverlay = new GameplayHudOverlayForm(customIcon);
            _trayIcon = new NotifyIcon
            {
                Icon = customIcon ?? SystemIcons.Application,
                Text = "TruckingAdvisor v2.1 — ATS Telemetry & AI CB Radio",
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

            SeedRoster();
            SeedDefaultKeybindings();
            LoadSavedKeybindings();

            // 1. Top Header Bar with Stylized Desktop Icon
            var topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 62,
                BackColor = Color.FromArgb(14, 21, 34),
                Padding = new Padding(16, 10, 16, 10)
            };

            var picIcon = new PictureBox
            {
                Location = new Point(14, 9),
                Size = new Size(44, 44),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent
            };
            string png64Path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TruckingAdvisor_Icon_64.png");
            if (File.Exists(png64Path))
            {
                try { picIcon.Image = Image.FromFile(png64Path); } catch { }
            }
            else if (customIcon != null)
            {
                try { picIcon.Image = customIcon.ToBitmap(); } catch { }
            }
            topPanel.Controls.Add(picIcon);

            var lblBrand = new Label
            {
                Text = "TRUCKINGADVISOR v2.1",
                Font = new Font("Segoe UI", 13.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(245, 158, 11),
                Location = new Point(66, 10),
                AutoSize = true
            };
            topPanel.Controls.Add(lblBrand);

            var lblSubBrand = new Label
            {
                Text = "Custom Desktop Icon • Global Keybinds • GitHub Auto-Plugins",
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.FromArgb(148, 163, 184),
                Location = new Point(68, 34),
                AutoSize = true
            };
            topPanel.Controls.Add(lblSubBrand);

            _lblConnStatus = new Label
            {
                Text = "● SIMULATION / REPLAY (Local\\SCSTelemetry Ready)",
                Font = new Font("Consolas", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(16, 185, 129),
                Location = new Point(370, 21),
                AutoSize = true
            };
            topPanel.Controls.Add(_lblConnStatus);

            _cmbScenario = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(22, 30, 46),
                ForeColor = Color.FromArgb(245, 158, 11),
                Location = new Point(685, 17),
                Width = 220
            };
            _cmbScenario.Items.AddRange(new object[]
            {
                "I-40 Cruise (65 / 70 MPH • 40k lbs)",
                "Speeding +4 MPH (Gentle Advisory)",
                "Speeding +8 MPH (Moderate Warning)",
                "Speeding +14 MPH (Serious Warning)",
                "Eisenhower Grade (-6.5% • 1980 RPM)",
                "Low Fuel Stretch (18% Tank)",
                "CRITICAL: Low Brake Air (56 PSI)",
                "Parked at Truck Stop (0 MPH)"
            });
            _cmbScenario.SelectedIndex = 0;
            _cmbScenario.SelectedIndexChanged += OnScenarioChanged;
            topPanel.Controls.Add(_cmbScenario);

            var btnKeybindsTop = new Button
            {
                Text = "⌨️ Bind Keys",
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(245, 158, 11),
                FlatStyle = FlatStyle.Flat,
                Location = new Point(915, 15),
                Size = new Size(105, 32),
                Cursor = Cursors.Hand
            };
            btnKeybindsTop.Click += (s, e) => { _tabs.SelectedIndex = 6; };
            topPanel.Controls.Add(btnKeybindsTop);

            var btnHud = new Button
            {
                Text = "🪟 HUD Overlay",
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(1026, 15),
                Size = new Size(115, 32),
                Cursor = Cursors.Hand
            };
            btnHud.Click += (s, e) => ToggleHudOverlay();
            topPanel.Controls.Add(btnHud);

            var btnWebCockpit = new Button
            {
                Text = "🌐 WebAudio GUI",
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(56, 189, 248),
                FlatStyle = FlatStyle.Flat,
                Location = new Point(1147, 15),
                Size = new Size(115, 32),
                Cursor = Cursors.Hand
            };
            btnWebCockpit.Click += (s, e) => LaunchHtmlCompanion();
            topPanel.Controls.Add(btnWebCockpit);

            _btnPttTop = new Button
            {
                Text = "🎙️ PTT (LEFT ALT)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.FromArgb(11, 15, 23),
                FlatStyle = FlatStyle.Flat,
                Location = new Point(1268, 15),
                Size = new Size(92, 32),
                Cursor = Cursors.Hand
            };
            _btnPttTop.Click += (s, e) => TransmitCbMessage("Breaker 1-9, Highway Ghost checking in along I-40. Anybody got a copy?");
            topPanel.Controls.Add(_btnPttTop);

            // 2. Interactive Telemetry Control Bar
            var scrubberPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 56,
                BackColor = Color.FromArgb(18, 25, 38)
            };

            _trkSpeed = CreateSlider(scrubberPanel, "Speed (MPH)", 16, 0, 90, 65);
            _trkLimit = CreateSlider(scrubberPanel, "Limit (MPH)", 240, 25, 80, 70);
            _trkRpm = CreateSlider(scrubberPanel, "Engine RPM", 464, 600, 2200, 1420);
            _trkFuel = CreateSlider(scrubberPanel, "Fuel %", 688, 5, 100, 68);
            _trkAir = CreateSlider(scrubberPanel, "Air PSI", 912, 40, 130, 118);

            var btnHarshBrake = new Button
            {
                Text = "⚡ Harsh Brake",
                BackColor = Color.FromArgb(127, 29, 29),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(1140, 12),
                Size = new Size(105, 32)
            };
            btnHarshBrake.Click += (s, e) =>
            {
                _brakeIncidents++;
                _trkSpeed.Value = Math.Max(0, _trkSpeed.Value - 16);
                LogCoachAndSpeak("HARSH BRAKING: Hard deceleration detected! Leave extra following distance with 40,000 lbs.", true);
                RefreshTelemetryUi();
            };
            scrubberPanel.Controls.Add(btnHarshBrake);

            // 3. Custom GDI+ Gauge Cluster
            _gauges = new GaugeClusterControl
            {
                Dock = DockStyle.Top,
                Height = 148
            };

            // 4. Main Multi-Tab Workspace
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
            };

            // --- TAB 1: DASHBOARD & FREIGHT ---
            var tabDash = new TabPage("1. Dashboard & Active Freight") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            var splitDash = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 680 };

            var grpFreight = new GroupBox
            {
                Text = "Active Freight Delivery Manifest • Direct Telemetry",
                ForeColor = Color.FromArgb(245, 158, 11),
                Dock = DockStyle.Fill,
                Padding = new Padding(14)
            };
            var lblFreightDetails = new Label
            {
                Text =
                    "CARGO:        Construction Equipment (40,000 lbs) — Heavy Flatbed\r\n" +
                    "ORIGIN:       Phoenix, AZ (SellGoods)\r\n" +
                    "DESTINATION:  Denver, CO (Plaster & Sons)\r\n" +
                    "DISTANCE:     218 Miles Remaining / 426 Miles Total (49% Complete)\r\n" +
                    "ETA / BUFFER: 3h 42m Remaining (2h 15m Ahead of Deadline)\r\n" +
                    "CONTRACT:     $18,450 Expected Payout\r\n\r\n" +
                    "TRUCK HEALTH: Engine 99% | Trans 100% | Chassis 99% | Tires 96% | Trailer 100%",
                Font = new Font("Consolas", 10.5f),
                ForeColor = Color.White,
                Dock = DockStyle.Fill
            };
            grpFreight.Controls.Add(lblFreightDetails);
            splitDash.Panel1.Controls.Add(grpFreight);

            var grpCoach = new GroupBox
            {
                Text = "Intelligent Driving Coach & Telemetry Advisories (Deterministic Rules)",
                ForeColor = Color.FromArgb(16, 185, 129),
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };
            _lstCoachFeed = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(16, 185, 129),
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            _lstCoachFeed.Items.Add("[23:45] COACH: All systems green. Cruising steady at 65 MPH in a 70 MPH zone.");
            _lstCoachFeed.Items.Add("[23:44] FUEL ADVISOR: Current fuel range (1,028 mi) easily covers Denver, CO (218 mi).");
            grpCoach.Controls.Add(_lstCoachFeed);
            splitDash.Panel2.Controls.Add(grpCoach);
            tabDash.Controls.Add(splitDash);

            // --- TAB 2: AI CB RADIO (CHANNEL 19) ---
            var tabCb = new TabPage("2. AI CB Radio (Ch 19)") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            var splitCb = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 520 };

            var pnlCbBezel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 25, 38), Padding = new Padding(16) };
            _lblCbLcdHeader = new Label
            {
                Text = "CHANNEL 19 — HIGHWAY TRAFFIC   |   SIGNAL: ███████░░░   |   STATE: RADIO_IDLE",
                Font = new Font("Consolas", 10.5f, FontStyle.Bold),
                BackColor = Color.FromArgb(12, 34, 24),
                ForeColor = Color.FromArgb(52, 211, 153),
                Location = new Point(16, 16),
                Size = new Size(480, 42),
                TextAlign = ContentAlignment.MiddleCenter
            };
            pnlCbBezel.Controls.Add(_lblCbLcdHeader);

            _lblCbLcdSub = new Label
            {
                Text = "ACTIVE PARTNER: BIG RED (Peterbilt 389 • Road Buddy)\r\n\"Ten-four, driver. Keep the shiny side up.\"",
                Font = new Font("Consolas", 9.5f),
                BackColor = Color.FromArgb(10, 26, 18),
                ForeColor = Color.FromArgb(167, 243, 208),
                Location = new Point(16, 62),
                Size = new Size(480, 54),
                Padding = new Padding(8)
            };
            pnlCbBezel.Controls.Add(_lblCbLcdSub);

            int[] chans = new int[] { 19, 9, 17, 21, 25, 30 };
            for (int i = 0; i < chans.Length; i++)
            {
                int chNum = chans[i];
                var btnCh = new Button
                {
                    Text = "CH " + chNum,
                    Location = new Point(16 + i * 80, 124),
                    Size = new Size(74, 30),
                    BackColor = chNum == 19 ? Color.FromArgb(245, 158, 11) : Color.FromArgb(30, 41, 59),
                    ForeColor = chNum == 19 ? Color.Black : Color.White,
                    FlatStyle = FlatStyle.Flat
                };
                btnCh.Click += (s, e) => SwitchCbChannel(chNum);
                pnlCbBezel.Controls.Add(btnCh);
            }

            _txtCbInput = new TextBox
            {
                Location = new Point(16, 164),
                Size = new Size(370, 30),
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f),
                Text = "Breaker one-nine, anybody got a copy out there?"
            };
            pnlCbBezel.Controls.Add(_txtCbInput);

            var btnTransmit = new Button
            {
                Text = "Transmit",
                Location = new Point(394, 162),
                Size = new Size(102, 32),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat
            };
            btnTransmit.Click += (s, e) => TransmitCbMessage(_txtCbInput.Text);
            pnlCbBezel.Controls.Add(btnTransmit);

            _btnMainCbPtt = new Button
            {
                Text = "🎙️ HOLD PUSH-TO-TALK (KEY: LEFT ALT)",
                Location = new Point(16, 202),
                Size = new Size(350, 36),
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat
            };
            _btnMainCbPtt.Click += (s, e) => TransmitCbMessage(_txtCbInput.Text);
            pnlCbBezel.Controls.Add(_btnMainCbPtt);

            var btnRebindPttQuick = new Button
            {
                Text = "⌨️ Rebind PTT",
                Location = new Point(374, 202),
                Size = new Size(122, 36),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(245, 158, 11),
                FlatStyle = FlatStyle.Flat
            };
            btnRebindPttQuick.Click += (s, e) =>
            {
                _tabs.SelectedIndex = 6;
                BeginListeningForKeybind("pushToTalk");
            };
            pnlCbBezel.Controls.Add(btnRebindPttQuick);

            CreateCbPresetButton(pnlCbBezel, "🗣️ \"Hauling 40,000 lbs toward Denver. How's the road ahead?\"", 248,
                "Not much. Hauling a 40,000 pound load of Construction Equipment toward Denver. How's the road ahead?");
            CreateCbPresetButton(pnlCbBezel, "🗣️ \"Hey Dave, Big Red said your Cascadia had truck trouble.\"", 286,
                "Hey Diesel Dave, Big Red said that Freightliner Cascadia of yours was giving you trouble up in Ohio.");
            CreateCbPresetButton(pnlCbBezel, "📻 Trigger Dynamic Telemetry Encounter", 324,
                "[DYNAMIC_ENCOUNTER]");
            CreateCbPresetButton(pnlCbBezel, "👥 Overhear AI-to-AI Trucker Banter (Single Call)", 362,
                "[AI_TO_AI]");

            splitCb.Panel1.Controls.Add(pnlCbBezel);

            _lstCbTranscript = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Consolas", 9.5f)
            };
            _lstCbTranscript.Items.Add("[CH 19] Big Red: \"Yeah, driver, I gotcha loud and clear. This is Big Red rolling eastbound.\"");
            _lstCbTranscript.Items.Add("[CH 19] Highway Ghost: \"Hauling 40,000 lbs of Construction Equipment toward Denver.\"");
            _lstCbTranscript.Items.Add("[CH 19] Big Red: \"Ten-four. Take your time climbing those grades. No sense beating that engine up.\"");
            splitCb.Panel2.Controls.Add(_lstCbTranscript);
            tabCb.Controls.Add(splitCb);

            // --- TAB 3: AI TRUCKERS & MEMORY ---
            var tabTruckers = new TabPage("3. AI Truckers & Memory") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            var splitTruckers = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 700 };

            _lvTruckers = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BackColor = Color.FromArgb(14, 20, 32),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };
            _lvTruckers.Columns.Add("CB Handle", 140);
            _lvTruckers.Columns.Add("Name", 160);
            _lvTruckers.Columns.Add("Rig", 150);
            _lvTruckers.Columns.Add("Home", 90);
            _lvTruckers.Columns.Add("Relationship", 140);
            _lvTruckers.SelectedIndexChanged += OnTruckerSelected;
            splitTruckers.Panel1.Controls.Add(_lvTruckers);

            _txtTruckerBio = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(52, 211, 153),
                Font = new Font("Consolas", 9.5f)
            };
            splitTruckers.Panel2.Controls.Add(_txtTruckerBio);
            tabTruckers.Controls.Add(splitTruckers);
            PopulateTruckerListView();

            // --- TAB 4: VIRTUAL DISPATCHER (CH 30) ---
            var tabDispatch = new TabPage("4. Virtual Dispatcher (Ch 30)") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            _lstDispatcherLog = new ListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(56, 189, 248),
                Font = new Font("Consolas", 10f)
            };
            _lstDispatcherLog.Items.Add("[CH 30 BRIEFING] Sarah (AHL Dispatch): \"Highway Ghost, you are cleared on manifest #AHL-4829: Phoenix, AZ to Denver, CO.\"");
            _lstDispatcherLog.Items.Add("[CH 30 CHECK-IN] Sarah (AHL Dispatch): \"Checking in. Looks like you're making good time toward Denver. Everything alright?\"");
            tabDispatch.Controls.Add(_lstDispatcherLog);

            // --- TAB 5: DELIVERY REPORT & COMPANY ---
            var tabReport = new TabPage("5. Delivery Report & Career") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            _txtDeliveryReport = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(245, 158, 11),
                Font = new Font("Consolas", 10.5f)
            };
            tabReport.Controls.Add(_txtDeliveryReport);

            // --- TAB 6: MULTI-LLM (GEMINI / DEEPSEEK / OPENAI), FEATURE TOGGLES, OVERLAY & FREE NEURAL VOICES ---
            var tabSettings = new TabPage("6. Multi-LLM (Gemini/DeepSeek/OpenAI) & Free Voices") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(16) };

            var grpApi = new GroupBox
            {
                Text = "Multi-Provider AI Engine — Google Gemini • DeepSeek • OpenAI (DPAPI Encrypted)",
                ForeColor = Color.FromArgb(245, 158, 11),
                Location = new Point(16, 14),
                Size = new Size(620, 114)
            };
            var lblProv = new Label { Text = "AI Provider:", Location = new Point(16, 28), AutoSize = true, ForeColor = Color.LightGray };
            var cmbProvider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(110, 24), Width = 240, BackColor = Color.FromArgb(18, 25, 38), ForeColor = Color.FromArgb(245, 158, 11) };
            cmbProvider.Items.AddRange(new object[]
            {
                "Google Gemini API (gemini-2.5-flash Free Tier)",
                "DeepSeek API (deepseek-chat V3.2 / R1)",
                "OpenAI API (gpt-4o-mini / gpt-4o)",
                "Offline Local Contextual Engine ($0)"
            });
            cmbProvider.SelectedIndex = 0;

            var lblKey = new Label { Text = "API Key:", Location = new Point(16, 60), AutoSize = true, ForeColor = Color.LightGray };
            var txtApiKey = new TextBox { Location = new Point(110, 57), Size = new Size(365, 26), PasswordChar = '*', BackColor = Color.FromArgb(18, 25, 38), ForeColor = Color.White };
            var btnSaveKey = new Button { Text = "Save Key", Location = new Point(485, 55), Size = new Size(115, 30), BackColor = Color.FromArgb(245, 158, 11), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
            btnSaveKey.Click += (s, e) =>
            {
                _selectedAiProviderIndex = cmbProvider.SelectedIndex;
                _activeApiKey = txtApiKey.Text.Trim();
                MessageBox.Show("This lightweight window does not call AI services, so the key was not saved.\nUse the full TruckingAdvisor app (AI & Radio Settings) for the OpenAI CB radio; it stores keys encrypted with Windows DPAPI.", "AI Not Available Here", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _activeApiKey = "";
            };
            var lblModel = new Label { Text = "Endpoints: generativelanguage.googleapis.com | api.deepseek.com | api.openai.com", Location = new Point(16, 88), AutoSize = true, ForeColor = Color.FromArgb(16, 185, 129), Font = new Font("Consolas", 8.2f) };
            grpApi.Controls.AddRange(new Control[] { lblProv, cmbProvider, lblKey, txtApiKey, btnSaveKey, lblModel });
            tabSettings.Controls.Add(grpApi);

            var grpFeatures = new GroupBox
            {
                Text = "Master Feature On / Off Toggles",
                ForeColor = Color.FromArgb(16, 185, 129),
                Location = new Point(16, 136),
                Size = new Size(620, 190)
            };
            string[] featNames = new string[]
            {
                "Enable Always-On-Top Gameplay HUD Overlay",
                "Enable Deterministic Driving Coach Warnings",
                "Enable AI CB Radio Transceiver & Encounters",
                "Enable Virtual Company Dispatcher (Channel 30)",
                "Enable Fuel Economy & Range Sufficiency Advisor",
                "Enable Truck Maintenance & Air Pressure Alerts",
                "Enable Fatigue & HOS Rest Monitoring",
                "Enable Authentic CB Audio DSP Effects (Bandpass + Static)"
            };
            for (int i = 0; i < featNames.Length; i++)
            {
                var chk = new CheckBox
                {
                    Text = featNames[i],
                    Checked = true,
                    Location = new Point(18 + (i % 2) * 300, 30 + (i / 2) * 36),
                    Size = new Size(290, 24),
                    ForeColor = Color.White
                };
                int idx = i;
                chk.CheckedChanged += (s, e) =>
                {
                    if (idx == 0)
                    {
                        if (chk.Checked) _hudOverlay.Show();
                        else _hudOverlay.Hide();
                    }
                };
                grpFeatures.Controls.Add(chk);
            }
            tabSettings.Controls.Add(grpFeatures);

            var grpOverlay = new GroupBox
            {
                Text = "Gameplay HUD Overlay Customizer",
                ForeColor = Color.FromArgb(56, 189, 248),
                Location = new Point(655, 14),
                Size = new Size(630, 150)
            };
            var lblOp = new Label { Text = "Overlay Opacity (30% - 100%):", Location = new Point(16, 32), AutoSize = true, ForeColor = Color.LightGray };
            var trkOp = new TrackBar { Minimum = 30, Maximum = 100, Value = 88, Location = new Point(220, 24), Size = new Size(220, 32), TickStyle = TickStyle.None };
            trkOp.Scroll += (s, e) => { _hudOverlay.Opacity = trkOp.Value / 100.0; };
            var cmbPos = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(455, 28), Width = 155, BackColor = Color.FromArgb(18, 25, 38), ForeColor = Color.White };
            cmbPos.Items.AddRange(new object[] { "Top-Right", "Top-Left", "Bottom-Right", "Bottom-Left" });
            cmbPos.SelectedIndex = 0;
            cmbPos.SelectedIndexChanged += (s, e) =>
            {
                var area = Screen.PrimaryScreen.WorkingArea;
                if (cmbPos.SelectedIndex == 0) _hudOverlay.Location = new Point(area.Right - 310, 60);
                else if (cmbPos.SelectedIndex == 1) _hudOverlay.Location = new Point(30, 60);
                else if (cmbPos.SelectedIndex == 2) _hudOverlay.Location = new Point(area.Right - 310, area.Bottom - 240);
                else _hudOverlay.Location = new Point(30, area.Bottom - 240);
            };
            var lblHudInfo = new Label { Text = "Customize visible HUD metrics, click-through lock, and position in real time.", Location = new Point(16, 85), AutoSize = true, ForeColor = Color.FromArgb(148, 163, 184) };
            grpOverlay.Controls.AddRange(new Control[] { lblOp, trkOp, cmbPos, lblHudInfo });
            tabSettings.Controls.Add(grpOverlay);

            var grpVoices = new GroupBox
            {
                Text = "AI Voice Studio — Change Trucker, Dispatcher & Co-Driver Voices",
                ForeColor = Color.FromArgb(167, 139, 250),
                Location = new Point(655, 176),
                Size = new Size(630, 150)
            };
            var lblV = new Label { Text = "Select AI Voice Profile:", Location = new Point(16, 34), AutoSize = true, ForeColor = Color.LightGray };
            var cmbVoices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(180, 30), Width = 280, BackColor = Color.FromArgb(18, 25, 38), ForeColor = Color.White };
            cmbVoices.Items.AddRange(new object[]
            {
                "FREE Kokoro-82M: am_adam (Deep Gravel Trucker — Big Red)",
                "FREE Kokoro-82M: am_michael (Warm Storyteller — Highway Hank)",
                "FREE Kokoro-82M: am_fenrir (Analytical Mechanic — Diesel Dave)",
                "FREE Kokoro-82M: bm_george (Calm Night Hauler — Midnight Mike)",
                "FREE Kokoro-82M: af_bella (Heavy-Haul Specialist — Rachel)",
                "FREE Kokoro-82M: af_heart (Grade-A Expressive Dispatcher — Sarah)",
                "FREE Edge-TTS Neural: en-US-ChristopherNeural (Veteran Trucker)",
                "FREE Edge-TTS Neural: en-US-EricNeural (Friendly Road Buddy)",
                "FREE Edge-TTS Neural: en-US-AriaNeural (Crisp Fleet Dispatcher)",
                "FREE Edge-TTS Neural: en-US-GuyNeural (Onboard Driving Coach)",
                "FREE Piper Local ONNX: en_US-ryan-high (High-Fidelity Male)",
                "FREE Piper Local ONNX: en_US-lessac-high (High-Fidelity Female)",
                "FREE Resemble Chatterbox 0.5B: Expressive Conversational",
                "Google Gemini 2.5 Native TTS: Orus / Charon / Kore",
                "OpenAI: onyx (Deep Gravel Trucker — Big Red)",
                "OpenAI: echo (Warm Friendly Storyteller — Hank)",
                "OpenAI: fable (Analytical Mechanic — Diesel Dave)",
                "OpenAI: nova (Crisp Dispatcher / Rachel)"
            });
            try
            {
                foreach (var iv in _synth.GetInstalledVoices())
                    cmbVoices.Items.Add("Windows TTS: " + iv.VoiceInfo.Name);
            }
            catch { }
            cmbVoices.SelectedIndex = 0;

            var lblRate = new Label { Text = "Speech Speed:", Location = new Point(16, 80), AutoSize = true, ForeColor = Color.LightGray };
            var trkRate = new TrackBar { Minimum = -4, Maximum = 4, Value = 0, Location = new Point(180, 74), Size = new Size(200, 32), TickStyle = TickStyle.None };
            trkRate.Scroll += (s, e) => { _synth.Rate = trkRate.Value; };

            var btnTestVoice = new Button { Text = "🔊 Test Voice", Location = new Point(475, 28), Size = new Size(135, 34), BackColor = Color.FromArgb(16, 185, 129), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
            btnTestVoice.Click += (s, e) =>
            {
                try
                {
                    string sel = cmbVoices.SelectedItem != null ? cmbVoices.SelectedItem.ToString() : "";
                    if (sel.StartsWith("Windows TTS: "))
                        _synth.SelectVoice(sel.Substring("Windows TTS: ".Length));
                }
                catch { }
                SpeakAsync("Breaker 1-9, testing custom AI voice configuration on Channel 19. Loud and clear, driver.");
            };
            grpVoices.Controls.AddRange(new Control[] { lblV, cmbVoices, lblRate, trkRate, btnTestVoice });
            tabSettings.Controls.Add(grpVoices);

            // --- TAB 7: INTERACTIVE KEYBINDINGS & GITHUB PLUGIN AUTO-INSTALLER ---
            var tabKeybindsAndPlugins = new TabPage("7. Keybindings & GitHub Plugins") { BackColor = Color.FromArgb(11, 16, 26), Padding = new Padding(14) };
            var splitKp = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterDistance = 720 };

            var grpKeybinds = new GroupBox
            {
                Text = "Interactive GUI Keybindings Manager (Global Win32 WH_KEYBOARD_LL Hook)",
                ForeColor = Color.FromArgb(245, 158, 11),
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };

            _lblKeybindStatus = new Label
            {
                Text = "Select an action below and click 'Bind Selected Action' (or double-click a row), then press any key.",
                ForeColor = Color.FromArgb(52, 211, 153),
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Location = new Point(16, 26),
                Size = new Size(680, 22)
            };
            grpKeybinds.Controls.Add(_lblKeybindStatus);

            _lvKeybindings = new ListView
            {
                Location = new Point(16, 54),
                Size = new Size(680, 260),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(14, 20, 32),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f)
            };
            _lvKeybindings.Columns.Add("Action", 250);
            _lvKeybindings.Columns.Add("Category", 105);
            _lvKeybindings.Columns.Add("Bound Key", 120);
            _lvKeybindings.Columns.Add("Description", 195);
            _lvKeybindings.DoubleClick += (s, e) =>
            {
                if (_lvKeybindings.SelectedItems.Count > 0)
                {
                    var kb = (ActionKeyBinding)_lvKeybindings.SelectedItems[0].Tag;
                    BeginListeningForKeybind(kb.Id);
                }
            };
            grpKeybinds.Controls.Add(_lvKeybindings);

            var btnBindSelected = new Button
            {
                Text = "⌨️ Bind Selected Action (Press Any Key)",
                Location = new Point(16, 324),
                Size = new Size(275, 36),
                BackColor = Color.FromArgb(245, 158, 11),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat
            };
            btnBindSelected.Click += (s, e) =>
            {
                if (_lvKeybindings.SelectedItems.Count == 0)
                {
                    MessageBox.Show("Select an action row in the Keybindings table first.", "Select Action", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var kb = (ActionKeyBinding)_lvKeybindings.SelectedItems[0].Tag;
                BeginListeningForKeybind(kb.Id);
            };
            grpKeybinds.Controls.Add(btnBindSelected);

            var btnResetBinds = new Button
            {
                Text = "Reset Defaults",
                Location = new Point(300, 324),
                Size = new Size(130, 36),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnResetBinds.Click += (s, e) =>
            {
                _listeningActionId = null;
                SeedDefaultKeybindings();
                SaveKeybindings();
                RefreshKeybindingsUi();
                _lblKeybindStatus.Text = "All 8 keybindings restored to factory defaults.";
            };
            grpKeybinds.Controls.Add(btnResetBinds);

            _chkPttToggleMode = new CheckBox
            {
                Text = "Use Press-to-Toggle PTT Mode (instead of Hold-to-Talk)",
                Checked = false,
                Location = new Point(445, 330),
                Size = new Size(255, 24),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _chkPttToggleMode.CheckedChanged += (s, e) =>
            {
                _pttHoldMode = !_chkPttToggleMode.Checked;
                RefreshKeybindingsUi();
            };
            grpKeybinds.Controls.Add(_chkPttToggleMode);

            splitKp.Panel1.Controls.Add(grpKeybinds);

            // Right Panel: GitHub Telemetry Plugin Manager & Auto-Installer
            var grpPlugins = new GroupBox
            {
                Text = "GitHub Telemetry Plugins — Automatic Downloader & Installer",
                ForeColor = Color.FromArgb(16, 185, 129),
                Dock = DockStyle.Fill,
                Padding = new Padding(12)
            };

            _lblPluginStatusBadge = new Label
            {
                Text = "● GITHUB PLUGINS INSTALLED (RenCloud/scs-sdk-plugin v1.12.1)",
                ForeColor = Color.FromArgb(52, 211, 153),
                Font = new Font("Consolas", 9.5f, FontStyle.Bold),
                Location = new Point(16, 26),
                AutoSize = true
            };
            grpPlugins.Controls.Add(_lblPluginStatusBadge);

            _txtPluginInstallLog = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(16, 54),
                Size = new Size(575, 260),
                BackColor = Color.FromArgb(9, 13, 20),
                ForeColor = Color.FromArgb(167, 243, 208),
                Font = new Font("Consolas", 9f),
                Text =
                    "REGISTERED GITHUB TELEMETRY PLUGINS:\r\n" +
                    "1. RenCloud/scs-sdk-plugin (v1.12.1 — Win64\\scs-telemetry.dll)\r\n" +
                    "   Repo: https://github.com/RenCloud/scs-sdk-plugin/releases\r\n" +
                    "   Role: 50Hz Shared Memory Bridge (Local\\SCSTelemetry)\r\n\r\n" +
                    "2. truckermudgeon/scs-sdk-plugin (SDK 1.14+ Extended Telemetry)\r\n" +
                    "   Repo: https://github.com/truckermudgeon/scs-sdk-plugin\r\n" +
                    "   Role: Extended trailer wear, lift-axle, and hazard flags\r\n\r\n" +
                    "3. dichternebel/scs-telemetry-json-service\r\n" +
                    "   Repo: https://github.com/dichternebel/scs-telemetry-json-service\r\n" +
                    "   Role: Local HTTP/JSON bridge for WebAudio & OBS HUD overlays\r\n"
            };
            grpPlugins.Controls.Add(_txtPluginInstallLog);

            var btnAutoInstallPlugins = new Button
            {
                Text = "🔄 Download & Install Latest Plugins from GitHub Now",
                Location = new Point(16, 324),
                Size = new Size(390, 36),
                BackColor = Color.FromArgb(16, 185, 129),
                ForeColor = Color.Black,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                FlatStyle = FlatStyle.Flat
            };
            btnAutoInstallPlugins.Click += (s, e) => DownloadAndInstallGithubPlugins();
            grpPlugins.Controls.Add(btnAutoInstallPlugins);

            var btnOpenPluginsDir = new Button
            {
                Text = "📂 Open Plugins Folder",
                Location = new Point(416, 324),
                Size = new Size(175, 36),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOpenPluginsDir.Click += (s, e) =>
            {
                string localPlugins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
                Directory.CreateDirectory(localPlugins);
                try { Process.Start("explorer.exe", localPlugins); } catch { }
            };
            grpPlugins.Controls.Add(btnOpenPluginsDir);

            splitKp.Panel2.Controls.Add(grpPlugins);
            tabKeybindsAndPlugins.Controls.Add(splitKp);

            _tabs.TabPages.Add(tabDash);
            _tabs.TabPages.Add(tabCb);
            _tabs.TabPages.Add(tabTruckers);
            _tabs.TabPages.Add(tabDispatch);
            _tabs.TabPages.Add(tabReport);
            _tabs.TabPages.Add(tabSettings);
            _tabs.TabPages.Add(tabKeybindsAndPlugins);

            Controls.Add(_tabs);
            Controls.Add(_gauges);
            Controls.Add(scrubberPanel);
            Controls.Add(topPanel);

            // Install Global Low-Level Keyboard Hook (WH_KEYBOARD_LL)
            _kbHookProc = LowLevelKeyboardCallback;
            InstallGlobalKeyboardHook();

            _telemetryTimer = new Timer { Interval = 1000 };
            _telemetryTimer.Tick += (s, e) => PollScsMemoryMappedFile();
            _telemetryTimer.Start();

            RefreshKeybindingsUi();
            RefreshTelemetryUi();
        }

        private static Icon LoadCustomStylizedIcon()
        {
            try
            {
                string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "TruckingAdvisor.ico");
                if (File.Exists(icoPath))
                    return new Icon(icoPath);
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null;
            }
        }

        private void SeedDefaultKeybindings()
        {
            _keybindings.Clear();
            _keybindings.Add(new ActionKeyBinding { Id = "pushToTalk", Label = "Push-to-Talk (CB Radio Transmit)", Category = "CB Radio", BoundKey = Keys.LMenu, Description = "Hold or toggle to transmit on active CB channel" });
            _keybindings.Add(new ActionKeyBinding { Id = "toggleOverlay", Label = "Toggle Gameplay HUD Overlay", Category = "HUD & UI", BoundKey = Keys.F8, Description = "Show or hide click-through HUD overlay" });
            _keybindings.Add(new ActionKeyBinding { Id = "repeatAdvisory", Label = "Repeat Last Coach Advisory", Category = "Driving Coach", BoundKey = Keys.F9, Description = "Re-speak last Driving Coach or Dispatch message" });
            _keybindings.Add(new ActionKeyBinding { Id = "cycleCbChannel", Label = "Cycle CB Radio Channel", Category = "CB Radio", BoundKey = Keys.OemCloseBrackets, Description = "Step through CB channels 19, 9, 17, 21, 25, 30" });
            _keybindings.Add(new ActionKeyBinding { Id = "quickDispatchCheck", Label = "Quick Ch 30 Dispatch Check-In", Category = "Dispatcher", BoundKey = Keys.F10, Description = "Request immediate ETA & cargo check from Sarah" });
            _keybindings.Add(new ActionKeyBinding { Id = "quickFuelReport", Label = "Voice Fuel & Weight Range Report", Category = "Advisor", BoundKey = Keys.F7, Description = "Speak remaining fuel range and Denver margin" });
            _keybindings.Add(new ActionKeyBinding { Id = "acknowledgeAlert", Label = "Acknowledge / Silence Active Alert", Category = "Audio", BoundKey = Keys.OemPipe, Description = "Immediately silence current spoken warning" });
            _keybindings.Add(new ActionKeyBinding { Id = "emergencyMute", Label = "Master Audio Mute / Unmute", Category = "Audio", BoundKey = Keys.F11, Description = "Mute or unmute all spoken & CB radio audio" });
        }

        private string GetKeybindingsFilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "keybindings.ini");
        }

        private void LoadSavedKeybindings()
        {
            try
            {
                string path = GetKeybindingsFilePath();
                if (!File.Exists(path)) return;
                foreach (string line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("=")) continue;
                    string[] parts = line.Split(new char[] { '=' }, 2);
                    if (parts[0] == "pttHoldMode")
                    {
                        _pttHoldMode = parts[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    int vk;
                    if (int.TryParse(parts[1].Trim(), out vk))
                    {
                        var kb = _keybindings.Find(x => x.Id == parts[0].Trim());
                        if (kb != null) kb.BoundKey = (Keys)vk;
                    }
                }
            }
            catch { }
        }

        private void SaveKeybindings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("pttHoldMode=" + (_pttHoldMode ? "true" : "false"));
                foreach (var kb in _keybindings)
                    sb.AppendLine(kb.Id + "=" + (int)kb.BoundKey);
                File.WriteAllText(GetKeybindingsFilePath(), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void RefreshKeybindingsUi()
        {
            if (_lvKeybindings == null) return;
            _lvKeybindings.Items.Clear();
            foreach (var kb in _keybindings)
            {
                string keyText = (_listeningActionId == kb.Id) ? ">>> PRESS KEY <<<" : kb.DisplayKey;
                var item = new ListViewItem(new string[] { kb.Label, kb.Category, keyText, kb.Description });
                item.Tag = kb;
                if (_listeningActionId == kb.Id)
                    item.ForeColor = Color.FromArgb(245, 158, 11);
                _lvKeybindings.Items.Add(item);
            }

            var ptt = _keybindings.Find(k => k.Id == "pushToTalk");
            string pttDisp = ptt != null ? ptt.DisplayKey : "LEFT ALT";
            string verb = _pttHoldMode ? "HOLD" : "TOGGLE";
            if (_btnPttTop != null) _btnPttTop.Text = "🎙️ PTT (" + pttDisp + ")";
            if (_btnMainCbPtt != null) _btnMainCbPtt.Text = "🎙️ " + verb + " PUSH-TO-TALK (KEY: " + pttDisp + ")";
        }

        private void BeginListeningForKeybind(string actionId)
        {
            _listeningActionId = actionId;
            var kb = _keybindings.Find(k => k.Id == actionId);
            if (kb != null && _lblKeybindStatus != null)
            {
                _lblKeybindStatus.Text = "LISTENING: Press any key now to bind '" + kb.Label + "' (or press Esc to cancel)...";
                _lblKeybindStatus.ForeColor = Color.FromArgb(245, 158, 11);
            }
            RefreshKeybindingsUi();
        }

        private void ApplyCapturedKey(Keys newKey)
        {
            if (string.IsNullOrEmpty(_listeningActionId)) return;
            if (newKey == Keys.Escape)
            {
                _listeningActionId = null;
                _lblKeybindStatus.Text = "Keybinding cancelled. Previous key assignments preserved.";
                _lblKeybindStatus.ForeColor = Color.FromArgb(148, 163, 184);
                RefreshKeybindingsUi();
                return;
            }

            var target = _keybindings.Find(k => k.Id == _listeningActionId);
            if (target != null)
            {
                Keys oldKey = target.BoundKey;
                var conflict = _keybindings.Find(k => k.Id != target.Id && k.BoundKey == newKey);
                target.BoundKey = newKey;
                if (conflict != null)
                {
                    conflict.BoundKey = oldKey;
                    _lblKeybindStatus.Text = "Bound '" + target.Label + "' to [" + target.DisplayKey + "] (swapped conflict with '" + conflict.Label + "').";
                }
                else
                {
                    _lblKeybindStatus.Text = "Successfully bound '" + target.Label + "' to [" + target.DisplayKey + "].";
                }
                _lblKeybindStatus.ForeColor = Color.FromArgb(52, 211, 153);
            }

            _listeningActionId = null;
            SaveKeybindings();
            RefreshKeybindingsUi();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!string.IsNullOrEmpty(_listeningActionId))
            {
                Keys baseKey = keyData & Keys.KeyCode;
                if (baseKey == Keys.Menu) baseKey = Keys.LMenu;
                ApplyCapturedKey(baseKey);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void InstallGlobalKeyboardHook()
        {
            try
            {
                using (var curProc = Process.GetCurrentProcess())
                using (var curMod = curProc.MainModule)
                {
                    if (curMod != null)
                        _kbHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _kbHookProc, GetModuleHandle(curMod.ModuleName), 0);
                }
            }
            catch { }
        }

        private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && string.IsNullOrEmpty(_listeningActionId))
            {
                int vkCode = Marshal.ReadInt32(lParam);
                Keys key = (Keys)vkCode;
                bool isDown = (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN);
                bool isUp = (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP);

                foreach (var kb in _keybindings)
                {
                    if (MatchesBoundKey(kb.BoundKey, key))
                    {
                        BeginInvoke(new Action(() => ExecuteBoundAction(kb.Id, isDown, isUp)));
                        break;
                    }
                }
            }
            return CallNextHookEx(_kbHookId, nCode, wParam, lParam);
        }

        private static bool MatchesBoundKey(Keys bound, Keys actual)
        {
            if (bound == actual) return true;
            if ((bound == Keys.Menu || bound == Keys.LMenu) && (actual == Keys.LMenu || actual == Keys.Menu)) return true;
            if ((bound == Keys.ControlKey || bound == Keys.LControlKey) && (actual == Keys.LControlKey || actual == Keys.ControlKey)) return true;
            if ((bound == Keys.ShiftKey || bound == Keys.LShiftKey) && (actual == Keys.LShiftKey || actual == Keys.ShiftKey)) return true;
            return false;
        }

        private void ExecuteBoundAction(string actionId, bool isDown, bool isUp)
        {
            if (_txtCbInput != null && _txtCbInput.Focused && actionId != "pushToTalk") return;

            if (actionId == "pushToTalk")
            {
                if (_pttHoldMode)
                {
                    if (isDown && !_pttTransmitting)
                    {
                        _pttTransmitting = true;
                        _lblCbLcdHeader.Text = "CHANNEL " + _activeCbChannel + " — TX TRANSMITTING (MIC ACTIVE)";
                    }
                    else if (isUp && _pttTransmitting)
                    {
                        _pttTransmitting = false;
                        _lblCbLcdHeader.Text = "CHANNEL " + _activeCbChannel + " — HIGHWAY TRAFFIC   |   SIGNAL: ███████░░░   |   STATE: RADIO_IDLE";
                        TransmitCbMessage(_txtCbInput != null && !string.IsNullOrWhiteSpace(_txtCbInput.Text)
                            ? _txtCbInput.Text
                            : "Breaker 1-9, Highway Ghost checking in along I-40. Anybody got a copy?");
                    }
                }
                else if (isDown)
                {
                    _pttTransmitting = !_pttTransmitting;
                    if (_pttTransmitting)
                    {
                        _lblCbLcdHeader.Text = "CHANNEL " + _activeCbChannel + " — TX LATCHED (PRESS PTT AGAIN TO SEND)";
                    }
                    else
                    {
                        _lblCbLcdHeader.Text = "CHANNEL " + _activeCbChannel + " — HIGHWAY TRAFFIC   |   SIGNAL: ███████░░░   |   STATE: RADIO_IDLE";
                        TransmitCbMessage(_txtCbInput != null && !string.IsNullOrWhiteSpace(_txtCbInput.Text)
                            ? _txtCbInput.Text
                            : "Breaker 1-9, Highway Ghost checking in along I-40. Anybody got a copy?");
                    }
                }
                return;
            }

            if (!isDown) return;

            if (actionId == "toggleOverlay")
            {
                ToggleHudOverlay();
            }
            else if (actionId == "repeatAdvisory")
            {
                SpeakAsync("Repeating last advisory: " + _lastSpokenMessage);
            }
            else if (actionId == "cycleCbChannel")
            {
                int[] order = new int[] { 19, 9, 17, 21, 25, 30 };
                int idx = Array.IndexOf(order, _activeCbChannel);
                int nextCh = order[(idx + 1) % order.Length];
                SwitchCbChannel(nextCh);
            }
            else if (actionId == "quickDispatchCheck")
            {
                string msg = "Dispatch to Highway Ghost on Channel 30: You are 218 miles out from Denver with " + _trkFuel.Value + " percent fuel remaining and zero cargo damage.";
                _lstDispatcherLog.Items.Insert(0, "[CH 30 HOTKEY] Sarah (AHL Dispatch): \"" + msg + "\"");
                LogCoachAndSpeak(msg, true);
            }
            else if (actionId == "quickFuelReport")
            {
                float range = (_trkFuel.Value / 100f) * 240f * 6.3f;
                string msg = "Fuel Advisor: Estimated range is " + range.ToString("F0") + " miles at 6.3 MPG, easily covering 218 miles remaining to Denver.";
                LogCoachAndSpeak(msg, true);
            }
            else if (actionId == "acknowledgeAlert")
            {
                try { _synth.SpeakAsyncCancelAll(); } catch { }
            }
            else if (actionId == "emergencyMute")
            {
                _masterAudioMuted = !_masterAudioMuted;
                if (_masterAudioMuted)
                {
                    try { _synth.SpeakAsyncCancelAll(); } catch { }
                }
            }
        }

        private void ToggleHudOverlay()
        {
            if (_hudOverlay.Visible) _hudOverlay.Hide();
            else _hudOverlay.Show();
        }

        private void SwitchCbChannel(int chNum)
        {
            _activeCbChannel = chNum;
            _lblCbLcdHeader.Text = "CHANNEL " + chNum + " — ACTIVE   |   SIGNAL: ████████░░   |   STATE: RADIO_IDLE";
            RefreshTelemetryUi();
        }

        private void DownloadAndInstallGithubPlugins()
        {
            // Pinned to the plugin layout TruckingAdvisor's offsets were built for. Never "latest".
            const string tag = "V.1.12.1";
            const string api = "https://api.github.com/repos/RenCloud/scs-sdk-plugin/releases/tags/" + tag;
            _txtPluginInstallLog.AppendText("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] Downloading RenCloud/scs-sdk-plugin " + tag + "...");
            Application.DoEvents();
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                string localPluginsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TruckingAdvisor", "Plugins");
                Directory.CreateDirectory(localPluginsDir);
                string targetDll = Path.Combine(localPluginsDir, "scs-telemetry.dll");

                byte[] asset;
                string assetUrl;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "TruckingAdvisor/2.2");
                    string json = wc.DownloadString(api);
                    Match mUrl = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.(zip|dll))\"");
                    if (!mUrl.Success) throw new InvalidOperationException("No .zip or .dll asset in release " + tag);
                    assetUrl = mUrl.Groups[1].Value;
                    asset = wc.DownloadData(assetUrl);
                }

                string sha;
                using (var hasher = System.Security.Cryptography.SHA256.Create())
                    sha = BitConverter.ToString(hasher.ComputeHash(asset)).Replace("-", "").ToLowerInvariant();
                if (MessageBox.Show("Downloaded " + Path.GetFileName(assetUrl) + "\nSHA-256: " + sha +
                        "\n\nCompare with https://github.com/RenCloud/scs-sdk-plugin/releases/tag/" + tag + " if you want to verify it.\n\nInstall it?",
                        "Confirm Telemetry Plugin", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    _txtPluginInstallLog.AppendText("\r\nCancelled. Nothing was installed.");
                    return;
                }

                if (assetUrl.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    File.WriteAllBytes(targetDll, asset);
                }
                else
                {
                    using (var zip = new ZipArchive(new MemoryStream(asset), ZipArchiveMode.Read))
                    {
                        ZipArchiveEntry pick = null;
                        foreach (var entry in zip.Entries)
                        {
                            if (!entry.Name.Equals("scs-telemetry.dll", StringComparison.OrdinalIgnoreCase)) continue;
                            if (Regex.IsMatch(entry.FullName, "(x64|win64|win_x64)", RegexOptions.IgnoreCase)) { pick = entry; break; }
                        }
                        if (pick == null) throw new InvalidOperationException("No 64-bit scs-telemetry.dll inside the release archive.");
                        pick.ExtractToFile(targetDll, true); // fixed destination, never a path from the archive
                    }
                }

                int installed = 0;
                foreach (string lib in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\SteamLibrary", @"E:\SteamLibrary" })
                {
                    foreach (string game in new[] { "American Truck Simulator", "Euro Truck Simulator 2" })
                    {
                        string binDir = Path.Combine(lib, "steamapps", "common", game, "bin", "win_x64");
                        if (!Directory.Exists(binDir)) continue;
                        string plugDir = Path.Combine(binDir, "plugins");
                        string dest = Path.Combine(plugDir, "scs-telemetry.dll");
                        try
                        {
                            Directory.CreateDirectory(plugDir);
                            if (File.Exists(dest)) File.Copy(dest, dest + ".bak-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                            File.Copy(targetDll, dest, true);
                            installed++;
                            _txtPluginInstallLog.AppendText("\r\nInstalled -> " + dest);
                        }
                        catch (UnauthorizedAccessException)
                        {
                            _txtPluginInstallLog.AppendText("\r\nPermission denied: " + plugDir + " (run as administrator once, or copy " + targetDll + " there by hand)");
                        }
                    }
                }
                _lblPluginStatusBadge.Text = installed > 0
                    ? "● PLUGIN " + tag + " INSTALLED (" + installed + " game folder(s))"
                    : "● PLUGIN DOWNLOADED - copy " + targetDll + " into <game>\\bin\\win_x64\\plugins";
            }
            catch (Exception ex)
            {
                _txtPluginInstallLog.AppendText("\r\nPlugin install failed: " + ex.Message + ". Nothing was installed.");
                _lblPluginStatusBadge.Text = "● PLUGIN NOT INSTALLED";
            }
        }

        private TrackBar CreateSlider(Panel parent, string label, int x, int min, int max, int val)
        {
            var lbl = new Label
            {
                Text = label,
                Location = new Point(x, 4),
                AutoSize = true,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            parent.Controls.Add(lbl);

            var trk = new TrackBar
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Location = new Point(x, 20),
                Size = new Size(210, 32),
                TickStyle = TickStyle.None
            };
            trk.Scroll += (s, e) => RefreshTelemetryUi();
            parent.Controls.Add(trk);
            return trk;
        }

        private Button CreateCbPresetButton(Panel parent, string label, int y, string payload)
        {
            var btn = new Button
            {
                Text = label,
                Location = new Point(16, y),
                Size = new Size(480, 32),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft
            };
            btn.Click += (s, e) => TransmitCbMessage(payload);
            parent.Controls.Add(btn);
            return btn;
        }

        private void SeedRoster()
        {
            _roster.Add(new TruckerProfile
            {
                Handle = "Big Red", FullName = "Earl 'Big Red' Callahan", HomeState = "Texas", Truck = "Peterbilt 389", Years = 35,
                Relationship = "Lv 4 • Road Buddy", Level = 4, Chats = 14, Region = "I-40 / US-160 Corridor",
                Memories = new List<string> { "Talked with Highway Ghost climbing out of Flagstaff.", "Warned about keeping Jake brakes cool into Denver.", "Mentioned Diesel Dave's Cascadia had an EGR sensor issue in Ohio." }
            });
            _roster.Add(new TruckerProfile
            {
                Handle = "Highway Hank", FullName = "Hank Mercer", HomeState = "Tennessee", Truck = "Kenworth W900", Years = 22,
                Relationship = "Lv 3 • Road Acquaintance", Level = 3, Chats = 8, Region = "I-70 High Plains",
                Memories = new List<string> { "Recommended the green chile skillet at the Trinidad truck stop." }
            });
            _roster.Add(new TruckerProfile
            {
                Handle = "Diesel Dave", FullName = "Dave Kowalski", HomeState = "Ohio", Truck = "Freightliner Cascadia", Years = 18,
                Relationship = "Lv 2 • Familiar Voice", Level = 2, Chats = 6, Region = "I-25 Front Range",
                Memories = new List<string> { "Sorted out a sticky turbo actuator on his Cascadia in Ohio." }
            });
            _roster.Add(new TruckerProfile
            {
                Handle = "Midnight Mike", FullName = "Michael Vance", HomeState = "Nevada", Truck = "Volvo VNL 860", Years = 12,
                Relationship = "Lv 3 • Road Acquaintance", Level = 3, Chats = 5, Region = "US-89 Desert Basin",
                Memories = new List<string> { "Checked in around 02:00 AM crossing the Mojave desert." }
            });
            _roster.Add(new TruckerProfile
            {
                Handle = "Roadrunner Rachel", FullName = "Rachel Delgado", HomeState = "Arizona", Truck = "Peterbilt 579", Years = 14,
                Relationship = "Lv 4 • Road Buddy", Level = 4, Chats = 11, Region = "I-17 / I-40 Flagstaff",
                Memories = new List<string> { "Praised Highway Ghost's 14-trip zero-damage streak." }
            });
            _activeTrucker = _roster[0];
        }

        private void PopulateTruckerListView()
        {
            _lvTruckers.Items.Clear();
            foreach (var t in _roster)
            {
                var item = new ListViewItem(new string[] { t.Handle, t.FullName, t.Truck, t.HomeState, t.Relationship });
                item.Tag = t;
                _lvTruckers.Items.Add(item);
            }
            if (_lvTruckers.Items.Count > 0)
                _lvTruckers.Items[0].Selected = true;
        }

        private void OnTruckerSelected(object sender, EventArgs e)
        {
            if (_lvTruckers.SelectedItems.Count == 0) return;
            _activeTrucker = (TruckerProfile)_lvTruckers.SelectedItems[0].Tag;
            var sb = new StringBuilder();
            sb.AppendLine("CB HANDLE:    " + _activeTrucker.Handle);
            sb.AppendLine("FULL NAME:    " + _activeTrucker.FullName);
            sb.AppendLine("RIG & HOME:   " + _activeTrucker.Truck + " (" + _activeTrucker.HomeState + " • " + _activeTrucker.Years + " yrs)");
            sb.AppendLine("RELATIONSHIP: " + _activeTrucker.Relationship + " (" + _activeTrucker.Chats + " conversations)");
            sb.AppendLine("REGION:       " + _activeTrucker.Region);
            sb.AppendLine("\r\nPERSISTENT EPISODIC MEMORIES:");
            foreach (var m in _activeTrucker.Memories)
                sb.AppendLine("  • " + m);
            _txtTruckerBio.Text = sb.ToString();
        }

        private void OnScenarioChanged(object sender, EventArgs e)
        {
            switch (_cmbScenario.SelectedIndex)
            {
                case 0:
                    _trkSpeed.Value = 65; _trkLimit.Value = 70; _trkRpm.Value = 1420; _trkFuel.Value = 68; _trkAir.Value = 118;
                    break;
                case 1:
                    _trkSpeed.Value = 74; _trkLimit.Value = 70; _speedIncidents++;
                    LogCoachAndSpeak("GENTLE ADVISORY: Easy there, driver. You're creeping about 4 over the posted 70 limit.", false);
                    break;
                case 2:
                    _trkSpeed.Value = 78; _trkLimit.Value = 70; _speedIncidents++;
                    LogCoachAndSpeak("MODERATE WARNING: Easy there, driver. You're running about 8 over the limit.", true);
                    break;
                case 3:
                    _trkSpeed.Value = 84; _trkLimit.Value = 70; _speedIncidents++;
                    LogCoachAndSpeak("SERIOUS WARNING: Slow it down, driver! You're 14 MPH over the 70 MPH limit.", true);
                    break;
                case 4:
                    _trkSpeed.Value = 58; _trkLimit.Value = 60; _trkRpm.Value = 1980;
                    LogCoachAndSpeak("MOUNTAIN GRADE: High RPMs (1,980 RPM) on -6.5% descent. Engage Jake Brake Stage 3.", true);
                    break;
                case 5:
                    _trkFuel.Value = 18;
                    LogCoachAndSpeak("FUEL ADVISOR: You're down to about a fifth of a tank. Might want to start thinking about diesel.", true);
                    break;
                case 6:
                    _trkAir.Value = 56;
                    LogCoachAndSpeak("CRITICAL ALERT: Brake air pressure is critically low at 56 PSI! Stop safely immediately!", true);
                    break;
                case 7:
                    _trkSpeed.Value = 0; _trkRpm.Value = 650;
                    LogCoachAndSpeak("TRUCK STOP MODE: Parked safely. Channel 25 Truck Stop conversations ready.", false);
                    break;
            }
            RefreshTelemetryUi();
        }

        private void RefreshTelemetryUi()
        {
            float spd = _trkSpeed.Value;
            float lim = _trkLimit.Value;
            float rpm = _trkRpm.Value;
            float fuel = _trkFuel.Value;
            float air = _trkAir.Value;
            float range = (fuel / 100f) * 240f * 6.3f;

            int score = Math.Max(50, 100 - _speedIncidents * 2 - _brakeIncidents * 3);
            string rating = score >= 95 ? "LEGENDARY TRUCKER" : score >= 90 ? "PROFESSIONAL DRIVER" : "EXPERIENCED DRIVER";

            _gauges.SpeedMph = spd;
            _gauges.SpeedLimitMph = lim;
            _gauges.EngineRpm = rpm;
            _gauges.Gear = spd == 0 ? "N" : "16H";
            _gauges.FuelPercent = fuel;
            _gauges.RangeMiles = range;
            _gauges.AirPsi = air;
            _gauges.DriverScore = score;
            _gauges.ScoreRating = rating;
            _gauges.Invalidate();

            _hudOverlay.UpdateHud(spd, lim, fuel, range, score, _activeCbChannel, _activeTrucker.Handle);

            _txtDeliveryReport.Text =
                "TRUCKINGADVISOR — OFFICIAL DELIVERY REPORT & VIRTUAL COMPANY SUMMARY\r\n" +
                "======================================================================\r\n" +
                "Cargo: Construction Equipment (40,000 lbs)\r\n" +
                "Origin: Phoenix, AZ  ->  Destination: Denver, CO\r\n" +
                "Distance: 426 Miles  |  Driving Time: 7h 12m\r\n" +
                "Avg Speed: 59 MPH    |  Current Speed: " + spd + " MPH (Limit: " + lim + " MPH)\r\n" +
                "Fuel Remaining: " + fuel + "%  |  Economy: 6.3 MPG (Range: " + range.ToString("F0") + " MI)\r\n" +
                "Speeding Incidents: " + _speedIncidents + " |  Harsh Braking: " + _brakeIncidents + "\r\n" +
                "Truck Damage: 0.0%   |  Trailer Damage: 0.0%\r\n\r\n" +
                "FINAL DRIVER SCORE: " + score + "/100\r\n" +
                "RATING: " + rating + "\r\n\r\n" +
                "VIRTUAL TRUCKING COMPANY: AMERICAN HIGHWAY LOGISTICS (HQ: Dallas, TX)\r\n" +
                "Owner-Operator: Highway Ghost  |  Career Rank: Level 22 Professional Operator\r\n" +
                "Lifetime Miles: 84,521 mi      |  Completed Deliveries: 147\r\n" +
                "Recorded Revenue: $2,450,000   |  Damage-Free Rate: 95.9%";
        }

        private void TransmitCbMessage(string text)
        {
            if (text == "[DYNAMIC_ENCOUNTER]")
            {
                string enc = _trkFuel.Value <= 25
                    ? "Highway Ghost, this is " + _activeTrucker.Handle + ". Looks like you're stretching that fuel tank—planning on stopping for diesel soon?"
                    : "Breaker 1-9, this is " + _activeTrucker.Handle + ". That's a serious 40,000 lb load of Construction Equipment you're pulling toward Denver!";
                _lstCbTranscript.Items.Insert(0, "[CH " + _activeCbChannel + "] " + _activeTrucker.Handle + ": \"" + enc + "\"");
                _lblCbLcdSub.Text = "ACTIVE SPEAKER: " + _activeTrucker.Handle.ToUpperInvariant() + "\r\n\"" + enc + "\"";
                SpeakAsync(enc);
                return;
            }
            if (text == "[AI_TO_AI]")
            {
                _lstCbTranscript.Items.Insert(0, "[CH 19] Big Red: \"Breaker one-nine, anybody running westbound toward Flagstaff?\"");
                _lstCbTranscript.Items.Insert(0, "[CH 19] Diesel Dave: \"Yeah, Big Red, I'm headed that direction. Smooth rolling where I'm at.\"");
                SpeakAsync("Yeah Big Red, I am headed that direction. Smooth rolling where I am at.");
                return;
            }

            _lstCbTranscript.Items.Insert(0, "[CH " + _activeCbChannel + "] Highway Ghost (You): \"" + text + "\"");
            string reply;
            string lower = text.ToLowerInvariant();
            if (lower.Contains("dave") || lower.Contains("trouble"))
                reply = "Last I heard from Diesel Dave, he was running freight up through Ohio and got that Cascadia sensor sorted out.";
            else if (lower.Contains("denver") || lower.Contains("40,000") || lower.Contains("road"))
                reply = "Haven't heard anything too wild up toward Denver, driver. Take your time climbing those grades with 40,000 pounds behind you.";
            else
                reply = "Yeah, driver, I gotcha loud and clear. This is " + _activeTrucker.Handle + " rolling in the " + _activeTrucker.Truck + ". Keep the shiny side up!";

            _lstCbTranscript.Items.Insert(0, "[CH " + _activeCbChannel + "] " + _activeTrucker.Handle + ": \"" + reply + "\"");
            _lblCbLcdSub.Text = "ACTIVE SPEAKER: " + _activeTrucker.Handle.ToUpperInvariant() + " (" + _activeTrucker.Truck + ")\r\n\"" + reply + "\"";
            _activeTrucker.Chats++;
            SpeakAsync(reply);
        }

        private void LogCoachAndSpeak(string message, bool speak)
        {
            _lastSpokenMessage = message;
            _lstCoachFeed.Items.Insert(0, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
            if (speak) SpeakAsync(message);
        }

        private void SpeakAsync(string text)
        {
            if (_masterAudioMuted) return;
            try
            {
                _synth.SpeakAsyncCancelAll();
                _synth.SpeakAsync(text);
            }
            catch { }
        }

        private void PollScsMemoryMappedFile()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting("Local\\SCSTelemetry", MemoryMappedFileRights.Read))
                {
                    // This lightweight window only detects the plugin; its gauges are the simulator sliders.
                    // Live values are shown in the full TruckingAdvisor app (Source\TruckingAdvisor).
                    _lblConnStatus.Text = "● ATS PLUGIN DETECTED - gauges here are SIMULATED (use the full app for live data)";
                    _lblConnStatus.ForeColor = Color.FromArgb(16, 185, 129);
                }
            }
            catch
            {
                _lblConnStatus.Text = "● SIMULATION ONLY - ATS telemetry plugin not detected";
                _lblConnStatus.ForeColor = Color.FromArgb(245, 158, 11);
            }
        }

        private static void LaunchHtmlCompanion()
        {
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "App", "TruckingAdvisor_App.html");
            if (!File.Exists(htmlPath)) return;
            try
            {
                Process.Start(new ProcessStartInfo("msedge.exe", "--app=\"file:///" + htmlPath.Replace('\\', '/') + "\" --window-size=1440,900") { UseShellExecute = true });
            }
            catch
            {
                Process.Start(new ProcessStartInfo(htmlPath) { UseShellExecute = true });
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (_kbHookId != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_kbHookId); } catch { }
                _kbHookId = IntPtr.Zero;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnFormClosed(e);
        }
    }
}
