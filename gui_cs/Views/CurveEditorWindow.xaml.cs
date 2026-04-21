using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using BrakeCalibrator.Models;
using BrakeCalibrator.ViewModels;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace BrakeCalibrator.Views
{
    public partial class CurveEditorWindow : Window
    {
        private readonly MainViewModel _vm;
        private readonly bool _isThrottle;
        private List<List<double>> _points;
        private string _curveType;
        private double _progressivePower;
        private double _aggressivePower;

        // Drag state
        private int _dragIndex = -1;
        private bool _isDragging;
        private readonly List<Ellipse> _pointMarkers = new();

        // Canvas layout
        private const double Pad = 40;

        public CurveEditorWindow(MainViewModel vm, bool isThrottle)
        {
            InitializeComponent();

            _vm = vm;
            _isThrottle = isThrottle;

            // Load current settings
            _curveType = isThrottle ? vm.ThrottleCurve : vm.Curve;
            _progressivePower = isThrottle ? vm.ThrottleProgressivePower : vm.ProgressivePower;
            _aggressivePower = isThrottle ? vm.ThrottleAggressivePower : vm.AggressivePower;
            _points = (isThrottle ? vm.ThrottleCurvePoints : vm.CurvePoints)
                .ConvertAll(p => new List<double>(p));

            HeaderLabel.Text = isThrottle ? "Throttle Curve Editor" : "Brake Curve Editor";

            // Configure chart axes in code
            CurveChart.XAxes = new ObservableCollection<Axis>
            {
                new Axis
                {
                    MinLimit = 0, MaxLimit = 1,
                    LabelsPaint = new SolidColorPaint(SKColor.Parse("#6E7681")),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#21283B")),
                }
            };
            CurveChart.YAxes = new ObservableCollection<Axis>
            {
                new Axis
                {
                    MinLimit = 0, MaxLimit = 1,
                    LabelsPaint = new SolidColorPaint(SKColor.Parse("#6E7681")),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#21283B")),
                }
            };

            // Setup curve combo
            CurveCombo.ItemsSource = new[] { "linear", "progressive", "aggressive", "custom" };
            CurveCombo.SelectedItem = _curveType;

            // Setup power sliders
            ProgressiveSlider.Value = _progressivePower;
            AggressiveSlider.Value = _aggressivePower;

            UpdatePowerSliderVisibility();

            // Setup chart
            UpdateCurveChart();
            DrawControlPoints();

            // Hook canvas size changed
            PointCanvas.SizeChanged += (_, _) => DrawControlPoints();
        }

        private void UpdatePowerSliderVisibility()
        {
            PowerSlidersPanel.Visibility = _curveType is "progressive" or "aggressive"
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateCurveChart()
        {
            var curveSeries = new ObservableCollection<ISeries>();

            // Reference diagonal
            var refLine = new List<ObservablePoint>();
            for (int i = 0; i <= 60; i++)
                refLine.Add(new ObservablePoint(i / 60.0, i / 60.0));
            curveSeries.Add(new LineSeries<ObservablePoint>
            {
                Values = refLine,
                Stroke = new SolidColorPaint(SKColor.Parse("#21283B")) { StrokeThickness = 1f },
                Fill = null,
                GeometryStroke = null,
                GeometryFill = null,
                GeometrySize = 0,
                LineSmoothness = 0,
            });

            // Actual curve
            var curveLine = new List<ObservablePoint>();
            for (int i = 0; i <= 60; i++)
            {
                double t = i / 60.0;
                double v = SignalProcessing.ApplyCurve(t, _curveType, _progressivePower, _aggressivePower, _points);
                curveLine.Add(new ObservablePoint(t, v));
            }

            var color = _isThrottle ? "#D29922" : "#3FB950";
            curveSeries.Add(new LineSeries<ObservablePoint>
            {
                Values = curveLine,
                Stroke = new SolidColorPaint(SKColor.Parse(color)) { StrokeThickness = 2.5f },
                Fill = null,
                GeometryStroke = null,
                GeometryFill = null,
                GeometrySize = 0,
                LineSmoothness = 0.3f,
            });

            // Control points series (for custom)
            if (_curveType == "custom" && _points.Count >= 2)
            {
                var cpSeries = new List<ObservablePoint>();
                foreach (var p in _points)
                    cpSeries.Add(new ObservablePoint(p[0], p[1]));

                curveSeries.Add(new LineSeries<ObservablePoint>
                {
                    Values = cpSeries,
                    Stroke = new SolidColorPaint(SKColor.Parse(color)) { StrokeThickness = 1.5f },
                    Fill = null,
                    GeometryStroke = new SolidColorPaint(SKColor.Parse("#E6EDF3")) { StrokeThickness = 2f },
                    GeometryFill = new SolidColorPaint(SKColor.Parse("#21283B")),
                    GeometrySize = 10,
                    LineSmoothness = 0,
                });
            }

            CurveChart.Series = curveSeries;
        }

        private void DrawControlPoints()
        {
            PointCanvas.Children.Clear();
            _pointMarkers.Clear();

            if (_curveType != "custom") return;

            double cw = PointCanvas.ActualWidth;
            double ch = PointCanvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            double pw = cw - 2 * Pad;
            double ph = ch - 2 * Pad;

            for (int i = 0; i < _points.Count; i++)
            {
                double px = _points[i][0];
                double py = _points[i][1];

                double cx = Pad + pw * px;
                double cy = (ch - Pad) - ph * py;

                bool isEndpoint = i == 0 || i == _points.Count - 1;
                var fill = isEndpoint ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xAA, 0x00))
                                      : new SolidColorBrush(Colors.White);
                var stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88));

                var ellipse = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = 1.5,
                    Cursor = Cursors.Hand,
                    Tag = i,
                    ToolTip = $"({px:F2}, {py:F2})"
                };

                Canvas.SetLeft(ellipse, cx - 6);
                Canvas.SetTop(ellipse, cy - 6);

                PointCanvas.Children.Add(ellipse);
                _pointMarkers.Add(ellipse);
            }
        }

        #region Canvas Mouse Events

        private void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
        {
            if (_curveType != "custom") return;

            var pos = e.GetPosition(PointCanvas);
            int hitIndex = FindNearestPoint(pos, 15);

            if (hitIndex >= 0)
            {
                _dragIndex = hitIndex;
                _isDragging = true;
                PointCanvas.CaptureMouse();
            }
            else
            {
                // Add new point
                var (px, py) = FromCanvas(pos);
                if (px > 0.01 && px < 0.99)
                {
                    _points.Add(new List<double> { Math.Round(px, 4), Math.Round(py, 4) });
                    _points.Sort((a, b) => a[0].CompareTo(b[0]));
                    _curveType = "custom";
                    UpdateCurveChart();
                    DrawControlPoints();
                }
            }
        }

        private void OnCanvasLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                _dragIndex = -1;
                PointCanvas.ReleaseMouseCapture();
            }
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging || _dragIndex < 0) return;

            var pos = e.GetPosition(PointCanvas);
            var (px, py) = FromCanvas(pos);

            int i = _dragIndex;

            // Constrain x
            if (i == 0)
                px = 0.0;
            else if (i == _points.Count - 1)
                px = 1.0;
            else
            {
                double minX = _points[i - 1][0] + 0.01;
                double maxX = _points[i + 1][0] - 0.01;
                px = Math.Max(minX, Math.Min(maxX, px));
            }

            py = Math.Max(0.0, Math.Min(1.0, py));

            _points[i] = new List<double> { Math.Round(px, 4), Math.Round(py, 4) };
            _curveType = "custom";
            CurveCombo.SelectedItem = "custom";

            UpdateCurveChart();
            DrawControlPoints();
        }

        private void OnCanvasRightDown(object sender, MouseButtonEventArgs e)
        {
            if (_curveType != "custom") return;

            var pos = e.GetPosition(PointCanvas);
            int hitIndex = FindNearestPoint(pos, 15);

            if (hitIndex > 0 && hitIndex < _points.Count - 1)
            {
                _points.RemoveAt(hitIndex);
                _curveType = "custom";
                UpdateCurveChart();
                DrawControlPoints();
            }
        }

        private int FindNearestPoint(Point pos, double threshold)
        {
            double cw = PointCanvas.ActualWidth;
            double ch = PointCanvas.ActualHeight;
            double pw = cw - 2 * Pad;
            double ph = ch - 2 * Pad;

            double bestDist = threshold;
            int bestIdx = -1;

            for (int i = 0; i < _points.Count; i++)
            {
                double cx = Pad + pw * _points[i][0];
                double cy = (ch - Pad) - ph * _points[i][1];
                double dist = Math.Sqrt(Math.Pow(pos.X - cx, 2) + Math.Pow(pos.Y - cy, 2));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }

            return bestIdx;
        }

        private (double px, double py) FromCanvas(Point pos)
        {
            double cw = PointCanvas.ActualWidth;
            double ch = PointCanvas.ActualHeight;
            double pw = cw - 2 * Pad;
            double ph = ch - 2 * Pad;

            double px = (pos.X - Pad) / pw;
            double py = ((ch - Pad) - pos.Y) / ph;
            return (Math.Max(0.0, Math.Min(1.0, px)), Math.Max(0.0, Math.Min(1.0, py)));
        }

        #endregion

        #region Curve Combo & Presets

        private void OnCurveComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurveCombo.SelectedItem is string selected)
            {
                _curveType = selected;
                UpdatePowerSliderVisibility();
                UpdateCurveChart();
                DrawControlPoints();
            }
        }

        private void OnPowerSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _progressivePower = ProgressiveSlider.Value;
            _aggressivePower = AggressiveSlider.Value;
            ProgressiveValueLabel.Text = _progressivePower.ToString("F1");
            AggressiveValueLabel.Text = _aggressivePower.ToString("F1");
            UpdateCurveChart();
        }

        private void ApplyPreset(string name)
        {
            if (CurvePresets.Presets.TryGetValue(name, out var preset))
            {
                _points = preset.ConvertAll(p => new List<double>(p));
                _curveType = "custom";
                CurveCombo.SelectedItem = "custom";
                UpdatePowerSliderVisibility();
                UpdateCurveChart();
                DrawControlPoints();
            }
        }

        private void OnPresetLinear(object sender, RoutedEventArgs e) => ApplyPreset("linear");
        private void OnPresetProgressive(object sender, RoutedEventArgs e) => ApplyPreset("progressive");
        private void OnPresetAggressive(object sender, RoutedEventArgs e) => ApplyPreset("aggressive");
        private void OnPresetSCurve(object sender, RoutedEventArgs e) => ApplyPreset("S-curve");

        #endregion

        #region Apply / Cancel

        private void OnApply(object sender, RoutedEventArgs e)
        {
            if (_isThrottle)
            {
                _vm.ThrottleCurve = _curveType;
                _vm.ThrottleProgressivePower = _progressivePower;
                _vm.ThrottleAggressivePower = _aggressivePower;
                _vm.ThrottleCurvePoints = _points.ConvertAll(p => new List<double>(p));
            }
            else
            {
                _vm.Curve = _curveType;
                _vm.ProgressivePower = _progressivePower;
                _vm.AggressivePower = _aggressivePower;
                _vm.CurvePoints = _points.ConvertAll(p => new List<double>(p));
            }

            DialogResult = true;
            Close();
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion
    }
}
