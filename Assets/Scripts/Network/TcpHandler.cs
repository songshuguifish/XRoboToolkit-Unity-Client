using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LitJson;
using Network;
using UnityEngine;

namespace Robot
{
    public class TcpHandler : MonoBehaviour
    {
        public const string Tag = ">>Tcp ";
        public const int RECEIVE_TIME_OUT_DEFAULT = 25000;
        public const int BUFFER_LEN = 1024 * 63;
        public const int TCP_PORT = 63901;
        public const float CONNECT_TIMEOUT_SECONDS = 4.0f;

        public delegate void ReceiveFunctionMsg(string functionName, string value);

        public delegate void ReceiveMassage(NetPacket packet);

        public static event ReceiveMassage ReceiveEvent;
        public static event ReceiveFunctionMsg ReceiveFunctionEvent;

        public static bool SendTrackingData = false;
        private static readonly object _sendObject = new object();
        private readonly ConcurrentQueue<NetPacket> _receivePackages = new ConcurrentQueue<NetPacket>();
        private static readonly ConcurrentQueue<SendData> _sendDatas = new ConcurrentQueue<SendData>();

        private Socket _socket;
        private volatile SocketState _state = SocketState.NONE;

        private bool _connectInited = false;
        private static string _address = EnterpriseConnectionSettings.DefaultUsbHostIp;
        private int _port = TCP_PORT;
        private int _sendTimeout = 15000; // timeout
        private Thread _sendThread;
        private string _appVersion = "";
        private string _deviceSN = "";
        private JsonData _trackingJsonData = new JsonData();
        private JsonData _sendJson = new JsonData();
        private TrackingData _trackingData = new TrackingData();
        private ConcurrentQueue<string> _sendTrackingMsg = new ConcurrentQueue<string>();
        private float _lastHeardSend = 0;
        private int _connectAttemptId;
        private int _connectStartedAttemptId;
        private float _connectStartedAt;
        private volatile bool _destroying;

        private void Awake()
        {
            _appVersion = Application.version;
            _sendThread = new Thread(OnSendThread)
            {
                IsBackground = true,
                Name = "XRoboToolkit TCP sender",
            };
        }

        public SocketState State
        {
            get { return _state; }
            set { _state = value; }
        }

        public string ConnectErrorInfo { get; private set; }

        public static string GetTargetIP
        {
            get { return _address; }
        }

        public void Connect(string address)
        {
            if (_destroying)
                return;

            if (!EnterpriseConnectionSettings.TryNormalizeIpv4(address, out string normalized))
            {
                SetConnectError($"Invalid IPv4 address: {address}");
                return;
            }

            if (!EnterpriseConnectionSettings.IsConnectionAddressAllowed(normalized))
            {
                SetConnectError("Loopback/adb-reverse endpoints are disabled on PICO Enterprise builds");
                return;
            }

            LogWindow.Info($"Attempting to connect to {normalized}");
            _address = normalized;
            Connect();
        }

        private void Connect()
        {
            _port = TCP_PORT;
            ConnectErrorInfo = "";
            Debug.Log($"{Tag}connect to server: ip {_address} port: {_port}");
            if (!IPAddress.TryParse(_address, out IPAddress ipAddress))
            {
                SetConnectError($"Invalid IPv4 address: {_address}");
                return;
            }

            Socket socket = null;
            Socket previousSocket = null;
            ConnectAttempt attempt = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    Blocking = true,
                    SendTimeout = _sendTimeout,
                    NoDelay = true,
                    ReceiveTimeout = RECEIVE_TIME_OUT_DEFAULT,
                };

                lock (_sendObject)
                {
                    previousSocket = _socket;
                    _socket = socket;
                    _state = SocketState.CONNECTING;
                    _connectInited = false;
                    _connectStartedAt = Time.realtimeSinceStartup;
                    int attemptId = Interlocked.Increment(ref _connectAttemptId);
                    _connectStartedAttemptId = attemptId;
                    attempt = new ConnectAttempt(socket, attemptId);
                }

