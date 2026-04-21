using Newtonsoft.Json;
using System.Collections.Generic;

namespace BrakeCalibrator.Models
{
    public class ChannelCal
    {
        [JsonProperty("raw_min")]
        public int RawMin { get; set; } = 2000;

        [JsonProperty("raw_max")]
        public int RawMax { get; set; } = 56000;

        [JsonProperty("deadzone")]
        public int Deadzone { get; set; } = 300;

        [JsonProperty("curve")]
        public string Curve { get; set; } = "linear";

        [JsonProperty("progressive_power")]
        public double ProgressivePower { get; set; } = 2.0;

        [JsonProperty("aggressive_power")]
        public double AggressivePower { get; set; } = 2.0;

        [JsonProperty("smoothing")]
        public double Smoothing { get; set; } = 0.3;

        [JsonProperty("invert")]
        public bool Invert { get; set; } = false;

        [JsonProperty("oversample")]
        public int Oversample { get; set; } = 16;

        [JsonProperty("saturation")]
        public double Saturation { get; set; } = 1.0;

        [JsonProperty("bite_point")]
        public double BitePoint { get; set; } = 0.0;

        [JsonProperty("curve_points")]
        public List<List<double>> CurvePoints { get; set; } = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } };

        public ChannelCal Clone()
        {
            return new ChannelCal
            {
                RawMin = RawMin,
                RawMax = RawMax,
                Deadzone = Deadzone,
                Curve = Curve,
                ProgressivePower = ProgressivePower,
                AggressivePower = AggressivePower,
                Smoothing = Smoothing,
                Invert = Invert,
                Oversample = Oversample,
                Saturation = Saturation,
                BitePoint = BitePoint,
                CurvePoints = CurvePoints.ConvertAll(p => new List<double>(p))
            };
        }
    }

    public class CalibrationData
    {
        // Brake fields (flat, matching JSON format)
        [JsonProperty("raw_min")]
        public int RawMin { get; set; } = 2000;

        [JsonProperty("raw_max")]
        public int RawMax { get; set; } = 56000;

        [JsonProperty("deadzone")]
        public int Deadzone { get; set; } = 300;

        [JsonProperty("curve")]
        public string Curve { get; set; } = "linear";

        [JsonProperty("progressive_power")]
        public double ProgressivePower { get; set; } = 2.0;

        [JsonProperty("aggressive_power")]
        public double AggressivePower { get; set; } = 2.0;

        [JsonProperty("smoothing")]
        public double Smoothing { get; set; } = 0.3;

        [JsonProperty("invert")]
        public bool Invert { get; set; } = false;

        [JsonProperty("oversample")]
        public int Oversample { get; set; } = 16;

        [JsonProperty("saturation")]
        public double Saturation { get; set; } = 1.0;

        [JsonProperty("bite_point")]
        public double BitePoint { get; set; } = 0.0;

        [JsonProperty("curve_points")]
        public List<List<double>> CurvePoints { get; set; } = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } };

        // Throttle fields
        [JsonProperty("throttle_enabled")]
        public bool ThrottleEnabled { get; set; } = false;

        [JsonProperty("throttle_sensor")]
        public string ThrottleSensor { get; set; } = "auto";

        [JsonProperty("throttle_raw_min")]
        public int ThrottleRawMin { get; set; } = 2000;

        [JsonProperty("throttle_raw_max")]
        public int ThrottleRawMax { get; set; } = 56000;

        [JsonProperty("throttle_deadzone")]
        public int ThrottleDeadzone { get; set; } = 300;

        [JsonProperty("throttle_curve")]
        public string ThrottleCurve { get; set; } = "linear";

        [JsonProperty("throttle_progressive_power")]
        public double ThrottleProgressivePower { get; set; } = 2.0;

        [JsonProperty("throttle_aggressive_power")]
        public double ThrottleAggressivePower { get; set; } = 2.0;

        [JsonProperty("throttle_smoothing")]
        public double ThrottleSmoothing { get; set; } = 0.2;

        [JsonProperty("throttle_invert")]
        public bool ThrottleInvert { get; set; } = false;

        [JsonProperty("throttle_saturation")]
        public double ThrottleSaturation { get; set; } = 1.0;

        [JsonProperty("throttle_bite_point")]
        public double ThrottleBitePoint { get; set; } = 0.0;

        [JsonProperty("throttle_curve_points")]
        public List<List<double>> ThrottleCurvePoints { get; set; } = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } };

        public ChannelCal GetBrakeChannel() => new()
        {
            RawMin = RawMin, RawMax = RawMax, Deadzone = Deadzone,
            Curve = Curve, ProgressivePower = ProgressivePower, AggressivePower = AggressivePower,
            Smoothing = Smoothing, Invert = Invert, Oversample = Oversample,
            Saturation = Saturation, BitePoint = BitePoint,
            CurvePoints = CurvePoints.ConvertAll(p => new List<double>(p))
        };

        public ChannelCal GetThrottleChannel() => new()
        {
            RawMin = ThrottleRawMin, RawMax = ThrottleRawMax, Deadzone = ThrottleDeadzone,
            Curve = ThrottleCurve, ProgressivePower = ThrottleProgressivePower, AggressivePower = ThrottleAggressivePower,
            Smoothing = ThrottleSmoothing, Invert = ThrottleInvert, Oversample = Oversample,
            Saturation = ThrottleSaturation, BitePoint = ThrottleBitePoint,
            CurvePoints = ThrottleCurvePoints.ConvertAll(p => new List<double>(p))
        };

        public void ApplyBrakeChannel(ChannelCal ch)
        {
            RawMin = ch.RawMin; RawMax = ch.RawMax; Deadzone = ch.Deadzone;
            Curve = ch.Curve; ProgressivePower = ch.ProgressivePower; AggressivePower = ch.AggressivePower;
            Smoothing = ch.Smoothing; Invert = ch.Invert; Oversample = ch.Oversample;
            Saturation = ch.Saturation; BitePoint = ch.BitePoint;
            CurvePoints = ch.CurvePoints.ConvertAll(p => new List<double>(p));
        }

        public void ApplyThrottleChannel(ChannelCal ch)
        {
            ThrottleRawMin = ch.RawMin; ThrottleRawMax = ch.RawMax; ThrottleDeadzone = ch.Deadzone;
            ThrottleCurve = ch.Curve; ThrottleProgressivePower = ch.ProgressivePower; ThrottleAggressivePower = ch.AggressivePower;
            ThrottleSmoothing = ch.Smoothing; ThrottleInvert = ch.Invert;
            ThrottleSaturation = ch.Saturation; ThrottleBitePoint = ch.BitePoint;
            ThrottleCurvePoints = ch.CurvePoints.ConvertAll(p => new List<double>(p));
        }

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);

        public static CalibrationData? FromJson(string json) => JsonConvert.DeserializeObject<CalibrationData>(json);

        public CalibrationData Clone()
        {
            var json = ToJson();
            return FromJson(json)!;
        }
    }

    public static class CurvePresets
    {
        public static readonly Dictionary<string, List<List<double>>> Presets = new()
        {
            ["linear"] = new() { new() { 0.0, 0.0 }, new() { 1.0, 1.0 } },
            ["progressive"] = new() { new() { 0.0, 0.0 }, new() { 0.25, 0.06 }, new() { 0.5, 0.25 }, new() { 0.75, 0.56 }, new() { 1.0, 1.0 } },
            ["aggressive"] = new() { new() { 0.0, 0.0 }, new() { 0.25, 0.44 }, new() { 0.5, 0.75 }, new() { 0.75, 0.94 }, new() { 1.0, 1.0 } },
            ["S-curve"] = new() { new() { 0.0, 0.0 }, new() { 0.25, 0.1 }, new() { 0.5, 0.5 }, new() { 0.75, 0.9 }, new() { 1.0, 1.0 } },
        };
    }
}
