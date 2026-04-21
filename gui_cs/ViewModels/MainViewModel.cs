using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using BrakeCalibrator.Models;
using BrakeCalibrator.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Drawing;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;namespace BrakeCalibrator.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly DispatcherTimer _pollTimer;
        private readonly HidReader _reader;
        private readonly Dispatcher _dispatcher;

        private const int HistoryLength = 200;

        // EMA state
        private double _brakeEma;
        private bool _brakeEmaInit;
        private double _throttleEma;
        private bool _throttleEmaInit;

        // Auto-calibration state
        private bool _autoCalibrating;
        private string _calPhase = "idle"; // idle, countdown, capture
        private DateTime _phaseStart;
        private readonly List<int> _captureSamples = new();
        private int _calTarget; // 0=brake, 1=throttle
        // Drive detection
        private string? _drivePath;
        private DispatcherTimer? _driveRefreshTimer;

        #region Observable Properties

        // Connection
        [ObservableProperty] private string _statusText = "No device connected";
        [ObservableProperty] private string _driveInfo = "No CIRCUITPY drive found";
        [ObservableProperty] private string _axisDebugText = "";
        [ObservableProperty] private int _selectedDeviceIndex = -1;
        [ObservableProperty] private ObservableCollection<DeviceInfo> _devices = new();

        // Brake calibration
        [ObservableProperty] private int _rawMin = 2000;
        [ObservableProperty] private int _rawMax = 56000;
        [ObservableProperty] private int _deadzone = 300;
        [ObservableProperty] private double _smoothing = 0.3;
        [ObservableProperty] private double _saturation = 1.0;
        [ObservableProperty] private double _bitePoint = 0.0;
        [ObservableProperty] private string _curve = "linear";
        [ObservableProperty] private double _progressivePower = 2.0;
        [ObservableProperty] private double _aggressivePower = 2.0;
        [ObservableProperty] private bool _invertBrake = false;
        [ObservableProperty] private int _oversample = 16;
        [ObservableProperty] private List<List<double>> _curvePoints = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } };

        // Throttle
        [ObservableProperty] private bool _throttleEnabled = false;
        [ObservableProperty] private string _throttleSensor = "auto";
        [ObservableProperty] private int _throttleRawMin = 2000;
        [ObservableProperty] private int _throttleRawMax = 56000;
        [ObservableProperty] private int _throttleDeadzone = 300;
        [ObservableProperty] private double _throttleSmoothing = 0.2;
        [ObservableProperty] private double _throttleSaturation = 1.0;
        [ObservableProperty] private double _throttleBitePoint = 0.0;
        [ObservableProperty] private string _throttleCurve = "linear";
        [ObservableProperty] private double _throttleProgressivePower = 2.0;
        [ObservableProperty] private double _throttleAggressivePower = 2.0;
        [ObservableProperty] private bool _throttleInvert = false;
        [ObservableProperty] private List<List<double>> _throttleCurvePoints = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } };

        // Auto-cal
        [ObservableProperty] private string _calStatus = "";
        [ObservableProperty] private bool _isAutoCalibrating = false;

        // Profiles
        [ObservableProperty] private ObservableCollection<string> _profiles = new();
        [ObservableProperty] private int _selectedProfileIndex = -1;

        // Static options
        public string[] CurveOptions { get; } = { "linear", "progressive", "aggressive", "custom" };
        public int[] OversampleOptions { get; } = { 1, 4, 16, 64 };

        // Chart data
        public ObservableCollection<ISeries> Series { get; }
        public ObservableCollection<ISeries> ThrottleSeries { get; }

        // Chart axes (configured in code, bound to XAML)
        public ObservableCollection<Axis> BrakeXAxis { get; }
        public ObservableCollection<Axis> BrakeYAxis { get; }
        public ObservableCollection<Axis> ThrottleXAxis { get; }
        public ObservableCollection<Axis> ThrottleYAxis { get; }

        #endregion

        public MainViewModel()
        {
            _reader = new HidReader();
            _dispatcher = Application.Current.Dispatcher;

            // Initialize chart series
            Series = new ObservableCollection<ISeries>
            {
                new LineSeries<ObservablePoint>
                {
                    Name = "Raw Input",
                    Values = new ObservableCollection<ObservablePoint>(),
                    Stroke = new SolidColorPaint(SKColor.Parse("#4488FF")) { StrokeThickness = 2f },
                    Fill = null,
                    GeometryStroke = null,
                    GeometryFill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.3f,
                    IsVisible = true,
                },
                new LineSeries<ObservablePoint>
                {
                    Name = "Calibrated",
                    Values = new ObservableCollection<ObservablePoint>(),
                    Stroke = new SolidColorPaint(SKColor.Parse("#44FF44")) { StrokeThickness = 2.5f },
                    Fill = null,
                    GeometryStroke = null,
                    GeometryFill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.3f,
                    IsVisible = true,
                },
                new LineSeries<ObservablePoint>
                {
                    Name = "Game Input",
                    Values = new ObservableCollection<ObservablePoint>(),
                    Stroke = new SolidColorPaint(SKColor.Parse("#FF4444")) { StrokeThickness = 2f },
                    Fill = null,
                    GeometryStroke = null,
                    GeometryFill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.3f,
                    IsVisible = true,
                },
            };

            ThrottleSeries = new ObservableCollection<ISeries>
            {
                new LineSeries<ObservablePoint>
                {
                    Name = "Raw Throttle",
                    Values = new ObservableCollection<ObservablePoint>(),
                    Stroke = new SolidColorPaint(SKColor.Parse("#FF8800")) { StrokeThickness = 2f },
                    Fill = null,
                    GeometryStroke = null,
                    GeometryFill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.3f,
                },
                new LineSeries<ObservablePoint>
                {
                    Name = "Calibrated Throttle",
                    Values = new ObservableCollection<ObservablePoint>(),
                    Stroke = new SolidColorPaint(SKColor.Parse("#FFCC00")) { StrokeThickness = 2.5f },
                    Fill = null,
                    GeometryStroke = null,
                    GeometryFill = null,
                    GeometrySize = 0,
                    LineSmoothness = 0.3f,
                },
            };

            // Configure chart axes
            BrakeXAxis = new ObservableCollection<Axis>
            {
                new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 200 }
            };
            BrakeYAxis = new ObservableCollection<Axis>
            {
                new Axis
                {
                    MinLimit = 0, MaxLimit = 1,
                    LabelsPaint = new SolidColorPaint(SKColor.Parse("#6E7681")),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#21283B")),
                }
            };
            ThrottleXAxis = new ObservableCollection<Axis>
            {
                new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 200 }
            };
            ThrottleYAxis = new ObservableCollection<Axis>
            {
                new Axis
                {
                    MinLimit = 0, MaxLimit = 1,
                    LabelsPaint = new SolidColorPaint(SKColor.Parse("#6E7681")),
                    TextSize = 10,
                    SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#21283B")),
                }
            };

            // Poll timer at ~30Hz
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _pollTimer.Tick += OnPollTick;
            _pollTimer.Start();

            // Drive refresh timer
            _driveRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _driveRefreshTimer.Tick += (_, _) => RefreshDrive();
            _driveRefreshTimer.Start();

            // Initial setup
            RefreshDevices();
            RefreshDrive();
        }

        #region Commands

        [RelayCommand]
        private void RefreshDevices()
        {
            var gamepads = _reader.EnumerateGamepads();
            Devices.Clear();
            foreach (var gp in gamepads)
                Devices.Add(gp);

            // Auto-select Pico device
            var picoIdx = Devices.ToList().FindIndex(d => d.IsPico);
            if (picoIdx >= 0) SelectedDeviceIndex = picoIdx;
            else if (Devices.Count > 0) SelectedDeviceIndex = 0;
        }

        partial void OnSelectedDeviceIndexChanged(int value)
        {
            if (value < 0 || value >= Devices.Count) return;
            var dev = Devices[value];
            if (_reader.Connect(dev.InstanceGuid))
                StatusText = $"Connected: {dev.ProductName}";
            else
                StatusText = $"Failed to connect: {dev.ProductName}";
        }

        [RelayCommand]
        private void SetRawMinToCurrent()
        {
            RawMin = _reader.RawBrakeInt;
        }

        [RelayCommand]
        private void SetRawMaxToCurrent()
        {
            RawMax = _reader.RawBrakeInt;
        }

        [RelayCommand]
        private void SetThrottleRawMinToCurrent()
        {
            ThrottleRawMin = _reader.RawThrottleInt;
        }

        [RelayCommand]
        private void SetThrottleRawMaxToCurrent()
        {
            ThrottleRawMax = _reader.RawThrottleInt;
        }

        [RelayCommand]
        private void StartAutoCal(string target)
        {
            if (_autoCalibrating) return;
            _calTarget = int.Parse(target);
            _autoCalibrating = true;
            IsAutoCalibrating = true;
            _calPhase = "countdown";
            _phaseStart = DateTime.Now;
            _captureSamples.Clear();
            CalStatus = _calTarget == 0
                ? "Release brake completely... 3..."
                : "Release throttle completely... 3...";
        }

        [RelayCommand]
        private void OpenCurveEditor()
        {
            var editor = new Views.CurveEditorWindow(this, isThrottle: false)
            {
                Owner = Application.Current.MainWindow
            };
            editor.ShowDialog();
        }

        [RelayCommand]
        private void OpenThrottleCurveEditor()
        {
            var editor = new Views.CurveEditorWindow(this, isThrottle: true)
            {
                Owner = Application.Current.MainWindow
            };
            editor.ShowDialog();
        }

        [RelayCommand]
        private void SaveToPico()
        {
            if (string.IsNullOrEmpty(_drivePath))
            {
                MessageBox.Show("No CIRCUITPY drive found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var cal = BuildCalibrationData();
            if (CalibrationFileService.SaveCalibration(_drivePath, cal))
            {
                // Attempt auto-reset
                bool resetOk = HidReset.ResetPico();
                if (resetOk)
                {
                    MessageBox.Show("Calibration saved and Pico reset successfully!", "Success",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("Calibration saved. Press RESET on Pico to apply.",
                        "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            else
            {
                MessageBox.Show("Failed to write calibration.json to Pico drive.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void RefreshProfiles()
        {
            Profiles.Clear();
            foreach (var name in CalibrationFileService.ListProfiles(_drivePath))
                Profiles.Add(name);
        }

        [RelayCommand]
        private void SaveProfile()
        {
            var name = PromptDialog("Profile name:", "Save Profile");
            if (string.IsNullOrWhiteSpace(name)) return;
            var cal = BuildCalibrationData();
            if (CalibrationFileService.SaveProfile(_drivePath, name, cal))
                RefreshProfiles();
            else
                MessageBox.Show("Failed to save profile.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        [RelayCommand]
        private void LoadProfile()
        {
            if (SelectedProfileIndex < 0 || SelectedProfileIndex >= Profiles.Count) return;
            var name = Profiles[SelectedProfileIndex];
            var cal = CalibrationFileService.LoadProfile(_drivePath, name);
            if (cal != null)
            {
                ApplyCalibrationData(cal);
                MessageBox.Show($"Profile '{name}' loaded. Save to Pico to activate.",
                    "Profile Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        [RelayCommand]
        private void DeleteProfile()
        {
            if (SelectedProfileIndex < 0 || SelectedProfileIndex >= Profiles.Count) return;
            var name = Profiles[SelectedProfileIndex];
            if (MessageBox.Show($"Delete profile '{name}'?", "Delete Profile",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                CalibrationFileService.DeleteProfile(_drivePath, name);
                RefreshProfiles();
            }
        }

        [RelayCommand]
        private void LoadFromPico()
        {
            var cal = CalibrationFileService.LoadCalibration(_drivePath);
            if (cal != null)
            {
                ApplyCalibrationData(cal);
                MessageBox.Show("Calibration loaded from Pico.", "Loaded",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("No calibration.json found on Pico drive.",
                    "Not Found", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region Poll Loop

        private int _tickCounter;

        private void OnPollTick(object? sender, EventArgs e)
        {
            _reader.Update();

            // Process auto-calibration state machine
            if (_autoCalibrating)
                ProcessAutoCal();

            if (!_reader.IsConnected) return;

            // Read raw values
            double rawBrake = _reader.RawBrake;
            double procBrake = _reader.ProcessedBrake;
            double rawThrottle = _reader.RawThrottle;
            double procThrottle = _reader.ProcessedThrottle;

            // Update debug info on first few ticks
            if (_tickCounter < 5)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Tick {_tickCounter}: RawBrake={rawBrake:F4} ProcBrake={procBrake:F4} " +
                    $"RawThrottle={rawThrottle:F4} ProcThrottle={procThrottle:F4}");
            }
            
            // Show axis debug info in status
            if (!string.IsNullOrEmpty(_reader.AxisDebugInfo))
                AxisDebugText = _reader.AxisDebugInfo;

            // Compute preview (local pipeline)
            var brakeCal = GetCurrentBrakeCal();
            double previewBrake = Models.SignalProcessing.Process(rawBrake, brakeCal, ref _brakeEma, ref _brakeEmaInit);

            double previewThrottle = 0;
            if (ThrottleEnabled)
            {
                var throttleCal = GetCurrentThrottleCal();
                previewThrottle = Models.SignalProcessing.Process(rawThrottle, throttleCal, ref _throttleEma, ref _throttleEmaInit);
            }

            // Append to chart directly (rolling update — no clear/rebuild)
            _tickCounter++;

            // Add new points to each series
            AddPoint(Series, 0, _tickCounter, rawBrake);
            AddPoint(Series, 1, _tickCounter, previewBrake);
            AddPoint(Series, 2, _tickCounter, procBrake);

            if (ThrottleEnabled)
            {
                AddPoint(ThrottleSeries, 0, _tickCounter, rawThrottle);
                AddPoint(ThrottleSeries, 1, _tickCounter, previewThrottle);
            }

            // Trim old points when buffer exceeds limit
            if (_tickCounter > HistoryLength + 50)
            {
                int trimTo = HistoryLength;
                TrimSeries(Series, trimTo);
                if (ThrottleEnabled)
                    TrimSeries(ThrottleSeries, trimTo);

                // Reset tick counter to avoid overflow and keep X axis manageable
                _tickCounter = HistoryLength;
                ReindexSeries(Series);
                if (ThrottleEnabled)
                    ReindexSeries(ThrottleSeries);
            }

            // Update X axis to follow the data
            int xMin = Math.Max(0, _tickCounter - HistoryLength);
            int xMax = Math.Max(HistoryLength, _tickCounter);
            BrakeXAxis[0].MinLimit = xMin;
            BrakeXAxis[0].MaxLimit = xMax;
            ThrottleXAxis[0].MinLimit = xMin;
            ThrottleXAxis[0].MaxLimit = xMax;
        }

        private static void AddPoint(ObservableCollection<ISeries> seriesCollection, int seriesIndex, double x, double y)
        {
            if (seriesIndex < seriesCollection.Count && seriesCollection[seriesIndex] is LineSeries<ObservablePoint> ls)
            {
                var vals = (ObservableCollection<ObservablePoint>)ls.Values!;
                vals.Add(new ObservablePoint(x, y));
            }
        }

        private static void TrimSeries(ObservableCollection<ISeries> seriesCollection, int maxPoints)
        {
            foreach (var s in seriesCollection)
            {
                if (s is LineSeries<ObservablePoint> ls)
                {
                    var vals = (ObservableCollection<ObservablePoint>)ls.Values!;
                    while (vals.Count > maxPoints)
                        vals.RemoveAt(0);
                }
            }
        }

        private static void ReindexSeries(ObservableCollection<ISeries> seriesCollection)
        {
            foreach (var s in seriesCollection)
            {
                if (s is LineSeries<ObservablePoint> ls)
                {
                    var vals = (ObservableCollection<ObservablePoint>)ls.Values!;
                    for (int i = 0; i < vals.Count; i++)
                        vals[i] = new ObservablePoint(i, vals[i].Y ?? 0);
                }
            }
        }

        #endregion

        #region Auto-Calibration

        private void ProcessAutoCal()
        {
            var elapsed = (DateTime.Now - _phaseStart).TotalSeconds;

            if (_calPhase == "countdown")
            {
                int remaining = 3 - (int)elapsed;
                if (remaining <= 0)
                {
                    _calPhase = "capture";
                    _phaseStart = DateTime.Now;
                    _captureSamples.Clear();
                    CalStatus = _calTarget == 0 ? "Capturing brake... 5s" : "Capturing throttle... 5s";
                }
                else
                {
                    string target = _calTarget == 0 ? "brake" : "throttle";
                    CalStatus = $"Release {target} completely... {remaining}...";
                }
            }
            else if (_calPhase == "capture")
            {
                int remaining = 5 - (int)elapsed;
                if (remaining <= 0)
                {
                    // Compute min/max
                    if (_captureSamples.Count > 0)
                    {
                        int capturedMin = _captureSamples.Min();
                        int capturedMax = _captureSamples.Max();

                        if (_calTarget == 0)
                        {
                            RawMin = capturedMin;
                            RawMax = capturedMax;
                        }
                        else
                        {
                            ThrottleRawMin = capturedMin;
                            ThrottleRawMax = capturedMax;
                        }
                    }

                    _autoCalibrating = false;
                    IsAutoCalibrating = false;
                    _calPhase = "idle";
                    CalStatus = "";
                }
                else
                {
                    // Capture sample
                    int rawInt = _calTarget == 0 ? _reader.RawBrakeInt : _reader.RawThrottleInt;
                    _captureSamples.Add(rawInt);
                    string target = _calTarget == 0 ? "brake" : "throttle";
                    CalStatus = $"Capturing {target}... {remaining}s remaining";
                }
            }
        }

        #endregion

        #region Calibration Data Conversion

        public ChannelCal GetCurrentBrakeCal() => new()
        {
            RawMin = RawMin, RawMax = RawMax, Deadzone = Deadzone,
            Curve = Curve, ProgressivePower = ProgressivePower, AggressivePower = AggressivePower,
            Smoothing = Smoothing, Invert = InvertBrake, Oversample = Oversample,
            Saturation = Saturation, BitePoint = BitePoint,
            CurvePoints = CurvePoints.ConvertAll(p => new List<double>(p))
        };

        public ChannelCal GetCurrentThrottleCal() => new()
        {
            RawMin = ThrottleRawMin, RawMax = ThrottleRawMax, Deadzone = ThrottleDeadzone,
            Curve = ThrottleCurve, ProgressivePower = ThrottleProgressivePower, AggressivePower = ThrottleAggressivePower,
            Smoothing = ThrottleSmoothing, Invert = ThrottleInvert, Oversample = Oversample,
            Saturation = ThrottleSaturation, BitePoint = ThrottleBitePoint,
            CurvePoints = ThrottleCurvePoints.ConvertAll(p => new List<double>(p))
        };

        public CalibrationData BuildCalibrationData() => new()
        {
            RawMin = RawMin, RawMax = RawMax, Deadzone = Deadzone,
            Curve = Curve, ProgressivePower = ProgressivePower, AggressivePower = AggressivePower,
            Smoothing = Smoothing, Invert = InvertBrake, Oversample = Oversample,
            Saturation = Saturation, BitePoint = BitePoint,
            CurvePoints = CurvePoints.ConvertAll(p => new List<double>(p)),
            ThrottleEnabled = ThrottleEnabled, ThrottleSensor = ThrottleSensor,
            ThrottleRawMin = ThrottleRawMin, ThrottleRawMax = ThrottleRawMax,
            ThrottleDeadzone = ThrottleDeadzone, ThrottleCurve = ThrottleCurve,
            ThrottleProgressivePower = ThrottleProgressivePower, ThrottleAggressivePower = ThrottleAggressivePower,
            ThrottleSmoothing = ThrottleSmoothing, ThrottleInvert = ThrottleInvert,
            ThrottleSaturation = ThrottleSaturation, ThrottleBitePoint = ThrottleBitePoint,
            ThrottleCurvePoints = ThrottleCurvePoints.ConvertAll(p => new List<double>(p))
        };

        public void ApplyCalibrationData(CalibrationData cal)
        {
            RawMin = cal.RawMin; RawMax = cal.RawMax; Deadzone = cal.Deadzone;
            Curve = cal.Curve; ProgressivePower = cal.ProgressivePower; AggressivePower = cal.AggressivePower;
            Smoothing = cal.Smoothing; InvertBrake = cal.Invert; Oversample = cal.Oversample;
            Saturation = cal.Saturation; BitePoint = cal.BitePoint;
            CurvePoints = cal.CurvePoints.ConvertAll(p => new List<double>(p));
            ThrottleEnabled = cal.ThrottleEnabled; ThrottleSensor = cal.ThrottleSensor;
            ThrottleRawMin = cal.ThrottleRawMin; ThrottleRawMax = cal.ThrottleRawMax;
            ThrottleDeadzone = cal.ThrottleDeadzone; ThrottleCurve = cal.ThrottleCurve;
            ThrottleProgressivePower = cal.ThrottleProgressivePower; ThrottleAggressivePower = cal.ThrottleAggressivePower;
            ThrottleSmoothing = cal.ThrottleSmoothing; ThrottleInvert = cal.ThrottleInvert;
            ThrottleSaturation = cal.ThrottleSaturation; ThrottleBitePoint = cal.ThrottleBitePoint;
            ThrottleCurvePoints = cal.ThrottleCurvePoints.ConvertAll(p => new List<double>(p));

            // Reset EMA state on calibration change
            _brakeEmaInit = false;
            _throttleEmaInit = false;
        }

        #endregion

        #region Drive Detection

        private void RefreshDrive()
        {
            _drivePath = CircuitPyDrive.FindDrive();
            DriveInfo = _drivePath != null ? $"Drive: {_drivePath}" : "No CIRCUITPY drive found";
            RefreshProfiles();
        }

        #endregion

        #region Helpers

        private static string? PromptDialog(string prompt, string title)
        {
            var input = new Views.InputDialog(prompt, title)
            {
                Owner = Application.Current.MainWindow
            };
            return input.ShowDialog() == true ? input.Answer : null;
        }

        #endregion

        public void Dispose()
        {
            _pollTimer.Stop();
            _driveRefreshTimer?.Stop();
            _reader.Dispose();
        }
    }
}
