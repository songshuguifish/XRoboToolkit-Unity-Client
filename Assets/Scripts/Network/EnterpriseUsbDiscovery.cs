using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LitJson;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Unity-side client for the UMI PICO USB discovery protocol, served on the PC by
    /// <c>umi_pico_collect/scripts/remote/pico_usb_discovery.py</c>.
    ///
    /// The headset broadcasts a nonce-bearing request over the USB RNDIS/ECM link; the
    /// PC replies from the receiving interface with that interface's own IPv4 address
    /// and the XRoboToolkit TCP port. Neither side hardcodes a subnet, so the link
    /// works on whatever /24 Android's tethering stack assigns (192.168.37.x,
    /// 192.168.245.x, ...).
    /// </summary>
    public static class EnterpriseUsbDiscovery
    {
        public const int DiscoveryPort = 43451;

        private const string LogTag = ">>UsbDiscovery ";
        private const int ProtocolVersion = 1;
        private const string RequestType = "umi_pico_discovery";
        private const string ReplyType = "umi_pico_discovery_reply";

        // The PC compares the requested transport against what udev reports for the
        // receiving interface and silently drops mismatches, so probe both and let the
        // matching one answer.
        private static readonly string[] Transports = { "pico_usb_rndis", "pico_usb_ecm" };

        private const int ReceiveTimeoutMs = 300;
        private const int SweepDeadlineMs = 1500;
        private const int RefreshIntervalWithHostMs = 3000;
        private const int RefreshIntervalWithoutHostMs = 500;

        private static readonly object _stateLock = new object();
        private static readonly HashSet<string> _verifiedHosts = new HashSet<string>();
        private static readonly System.Diagnostics.Stopwatch _sinceLastSweep =
            System.Diagnostics.Stopwatch.StartNew();

        private static Thread _worker;
        private static volatile string _host;
        private static volatile int _tcpPort;
        private static volatile string _hostLocalIp;
        private static string _lastFailureLogged;

        /// <summary>
        /// Most recently discovered PC endpoint, or <c>null</c> when unknown or when the
        /// USB link has changed since it was discovered (a new cable or a new Android
        /// tethering session gets a different subnet, which makes the old peer moot).
        /// </summary>
        public static string Host
        {
            get
            {
                string host = _host;
                if (host == null)
                    return null;

                string discoveredOn = _hostLocalIp;
                if (!string.IsNullOrEmpty(discoveredOn) &&
                    TryResolveUsbInterface(out IPAddress local, out _, out _) &&
                    local.ToString() != discoveredOn)
                {
                    return null;
                }

                return host;
            }
        }

        /// <summary>
        /// Identifies the current USB link by the interface's own IPv4, or empty when the
        /// link is down. Callers watch this to notice a cable or subnet change.
        /// </summary>
        public static string UsbSubnetToken
        {
            get
            {
                return TryResolveUsbInterface(out IPAddress local, out _, out _)
                    ? local.ToString()
                    : string.Empty;
            }
        }

        /// <summary>TCP port advertised alongside <see cref="Host"/>; 0 when unknown.</summary>
        public static int TcpPort
        {
            get { return _tcpPort; }
        }

        /// <summary>
        /// Starts a background discovery sweep unless one is already running or the
        /// previous sweep finished too recently. Never blocks the caller.
        /// </summary>
        public static void RequestRefresh()
        {
            lock (_stateLock)
            {
                if (_worker != null && _worker.IsAlive)
                    return;

                int interval = _host != null ? RefreshIntervalWithHostMs : RefreshIntervalWithoutHostMs;
                if (_sinceLastSweep.ElapsedMilliseconds < interval)
                    return;

                _worker = new Thread(RunSweep)
                {
                    IsBackground = true,
                    Name = "XRoboToolkit USB discovery",
                };
                _worker.Start();
            }
        }

        /// <summary>
        /// True when the address came back from a nonce-matched discovery reply, which
        /// proves the PC answered over a udev-verified PICO USB interface.
        /// </summary>
        public static bool IsVerifiedHost(string address)
        {
            if (string.IsNullOrEmpty(address))
                return false;

            lock (_stateLock)
            {
                if (!_verifiedHosts.Contains(address))
                    return false;
            }

            // A host verified over an earlier USB link must not stay trusted once the
            // link moves to a different subnet, otherwise a stale endpoint keeps being
            // offered as a candidate forever.
            if (TryResolveUsbInterface(out IPAddress local, out _, out _))
                return IPAddress.TryParse(address, out IPAddress target) && SharesSlash24(local, target);

            return true;
        }

        /// <summary>
        /// Resolves the device's USB tethering interface (usb0 / rndis0 / ECM) and its
        /// IPv4 address and broadcast address. Returns false when tethering is down or
        /// the platform does not enumerate the interface.
        /// </summary>
        public static bool TryResolveUsbInterface(
            out IPAddress local, out IPAddress broadcast, out string interfaceName)
        {
            local = null;
            broadcast = null;
            interfaceName = null;

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string name = ni.Name.ToLowerInvariant();
                    if (!name.Contains("usb") && !name.Contains("rndis") && !name.Contains("ecm"))
                        continue;

                    foreach (UnicastIPAddressInformation uni in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        if (IPAddress.IsLoopback(uni.Address))
                            continue;

                        local = uni.Address;
                        broadcast = ComputeBroadcast(uni);
                        interfaceName = ni.Name;
                        return true;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag}interface enumeration failed: {e.GetType().Name}: {e.Message}");
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="address"/> shares the /24 of the device's USB
        /// interface. Used to reject replies that arrive over Wi-Fi.
        /// </summary>
        public static bool IsOnUsbSubnet(string address)
        {
            if (!IPAddress.TryParse(address, out IPAddress target))
                return false;
            if (!TryResolveUsbInterface(out IPAddress local, out _, out _))
                return false;

            return SharesSlash24(local, target);
        }

        private static bool SharesSlash24(IPAddress a, IPAddress b)
        {
            byte[] left = a.GetAddressBytes();
            byte[] right = b.GetAddressBytes();
            return left.Length == 4 && right.Length == 4 &&
                   left[0] == right[0] && left[1] == right[1] && left[2] == right[2];
        }

        private static IPAddress ComputeBroadcast(UnicastIPAddressInformation uni)
        {
            byte[] address = uni.Address.GetAddressBytes();
            byte[] mask = null;
            try
            {
                if (uni.IPv4Mask != null)
                    mask = uni.IPv4Mask.GetAddressBytes();
            }
            catch
            {
                // Mono on Android does not always expose IPv4Mask.
            }

            bool maskUsable = mask != null && mask.Length == 4 &&
                              (mask[0] != 0 || mask[1] != 0 || mask[2] != 0 || mask[3] != 0);
            if (!maskUsable)
                mask = new byte[] { 255, 255, 255, 0 };

            byte[] result = new byte[4];
            for (int i = 0; i < 4; i++)
                result[i] = (byte)(address[i] | (byte)~mask[i]);
            return new IPAddress(result);
        }

        private static void RunSweep()
        {
            try
            {
                Sweep();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag}sweep aborted: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                lock (_stateLock)
                    _sinceLastSweep.Restart();
            }
        }

        private static void Sweep()
        {
            bool haveInterface = TryResolveUsbInterface(
                out IPAddress local, out IPAddress broadcast, out string interfaceName);

            // Preferred path: bind to the USB interface so the probe never leaves the
            // USB link. Fallback: bind to any address and use the all-hosts broadcast.
            // The PC daemon only answers on a udev-verified PICO USB interface, so a
            // reply still proves the endpoint is reachable over USB.
            IPAddress bindAddress = haveInterface ? local : IPAddress.Any;
            IPAddress target = haveInterface ? broadcast : IPAddress.Broadcast;
            string nonce = Guid.NewGuid().ToString("N");

            UdpClient client = null;
            try
            {
                client = new UdpClient(new IPEndPoint(bindAddress, 0));
                client.EnableBroadcast = true;
                client.Client.ReceiveTimeout = ReceiveTimeoutMs;

                System.Diagnostics.Stopwatch deadline = System.Diagnostics.Stopwatch.StartNew();
                bool probeSent = false;

                while (deadline.ElapsedMilliseconds < SweepDeadlineMs)
                {
                    if (!probeSent)
                    {
                        int sent = 0;
                        foreach (string transport in Transports)
                        {
                            byte[] request = BuildRequest(nonce, transport);
                            try
                            {
                                client.Send(request, request.Length, new IPEndPoint(target, DiscoveryPort));
                                sent++;
                            }
                            catch (SocketException e)
                            {
                                // One unroutable probe must not abort the whole sweep.
                                LogFailure($"{LogTag}probe send failed for {transport} " +
                                           $"via {target}: {e.SocketErrorCode}");
                            }
                        }

                        if (sent == 0)
                        {
                            LogFailure($"{LogTag}no probe could be sent to {target}:{DiscoveryPort}");
                            return;
                        }

                        probeSent = true;
                    }

                    IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                    byte[] payload;
                    try
                    {
                        payload = client.Receive(ref remote);
                    }
                    catch (SocketException)
                    {
                        // Receive timeout: re-probe in case tethering came up mid-sweep.
                        probeSent = false;
                        continue;
                    }

                    if (!TryParseReply(payload, nonce, out string host, out int tcpPort))
                        continue;

                    // Reject a reply that did not arrive over the USB link.
                    if (haveInterface && !SharesSlash24(local, IPAddress.Parse(host)))
                    {
                        Debug.LogWarning(
                            $"{LogTag}ignored reply {host} outside USB subnet of {local} ({interfaceName})");
                        continue;
                    }

                    Accept(host, tcpPort, haveInterface ? interfaceName : "unenumerated",
                        haveInterface ? local.ToString() : string.Empty);
                    return;
                }

                LogFailure(haveInterface
                    ? $"{LogTag}no reply on {interfaceName} ({local} -> {target}:{DiscoveryPort})"
                    : $"{LogTag}no reply and no USB interface enumerated (broadcast {target}:{DiscoveryPort})");
            }
            finally
            {
                if (client != null)
                    client.Close();
            }
        }

        private static void Accept(string host, int tcpPort, string interfaceName, string localIp)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = !string.Equals(_host, host) || _tcpPort != tcpPort;
                _verifiedHosts.Add(host);
                _host = host;
                _tcpPort = tcpPort;
                _hostLocalIp = localIp;
                _lastFailureLogged = null;
            }

            if (!changed)
                return;

            string message = $"{LogTag}discovered PC {host}:{tcpPort} via {interfaceName}";
            Debug.Log(message);
            LogWindow.Info(message);
        }

        private static void LogFailure(string message)
        {
            lock (_stateLock)
            {
                if (string.Equals(_lastFailureLogged, message))
                    return;
                _lastFailureLogged = message;
            }

            Debug.LogWarning(message);
            LogWindow.Warn(message);
        }

        private static byte[] BuildRequest(string nonce, string transport)
        {
            // nonce is hex from Guid, transport is a literal, so no escaping is needed.
            string json = "{\"type\":\"" + RequestType + "\"" +
                          ",\"version\":" + ProtocolVersion +
                          ",\"nonce\":\"" + nonce + "\"" +
                          ",\"transport\":\"" + transport + "\"}";
            return Encoding.UTF8.GetBytes(json);
        }

        private static bool TryParseReply(byte[] payload, string nonce, out string host, out int tcpPort)
        {
            host = null;
            tcpPort = 0;
            if (payload == null || payload.Length == 0)
                return false;

            JsonData reply;
            try
            {
                reply = JsonMapper.ToObject(Encoding.UTF8.GetString(payload));
            }
            catch (Exception)
            {
                return false;
            }

            if (reply == null || !reply.IsObject)
                return false;
            if (ReadString(reply, "type") != ReplyType)
                return false;
            if (ReadInt(reply, "version") != ProtocolVersion)
                return false;
            if (ReadString(reply, "nonce") != nonce)
                return false;

            string replyHost = ReadString(reply, "host");
            if (!EnterpriseConnectionSettings.TryNormalizeIpv4(replyHost, out string normalized))
                return false;

            int replyPort = ReadInt(reply, "tcp_port");
            if (replyPort <= 0 || replyPort > 65535)
                return false;

            host = normalized;
            tcpPort = replyPort;
            return true;
        }

        private static string ReadString(JsonData data, string key)
        {
            if (!data.Keys.Contains(key))
                return null;
            JsonData value = data[key];
            return value != null && value.IsString ? (string)value : null;
        }

        private static int ReadInt(JsonData data, string key)
        {
            if (!data.Keys.Contains(key))
                return -1;
            JsonData value = data[key];
            return value != null && value.IsInt ? (int)value : -1;
        }
    }
}
