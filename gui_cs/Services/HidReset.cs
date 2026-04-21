using System;
using System.Collections.Generic;
using System.Linq;
using HidLibrary;

namespace BrakeCalibrator.Services
{
    /// <summary>
    /// Sends HID reset command (0xDE 0xAD) to reboot the Pico.
    /// Matches Python hidapi logic: find by Usage Page 0x01 + Usage 0x05.
    /// </summary>
    public static class HidReset
    {
        /// <summary>
        /// Attempt to send reset command to Pico via HID Output Report.
        /// Returns true if successfully sent to at least one device.
        /// </summary>
        public static bool ResetPico()
        {
            try
            {
                var devices = HidDevices.Enumerate()
                    .Where(d => d.Capabilities.UsagePage == 0x01 && d.Capabilities.Usage == 0x05)
                    .ToList();

                foreach (var device in devices)
                {
                    try
                    {
                        if (!device.IsOpen)
                            device.OpenDevice();
                        if (device.IsOpen)
                        {
                            // 9 bytes: Report ID (0x00) + 0xDE 0xAD + 6 padding zeros
                            byte[] report = new byte[] { 0x00, 0xDE, 0xAD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
                            device.Write(report);
                            device.CloseDevice();
                            return true;
                        }
                    }
                    catch
                    {
                        try { device.CloseDevice(); } catch { }
                        continue;
                    }
                }
            }
            catch { }

            return false;
        }
    }
}
