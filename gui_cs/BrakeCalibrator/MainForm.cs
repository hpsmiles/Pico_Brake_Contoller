using ScottPlot;
using SDColor = System.Drawing.Color;
using SDFontStyle = System.Drawing.FontStyle;
using SWFOrientation = System.Windows.Forms.Orientation;
using WinFormsLabel = System.Windows.Forms.Label;

namespace BrakeCalibrator;

public class MainForm : Form
{
    // ── Core state ──
    private readonly CalibrationData _cal = CalibrationData.CreateDefaults();
    private readonly HidReader _hidReader = new();
    private ProfileManager? _profileManager;

    // ── History ──
    private const int HistoryLength = 200;
    private readonly List<double> _rawHistory = new();
    private readonly List<double> _previewHistory = new();
    private readonly List<double> _gameInputHistory = new();
    private readonly List<double> _throttleHistory = new();

    // ── Auto-calibration state machine ──
    private enum AutoCalPhase { Idle, Countdown, Capture, Done }
    private AutoCalPhase _calPhase = AutoCalPhase.Idle;
    private DateTime _phaseStart;
    private readonly List<int> _captureSamples = new();
    private int _calTarget; // 0=brake, 1=throttle

    // ── Poll timer ──
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = 33 }; // ~30Hz

    // ── UI controls ──
    private readonly ScottPlot.WinForms.FormsPlot _formsPlot;
    private readonly ComboBox _deviceCombo;
    private readonly WinFormsLabel _rawAdcLabel;
    private readonly WinFormsLabel _brakeLabel;
    private readonly WinFormsLabel _throttleLabel;
    private readonly WinFormsLabel _statusLabel;

    // Auto-cal
    private readonly Button _autoCalButton;
    private readonly WinFormsLabel _autoCalLabel;

    // Profiles
    private readonly ComboBox _profileCombo;
    private readonly Button _profileSaveButton;
    private readonly Button _profileLoadButton;
    private readonly Button _profileDeleteButton;

    // Throttle group
    private readonly CheckBox _throttleEnableCheck;
    private readonly NumericUpDown _throttleRawMinNum;
    private readonly NumericUpDown _throttleRawMaxNum;
    private readonly Button _throttleRawMinSetButton;
    private readonly Button _throttleRawMaxSetButton;
    private readonly ComboBox _throttleCurveCombo;
    private readonly Button _throttleCurveEditButton;
    private readonly TrackBar _throttleSmoothingTrack;
    private readonly WinFormsLabel _throttleSmoothingLabel;
    private readonly TrackBar _throttleDeadzoneTrack;
    private readonly WinFormsLabel _throttleDeadzoneLabel;
    private readonly CheckBox _throttleInvertCheck;
    private readonly TrackBar _throttleSaturationTrack;
    private readonly WinFormsLabel _throttleSaturationLabel;
    private readonly TrackBar _throttleBitePointTrack;
    private readonly WinFormsLabel _throttleBitePointLabel;
    private readonly TrackBar _throttleProgressivePowerTrack;
    private readonly WinFormsLabel _throttleProgressivePowerLabel;
    private readonly TrackBar _throttleAggressivePowerTrack;
    private readonly WinFormsLabel _throttleAggressivePowerLabel;
    private readonly Panel _throttlePanel;

    // Brake group
    private readonly NumericUpDown _brakeRawMinNum;
    private readonly NumericUpDown _brakeRawMaxNum;
    private readonly Button _brakeRawMinSetButton;
    private readonly Button _brakeRawMaxSetButton;
    private readonly ComboBox _brakeCurveCombo;
    private readonly Button _brakeCurveEditButton;
    private readonly TrackBar _brakeSmoothingTrack;
    private readonly WinFormsLabel _brakeSmoothingLabel;
    private readonly TrackBar _brakeDeadzoneTrack;
    private readonly WinFormsLabel _brakeDeadzoneLabel;
    private readonly CheckBox _brakeInvertCheck;
    private readonly TrackBar _brakeSaturationTrack;
    private readonly WinFormsLabel _brakeSaturationLabel;
    private readonly TrackBar _brakeBitePointTrack;
    private readonly WinFormsLabel _brakeBitePointLabel;
    private readonly ComboBox _brakeOversampleCombo;
    private readonly TrackBar _brakeProgressivePowerTrack;
    private readonly WinFormsLabel _brakeProgressivePowerLabel;
    private readonly TrackBar _brakeAggressivePowerTrack;
    private readonly WinFormsLabel _brakeAggressivePowerLabel;

    // Save
    private readonly Button _saveToPicoButton;

    public MainForm()
    {
        Text = "Brake Controller Calibrator";
        Size = new Size(1280, 780);
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterScreen;

        // ── Main layout: SplitContainer ──
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = SWFOrientation.Vertical,
            SplitterDistance = 780,
            SplitterWidth = 6,
            BackColor = SDColor.FromArgb(45, 45, 45),
            Panel1MinSize = 500,
            Panel2MinSize = 280,
        };

        // ══════════════════════════════════════════════════════════════════
        // LEFT PANEL: Graph + info labels + device selector
        // ══════════════════════════════════════════════════════════════════
        var leftPanel = split.Panel1;
        leftPanel.BackColor = SDColor.FromArgb(30, 30, 30);

        // FormsPlot
        _formsPlot = new ScottPlot.WinForms.FormsPlot { Dock = DockStyle.Fill };
        SetupGraph();

        // Info bar at bottom of left panel
        var infoPanel = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = SDColor.FromArgb(40, 40, 40) };

        _rawAdcLabel = new WinFormsLabel { Text = "Raw ADC: ---", Left = 12, Top = 6, AutoSize = true, ForeColor = SDColor.FromArgb(68, 136, 255), Font = new Font("Consolas", 9F) };
        _brakeLabel = new WinFormsLabel { Text = "Brake: ---", Left = 200, Top = 6, AutoSize = true, ForeColor = SDColor.FromArgb(255, 68, 68), Font = new Font("Consolas", 9F) };
        _throttleLabel = new WinFormsLabel { Text = "Throttle: ---", Left = 380, Top = 6, AutoSize = true, ForeColor = SDColor.FromArgb(255, 136, 0), Font = new Font("Consolas", 9F) };

        _deviceCombo = new ComboBox { Left = 12, Top = 30, Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
        _deviceCombo.SelectedIndexChanged += DeviceCombo_SelectedIndexChanged;

        _statusLabel = new WinFormsLabel { Text = "Status: No Pico drive found", Left = 330, Top = 33, AutoSize = true, ForeColor = SDColor.LightGray, Font = new Font("Segoe UI", 8F) };

        infoPanel.Controls.AddRange(new Control[] { _rawAdcLabel, _brakeLabel, _throttleLabel, _deviceCombo, _statusLabel });
        leftPanel.Controls.Add(_formsPlot);
        leftPanel.Controls.Add(infoPanel);

        // ══════════════════════════════════════════════════════════════════
        // RIGHT PANEL: Scrollable controls
        // ══════════════════════════════════════════════════════════════════
        var rightOuter = split.Panel2;
        rightOuter.BackColor = SDColor.FromArgb(45, 45, 45);

        var rightScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = SDColor.FromArgb(45, 45, 45) };
        var rightContent = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(6),
            BackColor = SDColor.FromArgb(45, 45, 45),
        };

        // ── Auto Calibrate group ──
        var autoCalGroup = CreateGroupBox("Auto Calibrate");
        _autoCalButton = new Button { Text = "Start Auto Calibration", Width = 190, Height = 26 };
        _autoCalButton.Click += AutoCalButton_Click;
        _autoCalLabel = new WinFormsLabel { Text = "Press button to start", AutoSize = true, ForeColor = SDColor.LightGray };
        autoCalGroup.Controls.AddRange(new Control[] { _autoCalButton, _autoCalLabel });
        rightContent.Controls.Add(autoCalGroup);

        // ── Profiles group ──
        var profilesGroup = CreateGroupBox("Profiles");
        _profileCombo = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        _profileSaveButton = new Button { Text = "Save", Width = 60, Height = 24 };
        _profileLoadButton = new Button { Text = "Load", Width = 60, Height = 24 };
        _profileDeleteButton = new Button { Text = "Delete", Width = 60, Height = 24 };
        _profileSaveButton.Click += ProfileSave_Click;
        _profileLoadButton.Click += ProfileLoad_Click;
        _profileDeleteButton.Click += ProfileDelete_Click;
        var profileRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        profileRow.Controls.AddRange(new Control[] { _profileCombo, _profileSaveButton, _profileLoadButton, _profileDeleteButton });
        profilesGroup.Controls.Add(profileRow);
        rightContent.Controls.Add(profilesGroup);

        // ── Throttle group ──
        var throttleGroup = CreateGroupBox("Throttle");
        _throttleEnableCheck = new CheckBox { Text = "Enable Throttle", AutoSize = true, ForeColor = SDColor.White };
        _throttleEnableCheck.CheckedChanged += ThrottleEnableCheck_CheckedChanged;

        // Raw min/max
        var thrRawPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        thrRawPanel.Controls.Add(new WinFormsLabel { Text = "Min:", AutoSize = true, ForeColor = SDColor.LightGray });
        _throttleRawMinNum = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = 2000, Width = 72 };
        thrRawPanel.Controls.Add(_throttleRawMinNum);
        _throttleRawMinSetButton = new Button { Text = "Set", Width = 38, Height = 22 };
        _throttleRawMinSetButton.Click += (_, _) => { _throttleRawMinNum.Value = _hidReader.ReadThrottleRawInt(); };
        thrRawPanel.Controls.Add(_throttleRawMinSetButton);
        thrRawPanel.Controls.Add(new WinFormsLabel { Text = " Max:", AutoSize = true, ForeColor = SDColor.LightGray });
        _throttleRawMaxNum = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = 56000, Width = 72 };
        thrRawPanel.Controls.Add(_throttleRawMaxNum);
        _throttleRawMaxSetButton = new Button { Text = "Set", Width = 38, Height = 22 };
        _throttleRawMaxSetButton.Click += (_, _) => { _throttleRawMaxNum.Value = _hidReader.ReadThrottleRawInt(); };
        thrRawPanel.Controls.Add(_throttleRawMaxSetButton);

        // Curve + Edit
        var thrCurvePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        thrCurvePanel.Controls.Add(new WinFormsLabel { Text = "Curve:", AutoSize = true, ForeColor = SDColor.LightGray });
        _throttleCurveCombo = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        _throttleCurveCombo.Items.AddRange(CalibrationData.CurveTypes);
        _throttleCurveCombo.SelectedIndex = 0;
        thrCurvePanel.Controls.Add(_throttleCurveCombo);
        _throttleCurveEditButton = new Button { Text = "Edit...", Width = 58, Height = 22 };
        _throttleCurveEditButton.Click += (_, _) => ShowCurveEditor("throttle");
        thrCurvePanel.Controls.Add(_throttleCurveEditButton);

        // Progressive/Aggressive power
        var thrProgPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        thrProgPanel.Controls.Add(new WinFormsLabel { Text = "ProgPwr:", AutoSize = true, ForeColor = SDColor.LightGray });
        _throttleProgressivePowerTrack = new TrackBar { Width = 110, Minimum = 11, Maximum = 50, Value = 20, TickFrequency = 5 };
        _throttleProgressivePowerLabel = new WinFormsLabel { Text = "2.0", AutoSize = true, ForeColor = SDColor.LightGray };
        _throttleProgressivePowerTrack.ValueChanged += (_, _) =>
            _throttleProgressivePowerLabel.Text = (_throttleProgressivePowerTrack.Value / 10.0).ToString("F1");
        thrProgPanel.Controls.Add(_throttleProgressivePowerTrack);
        thrProgPanel.Controls.Add(_throttleProgressivePowerLabel);

        var thrAggPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        thrAggPanel.Controls.Add(new WinFormsLabel { Text = "AggPwr:", AutoSize = true, ForeColor = SDColor.LightGray });
        _throttleAggressivePowerTrack = new TrackBar { Width = 110, Minimum = 11, Maximum = 50, Value = 20, TickFrequency = 5 };
        _throttleAggressivePowerLabel = new WinFormsLabel { Text = "2.0", AutoSize = true, ForeColor = SDColor.LightGray };
        _throttleAggressivePowerTrack.ValueChanged += (_, _) =>
            _throttleAggressivePowerLabel.Text = (_throttleAggressivePowerTrack.Value / 10.0).ToString("F1");
        thrAggPanel.Controls.Add(_throttleAggressivePowerTrack);
        thrAggPanel.Controls.Add(_throttleAggressivePowerLabel);

        // Smoothing
        var thrSmoothPanel = CreateTrackBarRow("Smoothing:", out _throttleSmoothingTrack, out _throttleSmoothingLabel, 0, 95, 20, v => (v / 100.0).ToString("F2"));
        // Deadzone
        var thrDeadPanel = CreateTrackBarRow("Deadzone:", out _throttleDeadzoneTrack, out _throttleDeadzoneLabel, 0, 1000, 300, v => v.ToString());
        // Invert
        _throttleInvertCheck = new CheckBox { Text = "Invert", AutoSize = true, ForeColor = SDColor.White };
        // Saturation
        var thrSatPanel = CreateTrackBarRow("Saturation:", out _throttleSaturationTrack, out _throttleSaturationLabel, 10, 100, 100, v => (v / 100.0).ToString("F2"));
        // Bite point
        var thrBitePanel = CreateTrackBarRow("Bite Point:", out _throttleBitePointTrack, out _throttleBitePointLabel, 0, 50, 0, v => (v / 100.0).ToString("F2"));

        _throttlePanel = new Panel { AutoSize = true, BackColor = SDColor.Transparent };
        _throttlePanel.Controls.AddRange(new Control[]
        {
            _throttleEnableCheck, thrRawPanel, thrCurvePanel, thrProgPanel, thrAggPanel,
            thrSmoothPanel, thrDeadPanel, _throttleInvertCheck, thrSatPanel, thrBitePanel
        });
        throttleGroup.Controls.Add(_throttlePanel);
        rightContent.Controls.Add(throttleGroup);

        // ── Brake group ──
        var brakeGroup = CreateGroupBox("Brake");

        // Raw min/max
        var brakeRawPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        brakeRawPanel.Controls.Add(new WinFormsLabel { Text = "Min:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeRawMinNum = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = 2000, Width = 72 };
        brakeRawPanel.Controls.Add(_brakeRawMinNum);
        _brakeRawMinSetButton = new Button { Text = "Set", Width = 38, Height = 22 };
        _brakeRawMinSetButton.Click += (_, _) => { _brakeRawMinNum.Value = _hidReader.ReadRawAdcInt(); };
        brakeRawPanel.Controls.Add(_brakeRawMinSetButton);
        brakeRawPanel.Controls.Add(new WinFormsLabel { Text = " Max:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeRawMaxNum = new NumericUpDown { Minimum = 0, Maximum = 65535, Value = 56000, Width = 72 };
        brakeRawPanel.Controls.Add(_brakeRawMaxNum);
        _brakeRawMaxSetButton = new Button { Text = "Set", Width = 38, Height = 22 };
        _brakeRawMaxSetButton.Click += (_, _) => { _brakeRawMaxNum.Value = _hidReader.ReadRawAdcInt(); };
        brakeRawPanel.Controls.Add(_brakeRawMaxSetButton);

        // Curve + Edit
        var brakeCurvePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        brakeCurvePanel.Controls.Add(new WinFormsLabel { Text = "Curve:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeCurveCombo = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        _brakeCurveCombo.Items.AddRange(CalibrationData.CurveTypes);
        _brakeCurveCombo.SelectedIndex = 0;
        brakeCurvePanel.Controls.Add(_brakeCurveCombo);
        _brakeCurveEditButton = new Button { Text = "Edit...", Width = 58, Height = 22 };
        _brakeCurveEditButton.Click += (_, _) => ShowCurveEditor("brake");
        brakeCurvePanel.Controls.Add(_brakeCurveEditButton);

        // Progressive/Aggressive power
        var brakeProgPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        brakeProgPanel.Controls.Add(new WinFormsLabel { Text = "ProgPwr:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeProgressivePowerTrack = new TrackBar { Width = 110, Minimum = 11, Maximum = 50, Value = 20, TickFrequency = 5 };
        _brakeProgressivePowerLabel = new WinFormsLabel { Text = "2.0", AutoSize = true, ForeColor = SDColor.LightGray };
        _brakeProgressivePowerTrack.ValueChanged += (_, _) =>
            _brakeProgressivePowerLabel.Text = (_brakeProgressivePowerTrack.Value / 10.0).ToString("F1");
        brakeProgPanel.Controls.Add(_brakeProgressivePowerTrack);
        brakeProgPanel.Controls.Add(_brakeProgressivePowerLabel);

        var brakeAggPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        brakeAggPanel.Controls.Add(new WinFormsLabel { Text = "AggPwr:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeAggressivePowerTrack = new TrackBar { Width = 110, Minimum = 11, Maximum = 50, Value = 20, TickFrequency = 5 };
        _brakeAggressivePowerLabel = new WinFormsLabel { Text = "2.0", AutoSize = true, ForeColor = SDColor.LightGray };
        _brakeAggressivePowerTrack.ValueChanged += (_, _) =>
            _brakeAggressivePowerLabel.Text = (_brakeAggressivePowerTrack.Value / 10.0).ToString("F1");
        brakeAggPanel.Controls.Add(_brakeAggressivePowerTrack);
        brakeAggPanel.Controls.Add(_brakeAggressivePowerLabel);

        // Smoothing
        var brakeSmoothPanel = CreateTrackBarRow("Smoothing:", out _brakeSmoothingTrack, out _brakeSmoothingLabel, 0, 95, 30, v => (v / 100.0).ToString("F2"));
        // Deadzone
        var brakeDeadPanel = CreateTrackBarRow("Deadzone:", out _brakeDeadzoneTrack, out _brakeDeadzoneLabel, 0, 1000, 300, v => v.ToString());
        // Invert
        _brakeInvertCheck = new CheckBox { Text = "Invert", AutoSize = true, ForeColor = SDColor.White };
        // Saturation
        var brakeSatPanel = CreateTrackBarRow("Saturation:", out _brakeSaturationTrack, out _brakeSaturationLabel, 10, 100, 100, v => (v / 100.0).ToString("F2"));
        // Bite point
        var brakeBitePanel = CreateTrackBarRow("Bite Point:", out _brakeBitePointTrack, out _brakeBitePointLabel, 0, 50, 0, v => (v / 100.0).ToString("F2"));
        // Oversample
        var brakeOversamplePanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        brakeOversamplePanel.Controls.Add(new WinFormsLabel { Text = "Oversample:", AutoSize = true, ForeColor = SDColor.LightGray });
        _brakeOversampleCombo = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
        _brakeOversampleCombo.Items.AddRange(CalibrationData.OversampleOptions.Select(o => o.ToString()).ToArray());
        _brakeOversampleCombo.SelectedIndex = 2; // 16
        brakeOversamplePanel.Controls.Add(_brakeOversampleCombo);

        brakeGroup.Controls.AddRange(new Control[]
        {
            brakeRawPanel, brakeCurvePanel, brakeProgPanel, brakeAggPanel,
            brakeSmoothPanel, brakeDeadPanel, _brakeInvertCheck, brakeSatPanel, brakeBitePanel, brakeOversamplePanel
        });
        rightContent.Controls.Add(brakeGroup);

        // ── Save to Pico ──
        _saveToPicoButton = new Button
        {
            Text = "💾 Save to Pico",
            Width = 200,
            Height = 36,
            FlatStyle = FlatStyle.Flat,
            BackColor = SDColor.FromArgb(0, 100, 180),
            ForeColor = SDColor.White,
            Font = new Font(new FontFamily("Segoe UI"), 9.5F, SDFontStyle.Bold),
        };
        _saveToPicoButton.Click += SaveToPico_Click;
        rightContent.Controls.Add(_saveToPicoButton);

        rightScroll.Controls.Add(rightContent);
        rightOuter.Controls.Add(rightScroll);

        Controls.Add(split);

        // ── Timer ──
        _pollTimer.Tick += PollTimer_Tick;

        // ── Load handler ──
        Load += MainForm_Load;
    }

    // ══════════════════════════════════════════════════════════════════
    // Graph setup
    // ══════════════════════════════════════════════════════════════════

    private void SetupGraph()
    {
        var plot = _formsPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1e1e1e");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e1e1e");
        plot.Axes.Color(ScottPlot.Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#404040");

        plot.Axes.SetLimitsY(-0.05, 1.05);
        plot.ShowLegend();
        plot.Title("Live Brake Data");
        plot.XLabel("Sample");
        plot.YLabel("Value");
    }

    // ══════════════════════════════════════════════════════════════════
    // UI helpers
    // ══════════════════════════════════════════════════════════════════

    private static GroupBox CreateGroupBox(string title)
    {
        return new GroupBox
        {
            Text = title,
            AutoSize = true,
            Width = 260,
            ForeColor = SDColor.White,
            BackColor = SDColor.FromArgb(50, 50, 50),
            Padding = new Padding(6),
            Margin = new Padding(3),
            Font = new Font(new FontFamily("Segoe UI"), 9F, SDFontStyle.Bold),
        };
    }

    private static FlowLayoutPanel CreateTrackBarRow(string label, out TrackBar trackBar, out WinFormsLabel valueLabel, int min, int max, int value, Func<int, string> formatValue)
    {
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false };
        panel.Controls.Add(new WinFormsLabel { Text = label, AutoSize = true, ForeColor = SDColor.LightGray });
        trackBar = new TrackBar { Width = 120, Minimum = min, Maximum = max, Value = value, TickFrequency = Math.Max(1, (max - min) / 10) };
        valueLabel = new WinFormsLabel { Text = formatValue(value), AutoSize = true, ForeColor = SDColor.LightGray };
        // Capture to local variables since out params can't be used in lambdas
        var tb = trackBar;
        var vl = valueLabel;
        tb.ValueChanged += (_, _) => vl.Text = formatValue(tb.Value);
        panel.Controls.Add(trackBar);
        panel.Controls.Add(valueLabel);
        return panel;
    }

    private void SetAllControlsEnabled(Control container, bool enabled)
    {
        foreach (Control c in container.Controls)
        {
            c.Enabled = enabled;
            if (c.HasChildren) SetAllControlsEnabled(c, enabled);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Load / Init
    // ══════════════════════════════════════════════════════════════════

    private void MainForm_Load(object? sender, EventArgs e)
    {
        // Find Pico drive
        string? picoDrive = PicoDriveFinder.FindDrive();
        if (picoDrive != null)
        {
            _profileManager = new ProfileManager(picoDrive);

            // Try loading existing calibration.json
            string calPath = Path.Combine(picoDrive, "calibration.json");
            if (File.Exists(calPath))
            {
                try
                {
                    string json = File.ReadAllText(calPath);
                    var loaded = CalibrationData.FromJson(json);
                    // Copy loaded values into _cal
                    CopyCalibration(loaded, _cal);
                    SyncCalToUi();
                    _statusLabel.Text = $"Status: Pico found at {picoDrive} — calibration loaded";
                }
                catch
                {
                    _statusLabel.Text = $"Status: Pico found at {picoDrive} — calibration load failed (using defaults)";
                }
            }
            else
            {
                _statusLabel.Text = $"Status: Pico found at {picoDrive} — no calibration.json";
            }

            RefreshProfileList();
        }
        else
        {
            _statusLabel.Text = "Status: No Pico drive found — connect Pico and restart";
        }

        // HID setup
        _hidReader.Register(Handle);
        _hidReader.ScanDevices();
        var names = _hidReader.GetDeviceNames();
        _deviceCombo.Items.AddRange(names.ToArray());
        _hidReader.AutoSelectPico();
        if (_hidReader.SelectedDeviceIndex >= 0 && _deviceCombo.Items.Count > 0)
            _deviceCombo.SelectedIndex = _hidReader.SelectedDeviceIndex;

        // Initial throttle visibility
        UpdateThrottleVisibility();

        // Start poll loop
        _pollTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        _hidReader.ProcessMessage(ref m);
        base.WndProc(ref m);
    }

    // ══════════════════════════════════════════════════════════════════
    // Sync: UI ↔ CalibrationData
    // ══════════════════════════════════════════════════════════════════

    private void SyncCalFromUi()
    {
        // Brake
        _cal.RawMin = (int)_brakeRawMinNum.Value;
        _cal.RawMax = (int)_brakeRawMaxNum.Value;
        _cal.Curve = _brakeCurveCombo.SelectedItem?.ToString() ?? "linear";
        _cal.ProgressivePower = _brakeProgressivePowerTrack.Value / 10.0;
        _cal.AggressivePower = _brakeAggressivePowerTrack.Value / 10.0;
        _cal.Smoothing = _brakeSmoothingTrack.Value / 100.0;
        _cal.Deadzone = _brakeDeadzoneTrack.Value;
        _cal.Invert = _brakeInvertCheck.Checked;
        _cal.Saturation = _brakeSaturationTrack.Value / 100.0;
        _cal.BitePoint = _brakeBitePointTrack.Value / 100.0;
        if (_brakeOversampleCombo.SelectedItem is string oversampleStr && int.TryParse(oversampleStr, out int os))
            _cal.Oversample = os;

        // Throttle
        _cal.ThrottleEnabled = _throttleEnableCheck.Checked;
        _cal.ThrottleRawMin = (int)_throttleRawMinNum.Value;
        _cal.ThrottleRawMax = (int)_throttleRawMaxNum.Value;
        _cal.ThrottleCurve = _throttleCurveCombo.SelectedItem?.ToString() ?? "linear";
        _cal.ThrottleProgressivePower = _throttleProgressivePowerTrack.Value / 10.0;
        _cal.ThrottleAggressivePower = _throttleAggressivePowerTrack.Value / 10.0;
        _cal.ThrottleSmoothing = _throttleSmoothingTrack.Value / 100.0;
        _cal.ThrottleDeadzone = _throttleDeadzoneTrack.Value;
        _cal.ThrottleInvert = _throttleInvertCheck.Checked;
        _cal.ThrottleSaturation = _throttleSaturationTrack.Value / 100.0;
        _cal.ThrottleBitePoint = _throttleBitePointTrack.Value / 100.0;

        _cal.SyncToChannels();
    }

    private void SyncCalToUi()
    {
        // Brake
        _brakeRawMinNum.Value = Math.Clamp(_cal.RawMin, 0, 65535);
        _brakeRawMaxNum.Value = Math.Clamp(_cal.RawMax, 0, 65535);
        SelectComboByString(_brakeCurveCombo, _cal.Curve);
        _brakeProgressivePowerTrack.Value = Math.Clamp((int)(_cal.ProgressivePower * 10), 11, 50);
        _brakeAggressivePowerTrack.Value = Math.Clamp((int)(_cal.AggressivePower * 10), 11, 50);
        _brakeSmoothingTrack.Value = Math.Clamp((int)(_cal.Smoothing * 100), 0, 95);
        _brakeDeadzoneTrack.Value = Math.Clamp(_cal.Deadzone, 0, 1000);
        _brakeInvertCheck.Checked = _cal.Invert;
        _brakeSaturationTrack.Value = Math.Clamp((int)(_cal.Saturation * 100), 10, 100);
        _brakeBitePointTrack.Value = Math.Clamp((int)(_cal.BitePoint * 100), 0, 50);
        SelectOversampleCombo(_cal.Oversample);

        // Throttle
        _throttleEnableCheck.Checked = _cal.ThrottleEnabled;
        _throttleRawMinNum.Value = Math.Clamp(_cal.ThrottleRawMin, 0, 65535);
        _throttleRawMaxNum.Value = Math.Clamp(_cal.ThrottleRawMax, 0, 65535);
        SelectComboByString(_throttleCurveCombo, _cal.ThrottleCurve);
        _throttleProgressivePowerTrack.Value = Math.Clamp((int)(_cal.ThrottleProgressivePower * 10), 11, 50);
        _throttleAggressivePowerTrack.Value = Math.Clamp((int)(_cal.ThrottleAggressivePower * 10), 11, 50);
        _throttleSmoothingTrack.Value = Math.Clamp((int)(_cal.ThrottleSmoothing * 100), 0, 95);
        _throttleDeadzoneTrack.Value = Math.Clamp(_cal.ThrottleDeadzone, 0, 1000);
        _throttleInvertCheck.Checked = _cal.ThrottleInvert;
        _throttleSaturationTrack.Value = Math.Clamp((int)(_cal.ThrottleSaturation * 100), 10, 100);
        _throttleBitePointTrack.Value = Math.Clamp((int)(_cal.ThrottleBitePoint * 100), 0, 50);
    }

    private static void SelectComboByString(ComboBox combo, string value)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is string s && s == value)
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void SelectOversampleCombo(int value)
    {
        for (int i = 0; i < _brakeOversampleCombo.Items.Count; i++)
        {
            if (_brakeOversampleCombo.Items[i] is string s && int.TryParse(s, out int v) && v == value)
            {
                _brakeOversampleCombo.SelectedIndex = i;
                return;
            }
        }
        _brakeOversampleCombo.SelectedIndex = 2; // default 16
    }

    private static void CopyCalibration(CalibrationData src, CalibrationData dst)
    {
        // Brake
        dst.RawMin = src.RawMin;
        dst.RawMax = src.RawMax;
        dst.Deadzone = src.Deadzone;
        dst.Curve = src.Curve;
        dst.ProgressivePower = src.ProgressivePower;
        dst.AggressivePower = src.AggressivePower;
        dst.Smoothing = src.Smoothing;
        dst.Invert = src.Invert;
        dst.Oversample = src.Oversample;
        dst.Saturation = src.Saturation;
        dst.BitePoint = src.BitePoint;
        dst.CurvePoints = src.CurvePoints.Select(p => new[] { p[0], p[1] }).ToList();

        // Throttle
        dst.ThrottleEnabled = src.ThrottleEnabled;
        dst.ThrottleSensor = src.ThrottleSensor;
        dst.ThrottleRawMin = src.ThrottleRawMin;
        dst.ThrottleRawMax = src.ThrottleRawMax;
        dst.ThrottleDeadzone = src.ThrottleDeadzone;
        dst.ThrottleCurve = src.ThrottleCurve;
        dst.ThrottleProgressivePower = src.ThrottleProgressivePower;
        dst.ThrottleAggressivePower = src.ThrottleAggressivePower;
        dst.ThrottleSmoothing = src.ThrottleSmoothing;
        dst.ThrottleInvert = src.ThrottleInvert;
        dst.ThrottleSaturation = src.ThrottleSaturation;
        dst.ThrottleBitePoint = src.ThrottleBitePoint;
        dst.ThrottleCurvePoints = src.ThrottleCurvePoints.Select(p => new[] { p[0], p[1] }).ToList();

        dst.SyncToChannels();
    }

    // ══════════════════════════════════════════════════════════════════
    // Poll loop (~30Hz)
    // ══════════════════════════════════════════════════════════════════

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        // 1. Sync UI → _cal → channels
        SyncCalFromUi();

        // 2. Read HID
        int rawBrakeInt = _hidReader.ReadRawAdcInt();
        float rawBrakeFloat = _hidReader.ReadRawAdc();
        float gameBrake = _hidReader.ReadBrake();
        float rawThrottleFloat = _hidReader.ReadThrottleRaw();
        float gameThrottle = _hidReader.ReadThrottle();

        // 3. Compute preview
        double previewBrake = _cal.BrakeChannel.ProcessRaw(rawBrakeInt);
        double previewThrottle = _cal.ThrottleEnabled ? _cal.ThrottleChannel.ProcessRaw(_hidReader.ReadThrottleRawInt()) : 0.0;

        // 4. Update histories (FIFO)
        AddHistory(_rawHistory, rawBrakeFloat);
        AddHistory(_previewHistory, previewBrake);
        AddHistory(_gameInputHistory, gameBrake);
        if (_cal.ThrottleEnabled)
            AddHistory(_throttleHistory, previewThrottle);
        else if (_throttleHistory.Count > 0)
            _throttleHistory.Clear();

        // 5. Update info labels
        _rawAdcLabel.Text = $"Raw ADC: {rawBrakeInt} ({rawBrakeFloat:P1})";
        _brakeLabel.Text = $"Brake: {gameBrake:P1} (preview: {previewBrake:P1})";
        _throttleLabel.Text = _cal.ThrottleEnabled
            ? $"Throttle: {gameThrottle:P1} (preview: {previewThrottle:P1})"
            : "Throttle: disabled";

        // 6. Process auto-cal
        ProcessAutoCal();

        // 7. Update graph
        UpdateGraph();

        // 8. Update status
        UpdateStatus();
    }

    private static void AddHistory(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    private void UpdateGraph()
    {
        var plot = _formsPlot.Plot;
        plot.Clear();

        // Re-apply dark theme after Clear()
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1e1e1e");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#1e1e1e");
        plot.Axes.Color(ScottPlot.Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#404040");

        double[] xs = Enumerable.Range(0, _rawHistory.Count).Select(i => (double)i).ToArray();

        // Raw ADC (blue)
        var scRaw = plot.Add.Scatter(xs, _rawHistory.ToArray());
        scRaw.Color = ScottPlot.Color.FromHex("#4488ff");
        scRaw.LineWidth = 1;
        scRaw.MarkerSize = 0;
        scRaw.LegendText = "Raw ADC";

        // Preview (green)
        var scPreview = plot.Add.Scatter(xs.Length > 0 ? xs : new double[] { 0 }, _previewHistory.ToArray());
        scPreview.Color = ScottPlot.Color.FromHex("#44ff44");
        scPreview.LineWidth = 1;
        scPreview.MarkerSize = 0;
        scPreview.LegendText = "Preview";

        // Game Input (red)
        var scGame = plot.Add.Scatter(xs.Length > 0 ? xs : new double[] { 0 }, _gameInputHistory.ToArray());
        scGame.Color = ScottPlot.Color.FromHex("#ff4444");
        scGame.LineWidth = 1;
        scGame.MarkerSize = 0;
        scGame.LegendText = "Game Input";

        // Throttle (orange) — only when enabled
        if (_cal.ThrottleEnabled && _throttleHistory.Count > 0)
        {
            double[] xs_thr = Enumerable.Range(0, _throttleHistory.Count).Select(i => (double)i).ToArray();
            var scThrottle = plot.Add.Scatter(xs_thr, _throttleHistory.ToArray());
            scThrottle.Color = ScottPlot.Color.FromHex("#ff8800");
            scThrottle.LineWidth = 1;
            scThrottle.MarkerSize = 0;
            scThrottle.LegendText = "Throttle";
        }

        plot.Axes.SetLimitsX(0, Math.Max(HistoryLength, _rawHistory.Count + 1));
        plot.Axes.SetLimitsY(-0.05, 1.05);
        plot.ShowLegend();
        plot.Title("Live Brake Data");
        plot.XLabel("Sample");
        plot.YLabel("Value");

        _formsPlot.Refresh();
    }

    private void UpdateStatus()
    {
        // Append auto-cal status if active
        if (_calPhase == AutoCalPhase.Countdown)
        {
            double remaining = 3.0 - (DateTime.Now - _phaseStart).TotalSeconds;
            if (remaining > 0)
                _statusLabel.Text = $"Auto-Cal: Release {_calTargetName()}... {remaining:F0}s";
        }
        else if (_calPhase == AutoCalPhase.Capture)
        {
            double remaining = 5.0 - (DateTime.Now - _phaseStart).TotalSeconds;
            if (remaining > 0)
                _statusLabel.Text = $"Auto-Cal: Press {_calTargetName()} firmly! {remaining:F0}s";
        }
    }

    private string _calTargetName() => _calTarget == 0 ? "brake" : "throttle";

    // ══════════════════════════════════════════════════════════════════
    // Device selection
    // ══════════════════════════════════════════════════════════════════

    private void DeviceCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_deviceCombo.SelectedIndex >= 0)
            _hidReader.SelectDevice(_deviceCombo.SelectedIndex);
    }

    // ══════════════════════════════════════════════════════════════════
    // Throttle toggle
    // ══════════════════════════════════════════════════════════════════

    private void ThrottleEnableCheck_CheckedChanged(object? sender, EventArgs e)
    {
        UpdateThrottleVisibility();
        // Clear throttle history when disabled (Python only clears throttle histories)
        _throttleHistory.Clear();
    }

    private void UpdateThrottleVisibility()
    {
        SetAllControlsEnabled(_throttlePanel, _throttleEnableCheck.Checked);
        _throttleEnableCheck.Enabled = true; // The checkbox itself stays enabled
    }

    // ══════════════════════════════════════════════════════════════════
    // Auto-calibration state machine
    // ══════════════════════════════════════════════════════════════════

    private void AutoCalButton_Click(object? sender, EventArgs e)
    {
        if (_calPhase != AutoCalPhase.Idle) return;

        // If throttle enabled, ask which target
        if (_cal.ThrottleEnabled)
        {
            var result = MessageBox.Show(
                "Calibrate which pedal?\n\nYes = Brake\nNo = Throttle",
                "Auto Calibration Target",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes) _calTarget = 0;
            else if (result == DialogResult.No) _calTarget = 1;
            else return;
        }
        else
        {
            _calTarget = 0;
        }

        // Start countdown
        _calPhase = AutoCalPhase.Countdown;
        _phaseStart = DateTime.Now;
        _autoCalButton.Enabled = false;
        _autoCalLabel.Text = $"Release {_calTargetName()}...";
    }

    private void ProcessAutoCal()
    {
        switch (_calPhase)
        {
            case AutoCalPhase.Countdown:
            {
                double elapsed = (DateTime.Now - _phaseStart).TotalSeconds;
                double remaining = 3.0 - elapsed;
                if (remaining > 0)
                {
                    _autoCalLabel.Text = $"Release {_calTargetName()}... {remaining:F0}s";
                }
                else
                {
                    // Transition to capture
                    _calPhase = AutoCalPhase.Capture;
                    _phaseStart = DateTime.Now;
                    _captureSamples.Clear();
                    _autoCalLabel.Text = $"Press {_calTargetName()} firmly!";
                }
                break;
            }
            case AutoCalPhase.Capture:
            {
                double elapsed = (DateTime.Now - _phaseStart).TotalSeconds;
                double remaining = 5.0 - elapsed;

                // Collect sample
                int sample = _calTarget == 0 ? _hidReader.ReadRawAdcInt() : _hidReader.ReadThrottleRawInt();
                _captureSamples.Add(sample);

                if (remaining > 0)
                {
                    _autoCalLabel.Text = $"Press {_calTargetName()} firmly! {remaining:F0}s";
                }
                else
                {
                    // Compute min/max from captured samples
                    int minVal = _captureSamples.Min();
                    int maxVal = _captureSamples.Max();

                    // Apply to calibration
                    if (_calTarget == 0)
                    {
                        _cal.RawMin = minVal;
                        _cal.RawMax = maxVal;
                        _brakeRawMinNum.Value = Math.Clamp(minVal, 0, 65535);
                        _brakeRawMaxNum.Value = Math.Clamp(maxVal, 0, 65535);
                    }
                    else
                    {
                        _cal.ThrottleRawMin = minVal;
                        _cal.ThrottleRawMax = maxVal;
                        _throttleRawMinNum.Value = Math.Clamp(minVal, 0, 65535);
                        _throttleRawMaxNum.Value = Math.Clamp(maxVal, 0, 65535);
                    }

                    _cal.SyncToChannels();
                    _autoCalLabel.Text = $"Done! Min={minVal}, Max={maxVal}";
                    _calPhase = AutoCalPhase.Done;
                    _autoCalButton.Enabled = true;

                    // Reset to Idle after a brief moment
                    var resetTimer = new System.Windows.Forms.Timer { Interval = 2000 };
                    resetTimer.Tick += (_, _) =>
                    {
                        _calPhase = AutoCalPhase.Idle;
                        _autoCalLabel.Text = "Press button to start";
                        resetTimer.Dispose();
                    };
                    resetTimer.Start();
                }
                break;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Curve editor
    // ══════════════════════════════════════════════════════════════════

    private void ShowCurveEditor(string target)
    {
        string curve;
        double progPower, aggPower;
        List<double[]> points;

        if (target == "throttle")
        {
            curve = _cal.ThrottleCurve;
            progPower = _cal.ThrottleProgressivePower;
            aggPower = _cal.ThrottleAggressivePower;
            points = _cal.ThrottleCurvePoints;
        }
        else
        {
            curve = _cal.Curve;
            progPower = _cal.ProgressivePower;
            aggPower = _cal.AggressivePower;
            points = _cal.CurvePoints;
        }

        using var dlg = new CurveEditorDialog(curve, progPower, aggPower, points);
        if (dlg.ShowDialog(this) == DialogResult.OK && dlg.Applied)
        {
            if (target == "throttle")
            {
                _cal.ThrottleCurve = dlg.SelectedCurve;
                _cal.ThrottleProgressivePower = dlg.ProgressivePower;
                _cal.ThrottleAggressivePower = dlg.AggressivePower;
                _cal.ThrottleCurvePoints = dlg.EditedPoints;
                SelectComboByString(_throttleCurveCombo, dlg.SelectedCurve);
                _throttleProgressivePowerTrack.Value = Math.Clamp((int)(dlg.ProgressivePower * 10), 11, 50);
                _throttleAggressivePowerTrack.Value = Math.Clamp((int)(dlg.AggressivePower * 10), 11, 50);
            }
            else
            {
                _cal.Curve = dlg.SelectedCurve;
                _cal.ProgressivePower = dlg.ProgressivePower;
                _cal.AggressivePower = dlg.AggressivePower;
                _cal.CurvePoints = dlg.EditedPoints;
                SelectComboByString(_brakeCurveCombo, dlg.SelectedCurve);
                _brakeProgressivePowerTrack.Value = Math.Clamp((int)(dlg.ProgressivePower * 10), 11, 50);
                _brakeAggressivePowerTrack.Value = Math.Clamp((int)(dlg.AggressivePower * 10), 11, 50);
            }
            _cal.SyncToChannels();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Profiles
    // ══════════════════════════════════════════════════════════════════

    private void RefreshProfileList()
    {
        _profileCombo.Items.Clear();
        if (_profileManager == null) return;
        var profiles = _profileManager.ListProfiles();
        _profileCombo.Items.AddRange(profiles.ToArray());
        if (_profileCombo.Items.Count > 0) _profileCombo.SelectedIndex = 0;
    }

    private void ProfileSave_Click(object? sender, EventArgs e)
    {
        if (_profileManager == null) return;

        string? name = ShowInputDialog("Save Profile", "Profile name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            SyncCalFromUi();
            _cal.SyncFromChannels();
            _profileManager.SaveProfile(name, _cal);
            RefreshProfileList();

            // Select the newly saved profile
            for (int i = 0; i < _profileCombo.Items.Count; i++)
            {
                if (_profileCombo.Items[i] is string s && s.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    _profileCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ProfileLoad_Click(object? sender, EventArgs e)
    {
        if (_profileManager == null) return;
        if (_profileCombo.SelectedItem is not string name) return;

        try
        {
            var loaded = _profileManager.LoadProfile(name);
            if (loaded == null)
            {
                MessageBox.Show("Profile not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            CopyCalibration(loaded, _cal);
            SyncCalToUi();
            _statusLabel.Text = $"Status: Loaded profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ProfileDelete_Click(object? sender, EventArgs e)
    {
        if (_profileManager == null) return;
        if (_profileCombo.SelectedItem is not string name) return;

        var result = MessageBox.Show($"Delete profile '{name}'?", "Confirm Delete",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes) return;

        try
        {
            _profileManager.DeleteProfile(name);
            RefreshProfileList();
            _statusLabel.Text = $"Status: Deleted profile '{name}'";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete profile: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // Save to Pico
    // ══════════════════════════════════════════════════════════════════

    private void SaveToPico_Click(object? sender, EventArgs e)
    {
        if (_profileManager == null)
        {
            MessageBox.Show("No Pico drive found. Connect Pico and restart.", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            SyncCalFromUi();
            _cal.SyncFromChannels();
            _profileManager.SaveCalibration(_cal);
            MessageBox.Show(
                "Calibration saved to Pico!\n\nPress the RESET button on your Pico to apply the new settings.",
                "Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _statusLabel.Text = "Status: Calibration saved — press RESET on Pico";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save calibration: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // InputDialog (simple modal dialog for profile name entry)
    // ══════════════════════════════════════════════════════════════════

    private static string? ShowInputDialog(string title, string prompt)
    {
        using var form = new Form
        {
            Text = title,
            Size = new Size(360, 160),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var label = new WinFormsLabel { Text = prompt, Left = 16, Top = 16, AutoSize = true };
        var textBox = new TextBox { Left = 16, Top = 42, Width = 310 };
        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 160, Top = 78, Width = 80 };
        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 246, Top = 78, Width = 80 };

        form.Controls.AddRange(new Control[] { label, textBox, btnOk, btnCancel });
        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        return form.ShowDialog() == DialogResult.OK ? textBox.Text.Trim() : null;
    }

    // ══════════════════════════════════════════════════════════════════
    // Cleanup
    // ══════════════════════════════════════════════════════════════════

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _pollTimer.Stop();
        base.OnFormClosing(e);
    }
}
