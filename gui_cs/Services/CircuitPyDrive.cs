using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace BrakeCalibrator.Services
{
    /// <summary>
    /// Cross-platform CIRCUITPY/PIcoBrake drive detection.
    /// Windows: P/Invoke GetVolumeInformationW with two-pass logic matching Python.
    /// </summary>
    public static class CircuitPyDrive
    {
        private static readonly string[] PreferredNames = { "PIcoBrake", "CIRCUITPY" };

        public static string? FindDrive()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return FindDriveWindows();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return FindDriveLinux();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return FindDriveMac();
            return null;
        }

        #region Windows

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetVolumeInformationW(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetDriveType(string rootPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetLogicalDriveStrings(int nBufferLength, char[] lpBuffer);

        private static string? FindDriveWindows()
        {
            // Pass 1: Check volume name of every drive letter
            foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            {
                string drive = $"{letter}:\\";
                if (GetDriveType(drive) != 2) continue; // DRIVE_REMOVABLE=2, but we also want DRIVE_FIXED=3 etc.

                var volumeName = new StringBuilder(256);
                if (GetVolumeInformationW(drive, volumeName, 256, out _, out _, out _, null, 0))
                {
                    foreach (var name in PreferredNames)
                    {
                        if (volumeName.ToString() == name)
                            return drive;
                    }
                }
            }

            // Pass 2: Fallback — check for boot_out.txt or calibration.json
            foreach (char letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
            {
                string drive = $"{letter}:\\";
                if (!System.IO.Directory.Exists(drive)) continue;

                if (System.IO.File.Exists(System.IO.Path.Combine(drive, "boot_out.txt")))
                    return drive;

                if (System.IO.File.Exists(System.IO.Path.Combine(drive, "calibration.json")))
                {
                    var volumeName = new StringBuilder(256);
                    if (GetVolumeInformationW(drive, volumeName, 256, out _, out _, out _, null, 0))
                    {
                        string upper = volumeName.ToString().ToUpperInvariant();
                        if (upper.Contains("PICO") || upper.Contains("BRAKE"))
                            return drive;
                    }
                }
            }

            return null;
        }

        #endregion

        #region Linux

        private static string? FindDriveLinux()
        {
            foreach (var name in PreferredNames)
            {
                // Check /media/<user>/CIRCUITPY
                var mediaDir = System.IO.Path.Combine("/media", Environment.UserName, name);
                if (System.IO.Directory.Exists(mediaDir)) return mediaDir;

                // Also check any user under /media
                if (System.IO.Directory.Exists("/media"))
                {
                    foreach (var userDir in System.IO.Directory.GetDirectories("/media"))
                    {
                        var path = System.IO.Path.Combine(userDir, name);
                        if (System.IO.Directory.Exists(path)) return path;
                    }
                }

                // /run/media/<user>/CIRCUITPY
                if (System.IO.Directory.Exists("/run/media"))
                {
                    foreach (var userDir in System.IO.Directory.GetDirectories("/run/media"))
                    {
                        var path = System.IO.Path.Combine(userDir, name);
                        if (System.IO.Directory.Exists(path)) return path;
                    }
                }
            }
            return null;
        }

        #endregion

        #region macOS

        private static string? FindDriveMac()
        {
            foreach (var name in PreferredNames)
            {
                var path = $"/Volumes/{name}";
                if (System.IO.Directory.Exists(path)) return path;
            }
            return null;
        }

        #endregion
    }
}
