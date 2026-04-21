using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrakeCalibrator.Models;

namespace BrakeCalibrator.Services
{
    /// <summary>
    /// Handles reading/writing calibration.json and profile management on the CIRCUITPY drive.
    /// </summary>
    public static class CalibrationFileService
    {
        public static string? GetCalibrationPath(string? drivePath)
        {
            if (string.IsNullOrEmpty(drivePath)) return null;
            var path = Path.Combine(drivePath, "calibration.json");
            return File.Exists(path) ? path : null;
        }

        public static CalibrationData? LoadCalibration(string? drivePath)
        {
            var path = GetCalibrationPath(drivePath);
            if (path == null) return null;
            try
            {
                var json = File.ReadAllText(path);
                return CalibrationData.FromJson(json);
            }
            catch { return null; }
        }

        public static bool SaveCalibration(string? drivePath, CalibrationData cal)
        {
            if (string.IsNullOrEmpty(drivePath)) return false;
            try
            {
                var path = Path.Combine(drivePath, "calibration.json");
                File.WriteAllText(path, cal.ToJson());
                return true;
            }
            catch { return false; }
        }

        public static string? GetProfilesDir(string? drivePath)
        {
            if (string.IsNullOrEmpty(drivePath)) return null;
            var dir = Path.Combine(drivePath, "profiles");
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { return null; }
        }

        public static List<string> ListProfiles(string? drivePath)
        {
            var profiles = new List<string>();
            var dir = GetProfilesDir(drivePath);
            if (dir == null) return profiles;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                    profiles.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
            return profiles;
        }

        public static bool SaveProfile(string? drivePath, string name, CalibrationData cal)
        {
            var dir = GetProfilesDir(drivePath);
            if (dir == null) return false;
            try
            {
                // Sanitize name
                name = new string(name.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_').ToArray()).Trim();
                if (string.IsNullOrEmpty(name)) return false;
                var path = Path.Combine(dir, $"{name}.json");
                File.WriteAllText(path, cal.ToJson());
                return true;
            }
            catch { return false; }
        }

        public static CalibrationData? LoadProfile(string? drivePath, string name)
        {
            var dir = GetProfilesDir(drivePath);
            if (dir == null) return null;
            try
            {
                var path = Path.Combine(dir, $"{name}.json");
                if (!File.Exists(path)) return null;
                return CalibrationData.FromJson(File.ReadAllText(path));
            }
            catch { return null; }
        }

        public static bool DeleteProfile(string? drivePath, string name)
        {
            var dir = GetProfilesDir(drivePath);
            if (dir == null) return false;
            try
            {
                var path = Path.Combine(dir, $"{name}.json");
                if (!File.Exists(path)) return false;
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}