                CloseSocket(previousSocket);
                socket.BeginConnect(ipAddress, _port, ConnectCallback, attempt);
            }
            catch (Exception e)
            {
                if (attempt != null)
                    SetConnectError(e.Message, attempt);
                else
                    SetConnectError(e.Message);
                Debug.LogError(Tag + "Connection failed: " + e);
            }
        }

        private void ConnectCallback(IAsyncResult async)
        {
            ConnectAttempt attempt = (ConnectAttempt)async.AsyncState;
            try
            {
                attempt.Socket.EndConnect(async);
                if (!IsCurrentAttempt(attempt))
                {
                    CloseSocket(attempt.Socket);
                    return;
                }

                if (!attempt.Socket.Connected)
                {
                    SetConnectError("connect error", attempt);
                    return;
                }

                bool staleAttempt;
                lock (_sendObject)
                {
                    staleAttempt = !IsCurrentAttemptLocked(attempt);
                    if (!staleAttempt)
                        _state = SocketState.WORKING;
                }

                if (staleAttempt)
                {
                    CloseSocket(attempt.Socket);
                    return;
                }

                LogWindow.Info("TCP socket connection established successfully");

                if (_sendThread.ThreadState == ThreadState.Unstarted)
                    _sendThread.Start();

                ReceiveContext receiveContext = new ReceiveContext(attempt);
                attempt.Socket.BeginReceive(
                    receiveContext.Buffer.data,
                    receiveContext.Buffer.GetReadableCount(),
                    receiveContext.Buffer.GetRemainCapacity(),
                    SocketFlags.None,
                    OnDataReceived,
                    receiveContext);

                if (!IsCurrentAttempt(attempt))
                {
                    CloseSocket(attempt.Socket);
                    return;
                }

                if (!string.IsNullOrEmpty(_deviceSN))
                    ConnectInit();

                Debug.Log(Tag + "Socket Connected!");
            }
            catch (Exception e)
            {
                if (!IsCurrentAttempt(attempt))
                {
                    CloseSocket(attempt.Socket);
                    return;
                }

                SetConnectError(e.Message, attempt);
                Debug.LogError(Tag + "Connect error, Exception " + e);
            }
        }

        private void ConnectInit()
        {
            LogWindow.Info($"Initializing connection with device SN: {_deviceSN}");
            Debug.Log("ConnectInit deviceSN:" + _deviceSN);
            Send(NetCMD.PACKET_CCMD_CONNECT, _deviceSN + "|-1");
            Send(NetCMD.PACKET_CCMD_SEND_VERSION, _deviceSN + "|1.0|" + _appVersion);
        }

        public void SetDeviceSn(string sn)
        {
            _deviceSN = sn;
            if (_state == SocketState.WORKING)
            {
                ConnectInit();
            }
        }

        private void OnDataReceived(IAsyncResult ar)
        {
            ReceiveContext context = (ReceiveContext)ar.AsyncState;
            if (_state != SocketState.WORKING || !IsCurrentAttempt(context.Attempt))
            {
                CloseSocket(context.Attempt.Socket);
                return;
            }

            try
            {
                int bytesRead = context.Attempt.Socket.EndReceive(ar);
                if (!IsCurrentAttempt(context.Attempt))
                {
                    CloseSocket(context.Attempt.Socket);
                    return;
                }

                if (bytesRead > 0)
                {
                    context.Buffer.AddWriteIndex(bytesRead);
                    bool msgEnough;
                    do
                    {
                        msgEnough = PackageHandle.Unpack(context.Buffer, out var package);
                        if (msgEnough)
                        {
                            if (package.Cmd == NetCMD.PACKET_CMD_FROM_CONTROLLER_COMMON_FUNCTION)
                            {
                                Debug.Log("receive function:" + package.ToString());
                                if (package.ToString().Contains("timeTest"))
                                {
                                    Send(NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION, "timeTest");
                                }
                            }

                            _receivePackages.Enqueue(package);
                        }
                    } while (msgEnough);

                    context.Buffer.RemoveReadedBytes();

                    // Continue receiving data
                    context.Attempt.Socket.BeginReceive(
                        context.Buffer.data,
                        context.Buffer.GetReadableCount(),
                        context.Buffer.GetRemainCapacity(),
                        SocketFlags.None,
                        OnDataReceived,
                        context);
                }
                else
                {
                    LogWindow.Warn("TCP client disconnected - no data received");
                    Debug.Log("Client disconnected.");
                    Close();
                }
            }
            catch (Exception ex)
            {
                if (!IsCurrentAttempt(context.Attempt))
                {
                    CloseSocket(context.Attempt.Socket);
                    return;
                }

                LogWindow.Error($"TCP data receive error: {ex.Message}");
                Debug.LogError($"Error: {ex.Message}");
                Close();
            }
        }

        private static JsonData _functionJson = new JsonData();

        public static void SendFunctionValue(string function, string value)
        {
            _functionJson["functionName"] = function;
            _functionJson["value"] = value;

            Send(NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION, _functionJson.ToJson());
        }


        public static void Send(byte cmd, string msg)
        {
            _sendDatas.Enqueue(new SendData(cmd, Encoding.UTF8.GetBytes(msg)));
        }

        public static void SendCustomData(byte[] msg)
        {
            _sendDatas.Enqueue(new SendData(NetCMD.PACKET_CMD_CUSTOM_TO_PC, msg));
        }


        private void Update()
        {
            if (SendTrackingData)
            {
                if (_sendTrackingMsg.Count < 2)
                {
                    _trackingData.Get(ref _trackingJsonData);
                    _sendTrackingMsg.Enqueue(_trackingJsonData.ToJson());
                }
            }

            if (State == SocketState.WORKING)
            {
                //heartbeat
                if (_deviceSN != null)
                {
                    if (Time.time - _lastHeardSend > 10)
                    {
                        _lastHeardSend = Time.time;
                        Send(NetCMD.PACKET_CCMD_CLIENT_HEARTBEAT, _deviceSN);
                    }
                }
            }

            if (State == SocketState.CONNECTING &&
                Time.realtimeSinceStartup - _connectStartedAt > CONNECT_TIMEOUT_SECONDS)
            {
                SetConnectTimeoutError(_connectStartedAttemptId);
            }

            ReceivePacketHandle();
        }

        private void ReceivePacketHandle()
        {
            while (_receivePackages.TryDequeue(out NetPacket packet))
            {
                try
                {
                    if (packet.Cmd == NetCMD.PACKET_CMD_FROM_CONTROLLER_COMMON_FUNCTION)
                    {
                        string content = packet.ToString();
                        if (string.IsNullOrEmpty(content))
                        {
                            continue;
                        }

                        JsonData json = JsonMapper.ToObject(content);
                        if (!json.ContainsKey("functionName") || !json.ContainsKey("value"))
                        {
                            continue;
                        }

                        string functionName = json["functionName"].ToString();
                        Debug.Log("Receive functionName:" + functionName);
                        if (ReceiveFunctionEvent != null)
                        {
                            ReceiveFunctionEvent.Invoke(functionName, json["value"].ToString());
                        }
                    }
                    else
                    {
                        if (ReceiveEvent != null)
                        {
                            ReceiveEvent.Invoke(packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError("ReceivePacketHandle Exception:" + e);
                }
            }
        }


        public void Reconnect()
        {
            LogWindow.Info("Attempting to reconnect to TCP server");
            Connect(_address);
        }


        private void OnSendThread()
        {
            while (_state != SocketState.DESTROY)
            {
                if (_state != SocketState.WORKING)
                {
                    Thread.Sleep(100);
                    continue;
                }

                try
                {
                    lock (_sendObject)
                    {
                        SocketError socketError = SocketError.Success;
                        if (_socket != null && _socket.Connected)
                        {
                            //Sending general messages
                            while (_sendDatas.TryDequeue(out SendData sendData))
                            {
                                byte[] data = PackageHandle.Pack(sendData.Cmd, sendData.Content);

                                int totalBytes = data.Length;
                                int bytesSent = 0;
                                while (bytesSent < totalBytes)
                                {
                                    int remainingBytes = totalBytes - bytesSent;
                                    bytesSent += _socket.Send(data, bytesSent, remainingBytes,
                                        SocketFlags.None,
                                        out socketError);
                                    if (socketError == SocketError.Success)
                                    {
                                        if (sendData.Cmd == NetCMD.PACKET_CCMD_SEND_VERSION)
                                        {
                                            //This message has been successfully sent, indicating the establishment of communication with the PC side
                                            LogWindow.Info("PC connection established - version packet sent successfully");
                                            Debug.Log("pc connected !");
                                            _connectInited = true;
                                        }
                                    }
                                    else
                                    {
                                        break;
                                    }
                                }

                                if (socketError != SocketError.Success)
                                {
                                    LogWindow.Error($"TCP send error: {socketError}");
                                    Debug.LogError(Tag + "send SocketError:" + socketError);
                                    Close();
                                    break;
                                }
                            }

                            //Tracking data transmission
                            if (_connectInited && SendTrackingData)
                            {
                                if (_sendTrackingMsg.Count > 0)
                                {
                                    _sendTrackingMsg.TryDequeue(out var msg);
                                    //Display the frequency of tracking data occurrence
                                    FPSDisplay.UpdateTime();
                                    _sendJson["functionName"] = "Tracking";
                                    _sendJson["value"] = msg;

                                    byte[] data = PackageHandle.Pack(NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION,
                                        Encoding.UTF8.GetBytes(_sendJson.ToJson()));

                                    int res = _socket.Send(data, 0, data.Length, SocketFlags.None,
                                        out socketError);
                                    if (res < data.Length)
                                    {
                                        Debug.LogWarning(Tag + "Incomplete data occurrence!");
                                    }


                                    if (socketError != SocketError.Success)
                                    {
                                        LogWindow.Error($"TCP tracking data send error: {socketError}");
                                        Debug.LogError(Tag + "SocketError:" + socketError);
                                        Close();
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                Thread.Sleep(14);
                            }
                        }
                        else
                        {
                            Close();
                        }
                    }
                }
                catch (Exception e)
                {
                    LogWindow.Error($"TCP send thread error: {e.Message}");
                    Debug.LogError(Tag + "Error OnSendThread:" + e);
                    Close();
                }
            }
        }

        private void OnDestroy()
        {
            _destroying = true;
            CloseInternal(SocketState.DESTROY);
        }

        public void Close()
        {
            CloseInternal(_destroying ? SocketState.DESTROY : SocketState.CLOSE);
        }

        private void CloseInternal(SocketState finalState)
        {
            LogWindow.Info("Closing TCP connection");
            Debug.Log(Tag + "Close:");
            Socket socket;
            lock (_sendObject)
            {
                socket = DetachCurrentSocketLocked();
                _connectInited = false;
                _state = finalState;
            }

            CloseSocket(socket);
            ClearReceivePackages();
        }

        private void SetConnectError(string message, ConnectAttempt attempt = null)
        {
            Socket socket;
            lock (_sendObject)
            {
                if (attempt != null && !IsCurrentAttemptLocked(attempt))
                    return;

                ConnectErrorInfo = message;
                socket = DetachCurrentSocketLocked();
                _connectInited = false;
                _state = _destroying ? SocketState.DESTROY : SocketState.CONNECT_ERROR;
            }

            CloseSocket(socket);
            LogWindow.Error($"TCP connection failed: {message}");
            Debug.LogError(Tag + "Connection failed: " + message);
        }

        private void SetConnectTimeoutError(int attemptId)
        {
            Socket socket;
            string message = $"Connection timeout after {CONNECT_TIMEOUT_SECONDS:0.#} seconds";
            lock (_sendObject)
            {
                if (_state != SocketState.CONNECTING ||
                    attemptId != _connectAttemptId ||
                    attemptId != _connectStartedAttemptId)
                {
                    return;
                }

                ConnectErrorInfo = message;
                socket = DetachCurrentSocketLocked();
                _connectInited = false;
                _state = _destroying ? SocketState.DESTROY : SocketState.CONNECT_ERROR;
            }

            CloseSocket(socket);
            LogWindow.Error($"TCP connection failed: {message}");
            Debug.LogError(Tag + "Connection failed: " + message);
        }

        private bool IsCurrentAttempt(ConnectAttempt attempt)
        {
            lock (_sendObject)
            {
                return IsCurrentAttemptLocked(attempt);
            }
        }

        private bool IsCurrentAttemptLocked(ConnectAttempt attempt)
        {
            return attempt != null &&
                   attempt.Id == _connectAttemptId &&
                   ReferenceEquals(attempt.Socket, _socket);
        }

        private Socket DetachCurrentSocketLocked()
        {
            Socket socket = _socket;
            _socket = null;
            Interlocked.Increment(ref _connectAttemptId);
            return socket;
        }

        private static void CloseSocket(Socket socket)
        {
            if (socket == null)
                return;

            try
            {
                if (socket.Connected)
                    socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception)
            {
                // The socket may already be disconnected or a connect may still be pending.
            }

            try
            {
                socket.Close();
            }
            catch (Exception)
            {
                // Best-effort cleanup. The active attempt id prevents stale callbacks from winning.
            }
        }

        private void ClearReceivePackages()
        {
            while (_receivePackages.TryDequeue(out _))
            {
            }
        }

        private sealed class ConnectAttempt
        {
            public readonly Socket Socket;
            public readonly int Id;

            public ConnectAttempt(Socket socket, int id)
            {
                Socket = socket;
                Id = id;
            }
        }

        private sealed class ReceiveContext
        {
            public readonly ConnectAttempt Attempt;
            public readonly ByteBuffer Buffer = new ByteBuffer(BUFFER_LEN);

            public ReceiveContext(ConnectAttempt attempt)
            {
                Attempt = attempt;
            }
        }


        struct SendData
        {
            public byte Cmd;
            public byte[] Content;

            public SendData(byte cmd, byte[] content)
            {
                Cmd = cmd;
                Content = content;
            }
        }
    }

    public enum SocketState : int
    {
        NONE,
        CREATE,
        CONNECTING,
        WORKING,
        CLOSE,
        CONNECT_ERROR,
        DESTROY,
    }
}
