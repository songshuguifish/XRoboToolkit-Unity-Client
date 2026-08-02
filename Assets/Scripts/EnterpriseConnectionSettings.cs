using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Persistent connection settings for PICO Enterprise deployments.
    /// USB Ethernet is preferred; the configured Wi-Fi address is used as fallback.
    /// </summary>
    public static class EnterpriseConnectionSettings
    {
        public const string DefaultUsbHostIp = "192.168.245.223";
        public const string DefaultWifiHostIp = "10.22.111.121";

        private const string UsbHostIpKey = "XRoboToolkit.Enterprise.UsbHostIp";
        private const string WifiHostIpKey = "XRoboToolkit.Enterprise.WifiHostIp";
        private const string LastSuccessfulHostIpKey = "XRoboToolkit.Enterprise.LastSuccessfulHostIp";
        private const string PreferUsbKey = "XRoboToolkit.Enterprise.PreferUsb";
        private const string AutoEnableUsbTetheringKey = "XRoboToolkit.Enterprise.AutoEnableUsbTethering";

        public static string UsbHostIp
        {
            get { return ReadIpv4(UsbHostIpKey, DefaultUsbHostIp); }
            set { WriteIpv4(UsbHostIpKey, value); }
        }

        public static string WifiHostIp
        {
            get { return ReadIpv4(WifiHostIpKey, DefaultWifiHostIp); }
            set { WriteIpv4(WifiHostIpKey, value); }
        }

        public static string LastSuccessfulHostIp
        {
            get { return ReadIpv4(LastSuccessfulHostIpKey, string.Empty); }
        }

        public static bool PreferUsb
        {
            get { return PlayerPrefs.GetInt(PreferUsbKey, 1) == 1; }
            set
            {
                PlayerPrefs.SetInt(PreferUsbKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        public static bool AutoEnableUsbTethering
        {
            get { return PlayerPrefs.GetInt(AutoEnableUsbTetheringKey, 1) == 1; }
            set
            {
                PlayerPrefs.SetInt(AutoEnableUsbTetheringKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        public static List<string> GetAutoConnectCandidates()
        {
            List<string> candidates = new List<string>();
            if (PreferUsb)
            {
                AddCandidate(candidates, UsbHostIp);
                AddCandidate(candidates, WifiHostIp);
            }
            else
            {
                AddCandidate(candidates, WifiHostIp);
                AddCandidate(candidates, UsbHostIp);
            }

            AddCandidate(candidates, LastSuccessfulHostIp);
            return candidates;
        }

        /// <summary>
        /// Persists a manually entered endpoint. Addresses on the PICO USB tethering
        /// subnet update the USB endpoint; all other addresses update Wi-Fi fallback.
        /// </summary>
        public static void RememberManualHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return;

            if (IsUsbSubnetAddress(normalized))
                UsbHostIp = normalized;
            else
                WifiHostIp = normalized;
        }

        public static void RememberSuccessfulHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return;

            PlayerPrefs.SetString(LastSuccessfulHostIpKey, normalized);
            PlayerPrefs.Save();
        }

        public static bool IsConnectionAddressAllowed(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return false;

#if UNITY_EDITOR
            // Keep localhost available for Editor-only PC simulation.
            return true;
#else
            // Enterprise device builds use native USB/Wi-Fi networking. Rejecting
            // loopback prevents silently falling back to the legacy adb reverse path.
            return !IPAddress.IsLoopback(IPAddress.Parse(normalized));
#endif
        }

        public static bool IsUsbHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return false;
            return string.Equals(normalized, UsbHostIp, StringComparison.Ordinal) ||
                   IsUsbSubnetAddress(normalized);
        }

        public static string DescribeTransport(string address)
        {
            return IsUsbHost(address) ? "USB" : "Wi-Fi";
        }

        public static bool TryNormalizeIpv4(string address, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(address))
                return false;
            if (!IPAddress.TryParse(address.Trim(), out IPAddress parsed))
                return false;
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                return false;

            normalized = parsed.ToString();
            return true;
        }

        private static string ReadIpv4(string key, string fallback)
        {
            string value = PlayerPrefs.GetString(key, fallback);
            return TryNormalizeIpv4(value, out string normalized) ? normalized : fallback;
        }

        private static void WriteIpv4(string key, string value)
        {
            if (!TryNormalizeIpv4(value, out string normalized))
                return;

            PlayerPrefs.SetString(key, normalized);
            PlayerPrefs.Save();
        }

        private static void AddCandidate(List<string> candidates, string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return;
            if (!IsConnectionAddressAllowed(normalized))
                return;
            if (!candidates.Contains(normalized))
                candidates.Add(normalized);
        }

        private static bool IsUsbSubnetAddress(string address)
        {
            if (!IPAddress.TryParse(address, out IPAddress parsed))
                return false;
            byte[] bytes = parsed.GetAddressBytes();
            return bytes.Length == 4 && bytes[0] == 192 && bytes[1] == 168 && bytes[2] == 245;
        }
    }
}
