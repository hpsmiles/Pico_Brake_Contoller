using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrakeCalibrator;

/// <summary>
/// Per-channel calibration state and signal processing pipeline.
/// Mirrors the Python firmware pipeline exactly:
/// clamp → normalize → deadzone → bite → curve → EMA → invert
/// </summary>
public class ChannelCal
{
    // Calibration parameters
    public int RawMin { get; set; } = 2000;
    public int RawMax { get; set; } = 56000;
    public int Deadzone { get; set; } = 300;
    public string Curve { get; set; } = "linear";
    public double ProgressivePower { get; set; } = 2.0;
    public double AggressivePower { get; set; } = 2.0;
    public double Smoothing { get; set; } = 0.3;
    public bool Invert { get; set; } = false;
    public double Saturation { get; set; } = 1.0;
    public double BitePoint { get; set; } = 0.0;
    public List<double[]> CurvePoints { get; set; } = new() { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } };

    // EMA state (not serialized, preserved across SyncToChannels)
    private double _ema;
    private bool _emaInit;

    /// <summary>
    /// Process a raw ADC integer value (0-65535) through the full signal pipeline.
    /// Returns a float in the range 0.0-1.0.
    /// </summary>
    public double ProcessRaw(int rawAdc)
    {
        // Step 1: Compute effective max with saturation
        double rawMaxEff = RawMin + (RawMax - RawMin) * Math.Min(Saturation, 1.0);

        // Step 2: Clamp
        double clamped = Math.Max(RawMin, Math.Min(rawMaxEff, rawAdc));

        // Step 3: Normalize (0-1)
        double normalized;
        if (rawMaxEff == RawMin)
        {
            normalized = 0.0;
        }
        else
        {
            normalized = (clamped - RawMin) / (rawMaxEff - RawMin);
        }

        // Step 4: Deadzone
        double deadzoneFrac = RawMax != RawMin ? (double)Deadzone / (RawMax - RawMin) : 0.0;
        if (normalized < deadzoneFrac)
        {
            normalized = 0.0;
        }
        else if (deadzoneFrac > 0)
        {
            normalized = (normalized - deadzoneFrac) / (1.0 - deadzoneFrac);
        }

        // Step 5: Bite point
        if (BitePoint > 0.0)
        {
            if (normalized < BitePoint)
            {
                normalized = 0.0;
            }
            else
            {
                normalized = (normalized - BitePoint) / (1.0 - BitePoint);
            }
        }

        // Step 6: Curve
        normalized = ApplyCurve(normalized);

        // Step 7: EMA (alpha = 1 - smoothing; smoothing capped at 0.95)
        double alpha = 1.0 - Math.Min(Smoothing, 0.95);
        if (!_emaInit)
        {
            _ema = normalized;
            _emaInit = true;
        }
        else
        {
            _ema = alpha * normalized + (1.0 - alpha) * _ema;
        }

        // Step 8: Invert
        return Invert ? 1.0 - _ema : _ema;
    }

    /// <summary>
    /// Apply the configured curve to a normalized value (0-1).
    /// </summary>
    public double ApplyCurve(double t)
    {
        return Curve switch
        {
            "progressive" => Math.Pow(t, ProgressivePower),
            "aggressive" => Math.Pow(t, 1.0 / AggressivePower),
            "custom" => InterpolateCustom(t, CurvePoints),
            _ => t // "linear" or fallback
        };
    }

    /// <summary>
    /// Piecewise-linear interpolation through control points.
    /// Mirrors Python _interpolate_custom exactly.
    /// </summary>
    public static double InterpolateCustom(double t, List<double[]> points)
    {
        if (points == null || points.Count < 2)
            return t;
        if (t <= points[0][0])
            return points[0][1];
        if (t >= points[points.Count - 1][0])
            return points[points.Count - 1][1];
        for (int i = 0; i < points.Count - 1; i++)
        {
            double x0 = points[i][0], y0 = points[i][1];
            double x1 = points[i + 1][0], y1 = points[i + 1][1];
            if (x0 <= t && t <= x1)
            {
                if (x1 == x0)
                    return y0;
                double frac = (t - x0) / (x1 - x0);
                return y0 + frac * (y1 - y0);
            }
        }
        return t;
    }

    /// <summary>
    /// Reset the EMA smoothing state. Call when calibration parameters change
    /// and you want the filter to re-initialize on the next sample.
    /// </summary>
    public void ResetEma()
    {
        _emaInit = false;
        _ema = 0.0;
    }

    /// <summary>
    /// Create a deep copy of this channel calibration (EMA state is reset in the clone).
    /// </summary>
    public ChannelCal Clone()
    {
        var clone = new ChannelCal
        {
            RawMin = RawMin,
            RawMax = RawMax,
            Deadzone = Deadzone,
            Curve = Curve,
            ProgressivePower = ProgressivePower,
            AggressivePower = AggressivePower,
            Smoothing = Smoothing,
            Invert = Invert,
            Saturation = Saturation,
            BitePoint = BitePoint,
            CurvePoints = CurvePoints.Select(p => new[] { p[0], p[1] }).ToList(),
        };
        // EMA state intentionally not copied — clone starts fresh
        return clone;
    }
}

