using System;

namespace BrakeCalibrator.Models
{
    /// <summary>
    /// Mirrors the firmware's signal processing pipeline for local preview.
    /// All math matches Python calibrator exactly.
    /// </summary>
    public static class SignalProcessing
    {
        /// <summary>
        /// Full pipeline: saturation → clamp → normalize → deadzone → bite → curve → EMA → invert
        /// </summary>
        public static double Process(double raw01, ChannelCal cal, ref double emaState, ref bool emaInit)
        {
            // Step 1: Saturation-adjusted effective max
            int rawMaxEff = cal.RawMin + (int)((cal.RawMax - cal.RawMin) * Math.Min(cal.Saturation, 1.0));

            // Step 2: Convert 0–1 float back to raw integer
            int rawInt = (int)(raw01 * 65535);

            // Step 3: Clamp
            int clamped = Math.Max(cal.RawMin, Math.Min(rawMaxEff, rawInt));

            // Step 4: Normalize
            double normalized = rawMaxEff == cal.RawMin
                ? 0.0
                : (double)(clamped - cal.RawMin) / (rawMaxEff - cal.RawMin);

            // Step 5: Deadzone
            double deadzoneFrac = cal.RawMax != cal.RawMin
                ? (double)cal.Deadzone / (cal.RawMax - cal.RawMin)
                : 0.0;
            if (normalized < deadzoneFrac)
                normalized = 0.0;
            else if (deadzoneFrac > 0)
                normalized = (normalized - deadzoneFrac) / (1.0 - deadzoneFrac);

            // Step 6: Bite point
            if (cal.BitePoint > 0.0)
            {
                if (normalized < cal.BitePoint)
                    normalized = 0.0;
                else
                    normalized = (normalized - cal.BitePoint) / (1.0 - cal.BitePoint);
            }

            // Step 7: Curve
            normalized = ApplyCurve(normalized, cal.Curve, cal.ProgressivePower, cal.AggressivePower, cal.CurvePoints);

            // Step 8: EMA smoothing
            double alpha = 1.0 - Math.Min(cal.Smoothing, 0.95);
            if (!emaInit)
            {
                emaState = normalized;
                emaInit = true;
            }
            else
            {
                emaState = alpha * normalized + (1.0 - alpha) * emaState;
            }
            double result = emaState;

            // Step 9: Invert
            if (cal.Invert)
                result = 1.0 - result;

            return result;
        }

        public static double ApplyCurve(double t, string curve, double progressivePower, double aggressivePower, List<List<double>> curvePoints)
        {
            return curve switch
            {
                "linear" => t,
                "progressive" => Math.Pow(t, progressivePower),
                "aggressive" => Math.Pow(t, 1.0 / aggressivePower),
                "custom" => InterpolateCustom(t, curvePoints),
                _ => t
            };
        }

        /// <summary>
        /// Piecewise-linear interpolation through control points.
        /// Points must be sorted by x. Clamped to first/last point outside range.
        /// </summary>
        public static double InterpolateCustom(double t, List<List<double>> points)
        {
            if (points == null || points.Count < 2) return t;
            if (t <= points[0][0]) return points[0][1];
            if (t >= points[^1][0]) return points[^1][1];

            for (int i = 0; i < points.Count - 1; i++)
            {
                double x0 = points[i][0], y0 = points[i][1];
                double x1 = points[i + 1][0], y1 = points[i + 1][1];
                if (x0 <= t && t <= x1)
                {
                    if (x1 == x0) return y0;
                    double frac = (t - x0) / (x1 - x0);
                    return y0 + frac * (y1 - y0);
                }
            }
            return t;
        }
    }
}
