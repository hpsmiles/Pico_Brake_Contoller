using System.Runtime.InteropServices;
using System.Text;

namespace BrakeCalibrator;

/// <summary>
/// Finds the CIRCUITPY drive (or Pico MSC drive) using P/Invoke to kernel32.
/// Primary: volume name "CIRCUITPY". Fallback 1: boot_out.txt exists.
/// Fallback 2: volume name contains "PICO" or "BRAKE".
/// </summary>
public static class PicoDriveFinder
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    /// <summary>
    /// Scan all drive letters A-Z and return the root path of the Pico drive, or null if not found.
    /// </summary>
    public static string? FindDrive()
    {
        // Pass 1: exact match on "CIRCUITPY" volume name
        foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            string drive = $"{letter}:\\";
            string? volumeName = GetVolumeName(drive);
            if (volumeName == "CIRCUITPY")
                return drive;
        }

        // Pass 2: check for boot_out.txt (CircuitPython always creates this)
        foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            string drive = $"{letter}:\\";
            try
            {
                if (Directory.Exists(drive) && File.Exists(Path.Combine(drive, "boot_out.txt")))
                    return drive;
            }
            catch
            {
                // Drive not ready or access denied — skip
            }
        }

        // Pass 3: volume name contains "PICO" or "BRAKE" (C++ firmware MSC)
        foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            string drive = $"{letter}:\\";
            string? volumeName = GetVolumeName(drive);
            if (volumeName is not null &&
                (volumeName.Contains("PICO", StringComparison.OrdinalIgnoreCase) ||
                 volumeName.Contains("BRAKE", StringComparison.OrdinalIgnoreCase)))
                return drive;
        }

        return null;
    }

    private static string? GetVolumeName(string driveRoot)
    {
        try
        {
            var volumeName = new StringBuilder(256);
            if (GetVolumeInformationW(driveRoot, volumeName, 256,
                    out _, out _, out _, null!, 0))
            {
                return volumeName.ToString();
            }
        }
        catch
        {
            // Drive not ready — skip
        }
        return null;
    }
}
