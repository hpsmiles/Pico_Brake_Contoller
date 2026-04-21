using ScottPlot;
using ScottPlot.Plottables;
using SDColor = System.Drawing.Color;
using WinFormsLabel = System.Windows.Forms.Label;

namespace BrakeCalibrator;

public class CurveEditorDialog : Form
{
    private readonly ScottPlot.WinForms.FormsPlot _formsPlot;
    private Scatter _scatterCurve = null!;
    private Scatter _scatterPts = null!;

    private List<double[]> _points;
    private int _dragIndex = -1;
    private bool _dragging;

    // Result properties
    public string SelectedCurve { get; private set; }
    public double ProgressivePower { get; private set; }
    public double AggressivePower { get; private set; }
    public List<double[]> EditedPoints => _points.Select(p => new[] { p[0], p[1] }).ToList();
    public bool Applied { get; private set; }

    private readonly TrackBar _progPowerTrack;
    private readonly TrackBar _aggPowerTrack;
    private readonly WinFormsLabel _progPowerLabel;
    private readonly WinFormsLabel _aggPowerLabel;
    private readonly WinFormsLabel _infoLabel;

    public CurveEditorDialog(string curveType, double progressivePower, double aggressivePower, List<double[]> curvePoints)
    {
        SelectedCurve = curveType;
        ProgressivePower = progressivePower;
        AggressivePower = aggressivePower;
        _points = curvePoints.Select(p => new[] { p[0], p[1] }).ToList();

        Text = "Curve Editor";
        Size = new Size(620, 620);
        MinimumSize = new Size(500, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;

        // ── Top panel: preset buttons ──
        var topPanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        string[] presetNames = ["Linear", "Progressive", "Aggressive", "S-curve"];
        for (int i = 0; i < presetNames.Length; i++)
        {
            var btn = new Button
            {
                Text = presetNames[i],
                Tag = presetNames[i],
                Width = 100,
                Height = 28,
                Location = new Point(8 + i * 108, 4),
            };
            btn.Click += PresetButton_Click;
            topPanel.Controls.Add(btn);
        }
        Controls.Add(topPanel);

        // ── Info label ──
        _infoLabel = new WinFormsLabel
        {
            Text = "Drag points to edit \u00b7 Click empty space to add \u00b7 Right-click point to delete",
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SDColor.LightGray,
            BackColor = SDColor.FromArgb(30, 30, 30),
        };
        Controls.Add(_infoLabel);

        // ── ScottPlot graph ──
        _formsPlot = new ScottPlot.WinForms.FormsPlot { Dock = DockStyle.Fill };
        ApplyDarkTheme();
        Controls.Add(_formsPlot);

        // ── Bottom panel: sliders + buttons ──
        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 120 };
        bottomPanel.BackColor = SDColor.FromArgb(30, 30, 30);

        int y = 8;

        // Progressive power slider
        bottomPanel.Controls.Add(new WinFormsLabel { Text = "Progressive Power:", Left = 12, Top = y, AutoSize = true, ForeColor = SDColor.White, BackColor = SDColor.Transparent });
        _progPowerTrack = new TrackBar { Left = 150, Top = y - 2, Width = 250, Minimum = 11, Maximum = 50, Value = (int)(ProgressivePower * 10), TickFrequency = 5, BackColor = SDColor.FromArgb(30, 30, 30) };
        _progPowerLabel = new WinFormsLabel { Left = 410, Top = y, AutoSize = true, ForeColor = SDColor.White, BackColor = SDColor.Transparent, Text = $"P={ProgressivePower:F1}" };
        _progPowerTrack.ValueChanged += (_, _) =>
        {
            ProgressivePower = Math.Round(_progPowerTrack.Value / 10.0, 1);
            _progPowerLabel.Text = $"P={ProgressivePower:F1}";
            Redraw();
        };
        bottomPanel.Controls.Add(_progPowerTrack);
        bottomPanel.Controls.Add(_progPowerLabel);

        y += 34;

        // Aggressive power slider
        bottomPanel.Controls.Add(new WinFormsLabel { Text = "Aggressive Power:", Left = 12, Top = y, AutoSize = true, ForeColor = SDColor.White, BackColor = SDColor.Transparent });
        _aggPowerTrack = new TrackBar { Left = 150, Top = y - 2, Width = 250, Minimum = 11, Maximum = 50, Value = (int)(AggressivePower * 10), TickFrequency = 5, BackColor = SDColor.FromArgb(30, 30, 30) };
        _aggPowerLabel = new WinFormsLabel { Left = 410, Top = y, AutoSize = true, ForeColor = SDColor.White, BackColor = SDColor.Transparent, Text = $"P={AggressivePower:F1}" };
        _aggPowerTrack.ValueChanged += (_, _) =>
        {
            AggressivePower = Math.Round(_aggPowerTrack.Value / 10.0, 1);
            _aggPowerLabel.Text = $"P={AggressivePower:F1}";
            Redraw();
        };
        bottomPanel.Controls.Add(_aggPowerTrack);
        bottomPanel.Controls.Add(_aggPowerLabel);

        y += 40;

        // Apply / Cancel buttons
        var btnApply = new Button { Text = "Apply", DialogResult = DialogResult.OK, Left = 150, Top = y, Width = 90, Height = 28 };
        btnApply.Click += (_, _) => { Applied = true; Close(); };

        var btnCancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 260, Top = y, Width = 90, Height = 28 };
        btnCancel.Click += (_, _) => { Applied = false; Close(); };

