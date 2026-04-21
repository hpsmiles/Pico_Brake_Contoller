using System.Text.RegularExpressions;

namespace BrakeCalibrator;

/// <summary>
/// Manages calibration profiles on the Pico's CIRCUITPY USB drive.
/// Profiles are stored as JSON files in the profiles/ directory.
/// Also handles saving calibration.json to the drive root.
/// </summary>
public class ProfileManager
{
    /// <summary>
    /// Full path to the profiles directory on the Pico drive.
    /// </summary>
    public string ProfilesDir { get; }

    private readonly string _picoDrive;

    public ProfileManager(string picoDrive)
    {
        _picoDrive = picoDrive;
        ProfilesDir = Path.Combine(picoDrive, "profiles");
    }

    /// <summary>
    /// List all profile names (filename without extension), sorted alphabetically.
    /// Returns an empty list if the profiles directory doesn't exist.
    /// </summary>
    public List<string> ListProfiles()
    {
        if (!Directory.Exists(ProfilesDir))
            return new List<string>();

        return Directory.GetFiles(ProfilesDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Save a calibration profile. The name is sanitized to alphanumeric/space/dash/underscore only.
    /// Creates the profiles directory if it doesn't exist.
    /// </summary>
    public void SaveProfile(string name, CalibrationData cal)
    {
        string safeName = SanitizeName(name);
        if (string.IsNullOrWhiteSpace(safeName))
            throw new ArgumentException("Profile name is empty after sanitization.", nameof(name));

        Directory.CreateDirectory(ProfilesDir);
        string filePath = Path.Combine(ProfilesDir, $"{safeName}.json");
        File.WriteAllText(filePath, cal.ToJson());
    }

    /// <summary>
    /// Load a calibration profile by name. Returns null if the profile doesn't exist.
    /// </summary>
    public CalibrationData? LoadProfile(string name)
    {
        string safeName = SanitizeName(name);
        string filePath = Path.Combine(ProfilesDir, $"{safeName}.json");

        if (!File.Exists(filePath))
            return null;

        string json = File.ReadAllText(filePath);
        return CalibrationData.FromJson(json);
    }

    /// <summary>
    /// Delete a profile by name. No-op if the profile doesn't exist.
    /// </summary>
    public void DeleteProfile(string name)
    {
        string safeName = SanitizeName(name);
        string filePath = Path.Combine(ProfilesDir, $"{safeName}.json");

        if (File.Exists(filePath))
            File.Delete(filePath);
    }

    /// <summary>
    /// Save calibration.json to the Pico drive root (overwriting if exists).
    /// After saving, the Pico needs to be RESET for firmware to pick up changes.
    /// </summary>
    public void SaveCalibration(CalibrationData cal)
    {
        string filePath = Path.Combine(_picoDrive, "calibration.json");
        File.WriteAllText(filePath, cal.ToJson());
    }

    /// <summary>
    /// Sanitize a profile name: keep only alphanumeric, space, dash, underscore.
    /// Trim leading/trailing whitespace.
    /// </summary>
    private static string SanitizeName(string name)
    {
        return Regex.Replace(name, @"[^a-zA-Z0-9 \-_]", "").Trim();
    }
}
