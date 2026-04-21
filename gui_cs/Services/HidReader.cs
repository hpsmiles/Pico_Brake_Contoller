using System;
using System.Collections.Generic;
using System.Linq;
using SharpDX.DirectInput;

namespace BrakeCalibrator.Services
{
    /// <summary>
    /// Reads Pico HID gamepad input via DirectInput (same API games use).
    /// 
    /// The Pico's custom HID gamepad has 4 axes:
    ///   Axis 0 (X)  = processed brake
    ///   Axis 1 (Y)  = raw brake ADC  
    ///   Axis 2 (Z)  = processed throttle
    ///   Axis 3 (Rz) = raw throttle ADC
    /// 
    /// DirectInput normalizes axes to -32768..32767 (signed 16-bit).
    /// We map them to 0.0..1.0 matching pygame's (raw+1)/2 mapping.
    /// 
    /// Why DirectInput instead of raw HID:
    ///   Windows exclusively locks game controllers from raw HID access.
    ///   DirectInput is the standard API for reading gamepads (same as pygame uses).
    ///   HidLibrary is kept only for writing Output Reports (reset command).
    /// </summary>
    public class HidReader : IDisposable
    {
        private DirectInput? _directInput;
        private Joystick? _joystick;
        private bool _disposed;
        private readonly object _lock = new();

        // Current axis values (0.0–1.0)
        private double _processedBrake;
        private double _rawBrake;
        private double _processedThrottle;
        private double _rawThrottle;

        public double ProcessedBrake { get { lock (_lock) return _processedBrake; } }
        public double RawBrake { get { lock (_lock) return _rawBrake; } }
        public double ProcessedThrottle { get { lock (_lock) return _processedThrottle; } }
        public double RawThrottle { get { lock (_lock) return _rawThrottle; } }

        public int RawBrakeInt => (int)(RawBrake * 65535);
        public int RawThrottleInt => (int)(RawThrottle * 65535);

        public string DeviceName { get; private set; } = "";
        public bool IsConnected => _joystick != null;

        // Diagnostic: last raw axis readings for debugging
        public string AxisDebugInfo { get; private set; } = "";

        /// <summary>
        /// Lists all connected game controllers via DirectInput.
        /// </summary>
        public List<DeviceInfo> EnumerateGamepads()
        {
            _directInput ??= new DirectInput();
            var devices = new List<DeviceInfo>();

            try
            {
                var gamepads = _directInput.GetDevices(
                    DeviceClass.GameControl,
                    DeviceEnumerationFlags.AllDevices);

                foreach (var dev in gamepads)
                {
                    string name = dev.ProductName ?? "Unknown";
                    bool isPico = name.IndexOf("pico", StringComparison.OrdinalIgnoreCase) >= 0;
                    devices.Add(new DeviceInfo(dev.InstanceGuid, name, isPico));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"EnumerateGamepads error: {ex.Message}");
            }

            return devices;
        }

        public bool Connect(Guid instanceGuid)
        {
            try
            {
                Disconnect();
                _directInput ??= new DirectInput();
                _joystick = new Joystick(_directInput, instanceGuid);

                // Background + NonExclusive: share with other apps, no foreground requirement
                _joystick.SetCooperativeLevel(
                    IntPtr.Zero,
                    CooperativeLevel.Background | CooperativeLevel.NonExclusive);

                _joystick.Acquire();
                DeviceName = _joystick.Information.ProductName;

                System.Diagnostics.Debug.WriteLine(
                    $"HidReader: Connected to {DeviceName}, " +
                    $"Axes={_joystick.Capabilities.AxeCount}, " +
                    $"Buttons={_joystick.Capabilities.ButtonCount}");

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HidReader.Connect error: {ex.Message}");
                _joystick = null;
                return false;
            }
        }

        /// <summary>
        /// Poll the device and update axis values. Called by ViewModel's timer (~30Hz).
        /// </summary>
        public void Update()
        {
            if (_joystick == null || _disposed) return;

            try
            {
                _joystick.Poll();
                var state = _joystick.GetCurrentState();

                // Diagnostics: log all axis values on first few reads
                if (string.IsNullOrEmpty(AxisDebugInfo))
                {
                    AxisDebugInfo = $"Axes={_joystick.Capabilities.AxeCount} " +
                        $"X={state.X} Y={state.Y} Z={state.Z} " +
                        $"Rx={state.RotationX} Ry={state.RotationY} Rz={state.RotationZ} " +
                        $"Sliders={state.Sliders.Length} " +
                        $"POVs={state.PointOfViewControllers.Length}";
                    System.Diagnostics.Debug.WriteLine($"HidReader Axis Dump: {AxisDebugInfo}");
                }

                lock (_lock)
                {
                    // For a 4-axis gamepad, DirectInput maps:
                    //   X  = axis 0 → processed brake
                    //   Y  = axis 1 → raw brake ADC
                    //   Z  = axis 2 → processed throttle  
                    //   RotationZ = axis 5 → raw throttle ADC
                    //
                    // This matches the Pico's HID descriptor (X, Y, Z, Rz).
                    // DirectInput assigns X/Y/Z to first three axes,
                    // then Rx/Ry/Rz for rotation axes.
                    // For a 4-axis device with only X/Y/Z/Rz, 
                    // we typically have AxeCount=6 (X,Y,Z,Rx,Ry,Rz)
                    // where Rx and Ry are unused center values.
                    
                    _processedBrake = MapAxis(state.X);           // Axis 0: processed brake
                    _rawBrake = MapAxis(state.Y);                 // Axis 1: raw brake
                    _processedThrottle = MapAxis(state.Z);        // Axis 2: processed throttle
                    _rawThrottle = MapAxis(state.RotationZ);      // Axis 5: raw throttle
                }
            }
            catch (SharpDX.SharpDXException)
            {
                // Device lost — don't spam, just skip this tick
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HidReader.Update error: {ex.Message}");
            }
        }

        /// <summary>
        /// Map DirectInput axis value to 0.0–1.0.
        /// The Pico HID descriptor uses 16-bit unsigned (0–65535) axes.
        /// DirectInput returns raw values matching the HID logical range.
        /// Matches pygame: get_axis returns -1..1, then (raw+1)/2 = 0..1.
        /// Since DirectInput returns 0..65535 directly, we just divide.
        /// </summary>
        private static double MapAxis(int value)
        {
            return Math.Max(0.0, Math.Min(1.0, value / 65535.0));
        }

        public void Disconnect()
        {
            try
            {
                if (_joystick != null)
                {
                    _joystick.Unacquire();
                    _joystick.Dispose();
                }
            }
            catch { }
            _joystick = null;
            DeviceName = "";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _directInput?.Dispose();
        }
    }

    /// <summary>
    /// Info about a discovered DirectInput game controller.
    /// </summary>
    public class DeviceInfo
    {
        public Guid InstanceGuid { get; }
        public string ProductName { get; }
        public bool IsPico { get; }

        public DeviceInfo(Guid instanceGuid, string productName, bool isPico)
        {
            InstanceGuid = instanceGuid;
            ProductName = productName;
            IsPico = isPico;
        }

        public override string ToString() => IsPico ? $"★ {ProductName}" : ProductName;
    }
}