        bottomPanel.Controls.Add(btnApply);
        bottomPanel.Controls.Add(btnCancel);
        Controls.Add(bottomPanel);

        // Wire mouse events
        _formsPlot.MouseDown += FormsPlot_MouseDown;
        _formsPlot.MouseMove += FormsPlot_MouseMove;
        _formsPlot.MouseUp += FormsPlot_MouseUp;

        // Initial render
        Load += (_, _) => Redraw();
    }

    /// <summary>Apply ScottPlot 5 dark theme using the cookbook pattern.</summary>
    private void ApplyDarkTheme()
    {
        var plot = _formsPlot.Plot;
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#181818");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#1f1f1f");
        plot.Axes.Color(ScottPlot.Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#404040");
    }

    private void PresetButton_Click(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        string key = ((string)btn.Tag!).ToLowerInvariant();

        if (CalibrationData.CurvePresets.TryGetValue(key, out var preset))
        {
            _points = preset.Select(p => new[] { p[0], p[1] }).ToList();
            SelectedCurve = key;
            Redraw();
        }
    }

    private void FormsPlot_MouseDown(object? sender, MouseEventArgs e)
    {
        var coords = _formsPlot.Plot.GetCoordinates(e.X, e.Y);

        if (e.Button == MouseButtons.Right)
        {
            // Right-click: delete nearest point (endpoints protected)
            var nearest = _scatterPts.Data.GetNearest(coords, _formsPlot.Plot.LastRender);
            if (nearest.IsReal && nearest.Index > 0 && nearest.Index < _points.Count - 1)
            {
                _points.RemoveAt(nearest.Index);
                SelectedCurve = "custom";
                Redraw();
            }
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        var nearestLeft = _scatterPts.Data.GetNearest(coords, _formsPlot.Plot.LastRender);

        // Determine distance threshold in coordinate space (15px equivalent)
        double pxPerUnitX = _formsPlot.Plot.LastRender.DataRect.Width;
        double threshold = 15.0 / pxPerUnitX;

        if (nearestLeft.IsReal && nearestLeft.Index >= 0 && nearestLeft.Index < _points.Count)
        {
            double dx = coords.X - _points[nearestLeft.Index][0];
            double dy = coords.Y - _points[nearestLeft.Index][1];
            double dist = Math.Sqrt(dx * dx + dy * dy);

            if (dist < threshold)
            {
                _dragIndex = nearestLeft.Index;
                _dragging = true;
                return;
            }
        }

        // Click on empty space: add a point
        double nx = Math.Round(coords.X, 3);
        double ny = Math.Clamp(Math.Round(coords.Y, 3), 0.0, 1.0);
        if (nx > 0.01 && nx < 0.99)
        {
            _points.Add(new[] { nx, ny });
            _points.Sort((a, b) => a[0].CompareTo(b[0]));
            SelectedCurve = "custom";
            Redraw();
        }
    }

    private void FormsPlot_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging || _dragIndex < 0 || _dragIndex >= _points.Count) return;

        var coords = _formsPlot.Plot.GetCoordinates(e.X, e.Y);

        if (_dragIndex == 0)
        {
            // First endpoint: x fixed at 0
            _points[0][1] = Math.Clamp(coords.Y, 0.0, 1.0);
        }
        else if (_dragIndex == _points.Count - 1)
        {
            // Last endpoint: x fixed at 1
            _points[_points.Count - 1][1] = Math.Clamp(coords.Y, 0.0, 1.0);
        }
        else
        {
            // Interior point: constrained between neighbors with 0.01 gap
            double xMin = _points[_dragIndex - 1][0] + 0.01;
            double xMax = _points[_dragIndex + 1][0] - 0.01;
            if (xMin > xMax) xMin = xMax = (_points[_dragIndex - 1][0] + _points[_dragIndex + 1][0]) / 2.0;
            double nx = Math.Clamp(coords.X, xMin, xMax);
            double ny = Math.Clamp(coords.Y, 0.0, 1.0);
            _points[_dragIndex][0] = nx;
            _points[_dragIndex][1] = ny;
        }

        SelectedCurve = "custom";
        Redraw();
    }

    private void FormsPlot_MouseUp(object? sender, MouseEventArgs e)
    {
        _dragIndex = -1;
        _dragging = false;
    }

    private void Redraw()
    {
        _formsPlot.Plot.Clear();
        ApplyDarkTheme();

        // Diagonal reference line
        var refLine = _formsPlot.Plot.Add.Line(0, 0, 1, 1);
        refLine.LineColor = ScottPlot.Color.FromHex("#333333");
        refLine.LineWidth = 1;

        // Generate curve coordinates from type
        double[] curveX, curveY;
        switch (SelectedCurve)
        {
            case "progressive":
                curveX = Enumerable.Range(0, 61).Select(i => i / 60.0).ToArray();
                curveY = curveX.Select(t => Math.Pow(t, ProgressivePower)).ToArray();
                break;
            case "aggressive":
                curveX = Enumerable.Range(0, 61).Select(i => i / 60.0).ToArray();
                curveY = curveX.Select(t => Math.Pow(t, 1.0 / AggressivePower)).ToArray();
                break;
            case "custom":
                curveX = _points.Select(p => p[0]).ToArray();
                curveY = _points.Select(p => p[1]).ToArray();
                break;
            default: // "linear"
                curveX = [0.0, 1.0];
                curveY = [0.0, 1.0];
                break;
        }

        // Curve line
        _scatterCurve = _formsPlot.Plot.Add.Scatter(curveX, curveY);
        _scatterCurve.Color = ScottPlot.Color.FromHex("#00CED1"); // dark turquoise
        _scatterCurve.LineWidth = 2;
        _scatterCurve.MarkerSize = 0;

        // Control points (always shown as large draggable markers)
        var ptXs = _points.Select(p => p[0]).ToArray();
        var ptYs = _points.Select(p => p[1]).ToArray();
        _scatterPts = _formsPlot.Plot.Add.Scatter(ptXs, ptYs);
        _scatterPts.Color = ScottPlot.Color.FromHex("#FFA500"); // orange
        _scatterPts.MarkerSize = 10;
        _scatterPts.MarkerShape = MarkerShape.FilledCircle;
        _scatterPts.LineWidth = 0;

        _formsPlot.Plot.Axes.SetLimitsX(-0.05, 1.05);
        _formsPlot.Plot.Axes.SetLimitsY(-0.05, 1.05);
        _formsPlot.Plot.Title($"Response Curve ({SelectedCurve})");
        _formsPlot.Plot.XLabel("Input");
        _formsPlot.Plot.YLabel("Output");
        _formsPlot.Refresh();
    }
}
