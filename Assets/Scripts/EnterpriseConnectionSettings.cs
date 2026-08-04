using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Persistent connection settings for PICO Enterprise USB tethering.
    ///
    /// No USB subnet or host address is hardcoded. The endpoint comes from
    /// <see cref="EnterpriseUsbDiscovery"/>, and an address is only accepted when it is
    /// discovery-verified or on the /24 that Android actually assigned to the USB
    /// interface. Android hands out different subnets per device and per session
    /// (192.168.37.x, 192.168.245.x, ...), so any fixed guess eventually breaks.
    /// </summary>
    public static class EnterpriseConnectionSettings
    {
        private const string UsbHostIpKey = "XRoboToolkit.Enterprise.UsbHostIp";
        private const string LastSuccessfulHostIpKey = "XRoboToolkit.Enterprise.LastSuccessfulHostIp";
        private const string AutoEnableUsbTetheringKey = "XRoboToolkit.Enterprise.AutoEnableUsbTethering";

        public static string UsbHostIp
        {
            get { return ReadIpv4(UsbHostIpKey, string.Empty); }
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

        /// <summary>
        /// Ordered auto-connect candidates: the freshly discovered PC endpoint first,
        /// then addresses remembered from earlier sessions (which are still validated
        /// against the current USB subnet, so a stale one is dropped automatically).
        /// </summary>
        public static List<string> GetAutoConnectCandidates()
        {
            // Non-blocking; results land in EnterpriseUsbDiscovery.Host for this or a
            // later reload cycle.
            EnterpriseUsbDiscovery.RequestRefresh();

            List<string> candidates = new List<string>();
            AddCandidate(candidates, EnterpriseUsbDiscovery.Host);
            AddCandidate(candidates, UsbHostIp);
            AddCandidate(candidates, LastSuccessfulHostIp);
            return candidates;
        }

        /// <summary>
        /// Persists a manually entered endpoint only when it belongs to the live PICO
        /// Enterprise USB tethering subnet.
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

        /// <summary>
        /// An address is a valid USB host when a nonce-matched discovery reply came from
        /// it (which proves the PC answered on a udev-verified PICO USB interface), or
        /// when it shares the /24 of the device's live USB interface.
        /// </summary>
        public static bool IsUsbHost(string address)
        {
            if (!TryNormalizeIpv4(address, out string normalized))
                return false;

            return EnterpriseUsbDiscovery.IsVerifiedHost(normalized) ||
                   EnterpriseUsbDiscovery.IsOnUsbSubnet(normalized);
        }

        /// <summary>
        /// Best known host address for prefilling the manual-entry UI: the discovered
        /// endpoint if there is one, otherwise whatever a previous session remembered.
        /// </summary>
        public static string PreferredHostIp
        {
            get
            {
                if (TryNormalizeIpv4(EnterpriseUsbDiscovery.Host, out string discovered))
                    return discovered;
                if (TryNormalizeIpv4(UsbHostIp, out string remembered))
                    return remembered;
                return LastSuccessfulHostIp;
            }
        }

        /// <summary>
        /// Operator-facing description of the endpoints currently accepted, so the UI
        /// never names a subnet that is not the one actually in use.
        /// </summary>
        public static string DescribeAllowedEndpoints()
        {
            if (EnterpriseUsbDiscovery.TryResolveUsbInterface(out IPAddress local, out _, out string name))
            {
                byte[] bytes = local.GetAddressBytes();
                return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.x on {name}";
            }

            return "none yet - USB tethering is not up";
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
    }
}