/// <summary>
/// Flat property model matching calibration.json format exactly.
/// Contains two ChannelCal instances for live processing, plus flat properties
/// for JSON serialization. SyncToChannels/SyncFromChannels bridges between them.
/// </summary>
public class CalibrationData
{
    // ── Brake flat properties (serialized to JSON) ──

    [JsonPropertyName("raw_min")]
    public int RawMin { get; set; } = 2000;

    [JsonPropertyName("raw_max")]
    public int RawMax { get; set; } = 56000;

    [JsonPropertyName("deadzone")]
    public int Deadzone { get; set; } = 300;

    [JsonPropertyName("curve")]
    public string Curve { get; set; } = "linear";

    [JsonPropertyName("progressive_power")]
    public double ProgressivePower { get; set; } = 2.0;

    [JsonPropertyName("aggressive_power")]
    public double AggressivePower { get; set; } = 2.0;

    [JsonPropertyName("smoothing")]
    public double Smoothing { get; set; } = 0.3;

    [JsonPropertyName("invert")]
    public bool Invert { get; set; } = false;

    [JsonPropertyName("oversample")]
    public int Oversample { get; set; } = 16;

    [JsonPropertyName("saturation")]
    public double Saturation { get; set; } = 1.0;

    [JsonPropertyName("bite_point")]
    public double BitePoint { get; set; } = 0.0;

