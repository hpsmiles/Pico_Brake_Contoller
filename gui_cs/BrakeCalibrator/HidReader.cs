using System.Collections.Generic;
using System.Linq;
using Linearstar.Windows.RawInput;

namespace BrakeCalibrator;

/// <summary>
/// Wraps RawInput.Sharp to enumerate and read from HID gamepads.
/// Replaces the Python PicoReader class. Reads 4 axes from the Pico's
/// HID report: X=processed brake, Y=raw brake ADC, Z=processed throttle, Rz=raw throttle ADC.
/// </summary>
public class HidReader
{
    private const int WM_INPUT = 0x00FF;
    private const int AxisCount = 4;

    private readonly int[] _axisValues = new int[AxisCount];
    private List<RawInputHid> _devices = new();
    private int _selectedIndex = -1;

    /// <summary>Name of currently selected device, or empty string.</summary>
    public string SelectedDeviceName =>
        _selectedIndex >= 0 && _selectedIndex < _devices.Count
            ? _devices[_selectedIndex].ProductName ?? ""
            : "";

    /// <summary>Index of currently selected device in the scanned list, or -1.</summary>
    public int SelectedDeviceIndex => _selectedIndex;

    /// <summary>Register for raw HID gamepad input. Call after the window handle is created.</summary>
    public void Register(IntPtr windowHandle)
    {
        RawInputDevice.RegisterDevice(
            HidUsageAndPage.GamePad,
            RawInputDeviceFlags.InputSink,
            windowHandle);
    }

    /// <summary>Enumerate connected HID gamepad devices.</summary>
    public void ScanDevices()
    {
        _devices = RawInputDevice.GetDevices()
            .OfType<RawInputHid>()
            .Where(d => d.UsageAndPage.Equals(HidUsageAndPage.GamePad))
            .ToList();

        if (_selectedIndex >= _devices.Count)
            _selectedIndex = -1;
    }

    /// <summary>Auto-select device with "pico" in name, fallback to first.</summary>
    public void AutoSelectPico()
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].ProductName is string name
                && name.Contains("pico", StringComparison.OrdinalIgnoreCase))
            {
                SelectDevice(i);
                return;
            }
        }

        if (_devices.Count > 0)
            SelectDevice(0);
    }

    /// <summary>Select device by index from the scanned list.</summary>
    public void SelectDevice(int index)
    {
        if (index < 0 || index >= _devices.Count)
            return;
        _selectedIndex = index;
        Array.Clear(_axisValues);
    }

    /// <summary>Return list of device names for ComboBox display.</summary>
    public List<string> GetDeviceNames()
        => _devices.Select(d => d.ProductName ?? d.DevicePath ?? "(unknown)").ToList();

    /// <summary>
    /// Process a window message. Call from WndProc for WM_INPUT messages.
    /// Parses HID report via ValueSetStates (or falls back to raw bytes for 8-byte Pico reports).
    /// </summary>
    public void ProcessMessage(ref Message m)
    {
        if (m.Msg != WM_INPUT)
            return;

        var data = RawInputData.FromHandle(m.LParam);
        if (data is not RawInputHidData hidData)
            return;

        // Try structured value parsing first
        var values = new List<int>();
        foreach (var vss in hidData.ValueSetStates)
        {
            foreach (var val in vss.CurrentValues)
                values.Add(val);
        }

        if (values.Count >= AxisCount)
        {
            for (int i = 0; i < AxisCount; i++)
                _axisValues[i] = values[i];
            return;
        }

        // Fallback: parse raw 8-byte report as 4 × uint16 LE (Pico custom descriptor)
        byte[] raw = hidData.Hid.RawData;
        if (raw != null && raw.Length >= 8)
        {
            for (int i = 0; i < AxisCount; i++)
                _axisValues[i] = BitConverter.ToUInt16(raw, i * 2);
        }
    }

    // ── Axis read helpers ──────────────────────────────────────────────

    /// <summary>Processed brake value as float 0.0-1.0 (axis 0 = X).</summary>
    public float ReadBrake() => ToFloat(0);

    /// <summary>Raw brake ADC as float 0.0-1.0 (axis 1 = Y).</summary>
    public float ReadRawAdc() => ToFloat(1);

    /// <summary>Raw brake ADC as integer 0-65535 for calibration (axis 1 = Y).</summary>
    public int ReadRawAdcInt() => _axisValues[1];

    /// <summary>Processed throttle value as float 0.0-1.0 (axis 2 = Z).</summary>
    public float ReadThrottle() => ToFloat(2);

    /// <summary>Raw throttle ADC as float 0.0-1.0 (axis 3 = Rz).</summary>
    public float ReadThrottleRaw() => ToFloat(3);

    /// <summary>Raw throttle ADC as integer 0-65535 (axis 3 = Rz).</summary>
    public int ReadThrottleRawInt() => _axisValues[3];

    // ── Private ────────────────────────────────────────────────────────

    private float ToFloat(int axis)
        => _axisValues[axis] / 65535f;
}
