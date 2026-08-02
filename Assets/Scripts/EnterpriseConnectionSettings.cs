using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Persistent connection settings for PICO Enterprise USB tethering.
    /// Device builds only connect through the native 192.168.245.x USB network.
    /// </summary>
    public static class EnterpriseConnectionSettings
    {
        public const string DefaultUsbHostIp = "192.168.245.223";

        private const string UsbHostIpKey = "XRoboToolkit.Enterprise.UsbHostIp";
        private const string LastSuccessfulHostIpKey = "XRoboToolkit.Enterprise.LastSuccessfulHostIp";
        private const string AutoEnableUsbTetheringKey = "XRoboToolkit.Enterprise.AutoEnableUsbTethering";

        public static string UsbHostIp
        {
            get { return ReadIpv4(UsbHostIpKey, DefaultUsbHostIp); }
            set { WriteIpv4(UsbHostIpKey, value); }
        }

        public static string LastSuccessfulHostIp
        {
            get { return ReadIpv4(LastSuccessfulHostIpKey, string.Empty); }
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
            AddCandidate(candidates, UsbHostIp);
            AddCandidate(candidates, LastSuccessfulHostIp);
            return candidates;
        }

        /// <summary>
        /// Persists a manually entered endpoint only when it belongs to the
        /// PICO Enterprise USB tethering subnet.
        /// </summary>
        public static void RememberManualHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized) || !IsUsbHost(normalized))
                return;

            UsbHostIp = normalized;
        }

        public static void RememberSuccessfulHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized) ||
                !IsConnectionAddressAllowed(normalized))
            {
                return;
            }

            PlayerPrefs.SetString(LastSuccessfulHostIpKey, normalized);
            PlayerPrefs.Save();
        }

        public static bool IsConnectionAddressAllowed(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return false;

#if UNITY_EDITOR
            // Keep localhost available only for Editor-side simulation. Non-loopback
            // network connections still have to use the native PICO USB subnet.
            return IsUsbHost(normalized) || IPAddress.IsLoopback(IPAddress.Parse(normalized));
#else
            // PICO Enterprise device builds are intentionally USB-only. Wi-Fi and
            // adb-reverse/loopback endpoints are rejected to avoid high-latency paths.
            return IsUsbHost(normalized);
#endif
        }

        public static bool IsUsbHost(string address)
        {
            return TryNormalizeIpv4(address, out string normalized) &&
                   IsUsbSubnetAddress(normalized);
        }

        public static string DescribeTransport(string address)
        {
#if UNITY_EDITOR
            if (TryNormalizeIpv4(address, out string normalized) &&
                IPAddress.IsLoopback(IPAddress.Parse(normalized)))
            {
                return "Editor";
            }
#endif
            return "USB";
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
            if (!TryNormalizeIpv4(value, out string normalized) || !IsUsbHost(normalized))
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
            return bytes.Length == 4 &&
                   bytes[0] == 192 &&
                   bytes[1] == 168 &&
                   bytes[2] == 245 &&
                   bytes[3] > 0 &&
                   bytes[3] < 255;
        }
    }
}