    [JsonPropertyName("curve_points")]
    public List<double[]> CurvePoints { get; set; } = new() { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } };

    // ── Throttle flat properties (serialized to JSON) ──

    [JsonPropertyName("throttle_enabled")]
    public bool ThrottleEnabled { get; set; } = false;

    [JsonPropertyName("throttle_sensor")]
    public string ThrottleSensor { get; set; } = "auto";

    [JsonPropertyName("throttle_raw_min")]
    public int ThrottleRawMin { get; set; } = 2000;

    [JsonPropertyName("throttle_raw_max")]
    public int ThrottleRawMax { get; set; } = 56000;

    [JsonPropertyName("throttle_deadzone")]
    public int ThrottleDeadzone { get; set; } = 300;

    [JsonPropertyName("throttle_curve")]
    public string ThrottleCurve { get; set; } = "linear";

    [JsonPropertyName("throttle_progressive_power")]
    public double ThrottleProgressivePower { get; set; } = 2.0;

    [JsonPropertyName("throttle_aggressive_power")]
    public double ThrottleAggressivePower { get; set; } = 2.0;

    [JsonPropertyName("throttle_smoothing")]
    public double ThrottleSmoothing { get; set; } = 0.2;

    [JsonPropertyName("throttle_invert")]
    public bool ThrottleInvert { get; set; } = false;

    [JsonPropertyName("throttle_saturation")]
    public double ThrottleSaturation { get; set; } = 1.0;

    [JsonPropertyName("throttle_bite_point")]
    public double ThrottleBitePoint { get; set; } = 0.0;

    [JsonPropertyName("throttle_curve_points")]
    public List<double[]> ThrottleCurvePoints { get; set; } = new() { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } };

    // ── Channel instances for live signal processing ──

    /// <summary>
    /// Brake channel with EMA state — use ProcessRaw() for the preview line.
    /// </summary>
    [JsonIgnore]
    public ChannelCal BrakeChannel { get; } = new();

    /// <summary>
    /// Throttle channel with EMA state — use ProcessRaw() for the preview line.
    /// </summary>
    [JsonIgnore]
    public ChannelCal ThrottleChannel { get; } = new();

    /// <summary>
    /// Copy flat properties TO the ChannelCal instances (preserving EMA state).
    /// Call this when slider values change, before calling ProcessRaw().
    /// </summary>
    public void SyncToChannels()
    {
        // Brake
        BrakeChannel.RawMin = RawMin;
        BrakeChannel.RawMax = RawMax;
        BrakeChannel.Deadzone = Deadzone;
        BrakeChannel.Curve = Curve;
        BrakeChannel.ProgressivePower = ProgressivePower;
        BrakeChannel.AggressivePower = AggressivePower;
        BrakeChannel.Smoothing = Smoothing;
        BrakeChannel.Invert = Invert;
        BrakeChannel.Saturation = Saturation;
        BrakeChannel.BitePoint = BitePoint;
        BrakeChannel.CurvePoints = CurvePoints.Select(p => new[] { p[0], p[1] }).ToList();

        // Throttle
        ThrottleChannel.RawMin = ThrottleRawMin;
        ThrottleChannel.RawMax = ThrottleRawMax;
        ThrottleChannel.Deadzone = ThrottleDeadzone;
        ThrottleChannel.Curve = ThrottleCurve;
        ThrottleChannel.ProgressivePower = ThrottleProgressivePower;
        ThrottleChannel.AggressivePower = ThrottleAggressivePower;
        ThrottleChannel.Smoothing = ThrottleSmoothing;
        ThrottleChannel.Invert = ThrottleInvert;
        ThrottleChannel.Saturation = ThrottleSaturation;
        ThrottleChannel.BitePoint = ThrottleBitePoint;
        ThrottleChannel.CurvePoints = ThrottleCurvePoints.Select(p => new[] { p[0], p[1] }).ToList();
    }

    /// <summary>
    /// Copy channel values back to flat properties (for serialization after channel edits).
    /// EMA state is NOT copied back — it's not part of the JSON format.
    /// </summary>
    public void SyncFromChannels()
    {
        // Brake
        RawMin = BrakeChannel.RawMin;
        RawMax = BrakeChannel.RawMax;
        Deadzone = BrakeChannel.Deadzone;
        Curve = BrakeChannel.Curve;
        ProgressivePower = Math.Round(BrakeChannel.ProgressivePower, 1);
        AggressivePower = Math.Round(BrakeChannel.AggressivePower, 1);
        Smoothing = Math.Round(BrakeChannel.Smoothing, 2);
        Invert = BrakeChannel.Invert;
        Saturation = Math.Round(BrakeChannel.Saturation, 2);
        BitePoint = Math.Round(BrakeChannel.BitePoint, 2);
        CurvePoints = BrakeChannel.CurvePoints.Select(p => new[] { p[0], p[1] }).ToList();

        // Throttle
        ThrottleRawMin = ThrottleChannel.RawMin;
        ThrottleRawMax = ThrottleChannel.RawMax;
        ThrottleDeadzone = ThrottleChannel.Deadzone;
        ThrottleCurve = ThrottleChannel.Curve;
        ThrottleProgressivePower = Math.Round(ThrottleChannel.ProgressivePower, 1);
        ThrottleAggressivePower = Math.Round(ThrottleChannel.AggressivePower, 1);
        ThrottleSmoothing = Math.Round(ThrottleChannel.Smoothing, 2);
        ThrottleInvert = ThrottleChannel.Invert;
        ThrottleSaturation = Math.Round(ThrottleChannel.Saturation, 2);
        ThrottleBitePoint = Math.Round(ThrottleChannel.BitePoint, 2);
        ThrottleCurvePoints = ThrottleChannel.CurvePoints.Select(p => new[] { p[0], p[1] }).ToList();
    }

    // ── JSON serialization ──

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // use [JsonPropertyName] attributes
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Serialize this calibration data to JSON string (indented, matching Python json.dump format).
    /// Applies rounding rules from Python _build_cal_dict.
    /// </summary>
    public string ToJson()
    {
        // Create a serialization copy with rounding applied (matching Python _build_cal_dict)
        var data = new CalibrationData
        {
            RawMin = RawMin,
            RawMax = RawMax,
            Deadzone = Deadzone,
            Curve = Curve,
            ProgressivePower = Math.Round(ProgressivePower, 1),
            AggressivePower = Math.Round(AggressivePower, 1),
            Smoothing = Math.Round(Smoothing, 2),
            Invert = Invert,
            Oversample = Oversample,
            Saturation = Math.Round(Saturation, 2),
            BitePoint = Math.Round(BitePoint, 2),
            CurvePoints = (CurvePoints ?? new List<double[]>()).Select(p => new[] { p[0], p[1] }).ToList(),
            ThrottleEnabled = ThrottleEnabled,
            ThrottleSensor = ThrottleSensor,
            ThrottleRawMin = ThrottleRawMin,
            ThrottleRawMax = ThrottleRawMax,
            ThrottleDeadzone = ThrottleDeadzone,
            ThrottleCurve = ThrottleCurve,
            ThrottleProgressivePower = Math.Round(ThrottleProgressivePower, 1),
            ThrottleAggressivePower = Math.Round(ThrottleAggressivePower, 1),
            ThrottleSmoothing = Math.Round(ThrottleSmoothing, 2),
            ThrottleInvert = ThrottleInvert,
            ThrottleSaturation = Math.Round(ThrottleSaturation, 2),
            ThrottleBitePoint = Math.Round(ThrottleBitePoint, 2),
            ThrottleCurvePoints = (ThrottleCurvePoints ?? new List<double[]>()).Select(p => new[] { p[0], p[1] }).ToList(),
        };
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    /// <summary>
    /// Deserialize calibration data from a JSON string.
    /// Missing fields fall back to defaults (matching Python _apply_cal_dict behavior).
    /// Also syncs the parsed values to the channel instances.
    /// </summary>
    public static CalibrationData FromJson(string json)
    {
        var data = JsonSerializer.Deserialize<CalibrationData>(json, JsonOptions) ?? new CalibrationData();
        data.SyncToChannels();
        return data;
    }

    // ── Static presets (matching Python DEFAULTS, CURVES, CURVE_PRESETS, OVERSAMPLE_OPTIONS) ──

    /// <summary>
    /// All supported curve type names, matching Python CURVES list.
    /// </summary>
    public static readonly string[] CurveTypes = { "linear", "progressive", "aggressive", "custom" };

    /// <summary>
    /// Oversample options matching Python OVERSAMPLE_OPTIONS.
    /// </summary>
    public static readonly int[] OversampleOptions = { 1, 4, 16, 64 };

    /// <summary>
    /// Curve presets for the four preset buttons, matching Python CURVE_PRESETS exactly.
    /// Key = preset name, Value = list of [x, y] control points.
    /// </summary>
    public static readonly Dictionary<string, List<double[]>> CurvePresets = new()
    {
        ["linear"] = new() { new[] { 0.0, 0.0 }, new[] { 1.0, 1.0 } },
        ["progressive"] = new() { new[] { 0.0, 0.0 }, new[] { 0.25, 0.06 }, new[] { 0.5, 0.25 }, new[] { 0.75, 0.56 }, new[] { 1.0, 1.0 } },
        ["aggressive"] = new() { new[] { 0.0, 0.0 }, new[] { 0.25, 0.44 }, new[] { 0.5, 0.75 }, new[] { 0.75, 0.94 }, new[] { 1.0, 1.0 } },
        ["S-curve"] = new() { new[] { 0.0, 0.0 }, new[] { 0.25, 0.1 }, new[] { 0.5, 0.5 }, new[] { 0.75, 0.9 }, new[] { 1.0, 1.0 } },
    };

    /// <summary>
    /// Create a new CalibrationData with all default values, matching Python DEFAULTS dict.
    /// </summary>
    public static CalibrationData CreateDefaults()
    {
        var data = new CalibrationData();
        data.SyncToChannels();
        return data;
    }
}
