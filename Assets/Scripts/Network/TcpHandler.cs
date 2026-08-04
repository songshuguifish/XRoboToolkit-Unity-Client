using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LitJson;
using Network;
using Unity.XR.PXR;
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
        public static bool EnterpriseDirectTrackingEnabled = true;
        private static readonly object _sendObject = new object();
        private readonly ConcurrentQueue<NetPacket> _receivePackages = new ConcurrentQueue<NetPacket>();
        private static readonly ConcurrentQueue<SendData> _sendDatas = new ConcurrentQueue<SendData>();

        private Socket _socket;
        private volatile SocketState _state = SocketState.NONE;

        private bool _connectInited = false;
        private static string _address = string.Empty;
        private int _port = TCP_PORT;
        private int _sendTimeout = 15000; // timeout
        [SerializeField] private int trackingThreadIdleSleepMs = 5;
        [SerializeField] private int trackingThreadWaitForHeadSleepMs = 1;
        [SerializeField] private int trackingThreadQueueBackpressureSleepMs = 1;
        [SerializeField] private bool outputTrackingRateToLogWindow = false;
        [SerializeField] private float trackingRateLogIntervalSeconds = 1f;
        [SerializeField] private bool outputEnterpriseControllerPayloadToLog = true;
        [SerializeField] private float enterpriseControllerPayloadLogIntervalSeconds = 1f;
        private Thread _sendThread;
        private Thread _trackingThread;
        private string _appVersion = "";
        private string _deviceSN = "";
        private JsonData _trackingJsonData = new JsonData();
        private JsonData _sendJson = new JsonData();
        private TrackingData _trackingData = new TrackingData();
        private ConcurrentQueue<string> _sendTrackingMsg = new ConcurrentQueue<string>();
        private int _pendingDirectTrackingPackets = 0;
        private bool _cachedAppFocus = true;
        private int _cachedActiveInputDevice = (int)ActiveInputDevice.ControllerActive;
        private long _lastDirectTrackingHeadSampleSeq;
        private long _lastDirectTrackingControllerSampleSeq;
        private readonly System.Diagnostics.Stopwatch _trackingRateStopwatch = new System.Diagnostics.Stopwatch();
        private int _trackingPacketsSentInWindow = 0;
        private string _lastTrackingSendMode = "idle";
        private string _lastDirectTrackingGateStatus;
        private long _lastEnterpriseControllerPayloadLogTicks;
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
            _trackingThread = new Thread(OnTrackingThread)
            {
                IsBackground = true,
                Name = "XRoboToolkit enterprise tracking sender",
            };
            _trackingRateStopwatch.Start();
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
                SetConnectError(
                    $"{normalized} is not on the PICO Enterprise USB link; device builds only " +
                    "accept discovered or same-subnet USB endpoints");
                return;
            }

            LogWindow.Info($"Attempting to connect to {normalized}");
            _address = normalized;
            Connect();
        }

        private void Connect()
        {
            // Honour the port advertised by discovery; TCP_PORT is only the fallback for
            // remembered endpoints from a session that predates a discovery reply.
            _port = string.Equals(_address, EnterpriseUsbDiscovery.Host) && EnterpriseUsbDiscovery.TcpPort > 0
                ? EnterpriseUsbDiscovery.TcpPort
                : TCP_PORT;
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

        private static void StartIfUnstarted(Thread thread)
        {
            if (thread == null)
                return;
            if ((thread.ThreadState & ThreadState.Unstarted) == 0)
                return;

            thread.Start();
            Debug.Log($"{Tag}started thread {thread.Name}");
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

                // ThreadState is a flags enum and both threads are created with
                // IsBackground = true, so an unstarted one reports
                // Unstarted | Background (12), never Unstarted (8) on its own. Comparing
                // with == therefore never matched and neither thread was ever started,
                // which silently disabled the whole direct tracking send path.
                StartIfUnstarted(_sendThread);
                StartIfUnstarted(_trackingThread);

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
            _cachedAppFocus = Application.isFocused;
            _cachedActiveInputDevice = (int)PXR_HandTracking.GetActiveInputDevice();

            if (SendTrackingData && !ShouldSendEnterpriseTrackingDirectly())
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


        private void OnTrackingThread()
        {
            while (_state != SocketState.DESTROY)
            {
                if (!IsEnterpriseDirectTrackingReady())
                {
                    LogEnterpriseDirectTrackingGateIfChanged();
                    ResetDirectTrackingCache();
                    Thread.Sleep(GetClampedSleepMs(trackingThreadIdleSleepMs));
                    continue;
                }

                if (_pendingDirectTrackingPackets >= 2)
                {
                    Thread.Sleep(GetClampedSleepMs(trackingThreadQueueBackpressureSleepMs));
                    continue;
                }

                JsonData trackingValue = new JsonData();
                bool hasNewTrackingSample = false;
                long headSampleSeq = _lastDirectTrackingHeadSampleSeq;
                long controllerSampleSeq = _lastDirectTrackingControllerSampleSeq;

                LogEnterpriseDirectTrackingGateIfChanged();
                if (TrackingData.HeadOn)
                {
                    if (!EnterpriseCollectionRecorder.TryGetLatestEnterpriseHeadForTcp(
                            out string pose,
                            out int status,
                            out headSampleSeq))
                    {
                        Thread.Sleep(GetClampedSleepMs(trackingThreadWaitForHeadSleepMs));
                        continue;
                    }

                    JsonData head = new JsonData();
                    head["pose"] = pose;
                    head["status"] = status;
                    trackingValue["Head"] = head;
                    hasNewTrackingSample |= headSampleSeq != _lastDirectTrackingHeadSampleSeq;
                }

                if (TrackingData.ControllerOn)
                {
                    EnterpriseCollectionRecorder.TryGetLatestEnterpriseControllerForTcp(
                        out EnterpriseCollectionRecorder.EnterpriseControllerTcpPose leftController,
                        out EnterpriseCollectionRecorder.EnterpriseControllerTcpPose rightController,
                        out controllerSampleSeq);
                    if (!IsControllerActiveInput())
                    {
                        leftController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                        rightController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                    }

                    JsonData controller = BuildEnterpriseControllerJson(leftController, rightController);
                    trackingValue["Controller"] = controller;
                    LogEnterpriseControllerPayloadIfNeeded(controller, controllerSampleSeq);
                    hasNewTrackingSample |= controllerSampleSeq != _lastDirectTrackingControllerSampleSeq;
                }

                if (!hasNewTrackingSample)
                {
                    Thread.Yield();
                    continue;
                }

                JsonData appState = new JsonData();
                trackingValue["timeStampNs"] = Utils.GetCurrentTimestamp();
                appState["focus"] = _cachedAppFocus;
                trackingValue["appState"] = appState;
                trackingValue["Input"] = _cachedActiveInputDevice;

                JsonData sendJson = new JsonData();
                sendJson["functionName"] = "Tracking";
                sendJson["value"] = trackingValue.ToJson();

                _sendDatas.Enqueue(new SendData(
                    NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION,
                    Encoding.UTF8.GetBytes(sendJson.ToJson()),
                    true));
                Interlocked.Increment(ref _pendingDirectTrackingPackets);
                _lastDirectTrackingHeadSampleSeq = headSampleSeq;
                _lastDirectTrackingControllerSampleSeq = controllerSampleSeq;
            }
        }

        private bool ShouldSendEnterpriseTrackingDirectly()
        {
            return SendTrackingData &&
                   EnterpriseDirectTrackingEnabled &&
                   TrackingDataSourceCtrl.UseEnterpriseSDK &&
                   (TrackingData.HeadOn || TrackingData.ControllerOn);
        }

        private static JsonData BuildEnterpriseControllerJson(
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose left,
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose right)
        {
            EnterpriseCollectionRecorder.GetLatestInputForTcp(
                out EnterpriseCollectionRecorder.ControllerInputState leftInput,
                out EnterpriseCollectionRecorder.ControllerInputState rightInput);

            JsonData controller = new JsonData();
            controller["left"] = BuildEnterpriseControllerSideJson(left, leftInput);
            controller["right"] = BuildEnterpriseControllerSideJson(right, rightInput);
            return controller;
        }

        private static JsonData BuildEnterpriseControllerSideJson(
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose pose,
            EnterpriseCollectionRecorder.ControllerInputState input)
        {
            JsonData json = new JsonData();
            json["axisX"] = (double)input.AxisX;
            json["axisY"] = (double)input.AxisY;
            json["axisClick"] = input.AxisClick;
            json["grip"] = (double)input.Grip;
            json["trigger"] = (double)input.Trigger;
            json["primaryButton"] = input.PrimaryButton;
            json["secondaryButton"] = input.SecondaryButton;
            json["menuButton"] = input.MenuButton;
            json["hasPose"] = pose.HasPose;
            json["pose"] = string.IsNullOrEmpty(pose.Pose)
                ? EnterpriseCollectionRecorder.InvalidControllerPose
                : pose.Pose;
            json["status"] = (double)pose.Status;
            json["timeStampNs"] = (double)pose.TimeStampNs;
            json["type"] = (double)pose.Type;
            json["poseError"] = (double)pose.PoseError;

            return json;
        }

        private bool IsControllerActiveInput()
        {
            return _cachedActiveInputDevice == (int)ActiveInputDevice.ControllerActive;
        }

        private void LogEnterpriseControllerPayloadIfNeeded(JsonData controller, long sampleSeq)
        {
            if (!outputEnterpriseControllerPayloadToLog)
            {
                return;
            }

            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            double elapsedSeconds = (nowTicks - _lastEnterpriseControllerPayloadLogTicks) /
                                    (double)System.Diagnostics.Stopwatch.Frequency;
            float intervalSeconds = Mathf.Max(0.1f, enterpriseControllerPayloadLogIntervalSeconds);
            if (_lastEnterpriseControllerPayloadLogTicks != 0 && elapsedSeconds < intervalSeconds)
            {
                return;
            }

            _lastEnterpriseControllerPayloadLogTicks = nowTicks;
            string schemaMessage =
                "TCP enterprise controller schema: " +
                "axisX=double axisY=double axisClick=bool grip=double trigger=double " +
                "primaryButton=bool secondaryButton=bool menuButton=bool hasPose=bool pose=string " +
                "status=double timeStampNs=double type=double poseError=double";
            string payloadMessage =
                $"TCP enterprise controller payload: sampleSeq={sampleSeq} json={controller.ToJson()}";
            Debug.Log($"{Tag}{schemaMessage}");
            Debug.Log($"{Tag}{payloadMessage}");
        }

        private bool IsEnterpriseDirectTrackingReady()
        {
            return ShouldSendEnterpriseTrackingDirectly() && _state == SocketState.WORKING && _connectInited;
        }

        private void LogEnterpriseDirectTrackingGateIfChanged()
        {
            string status = $"ready={IsEnterpriseDirectTrackingReady()} send={SendTrackingData} " +
                            $"directEnabled={EnterpriseDirectTrackingEnabled} " +
                            $"source={TrackingDataSourceCtrl.CurrentSource} " +
                            $"head={TrackingData.HeadOn} controller={TrackingData.ControllerOn} " +
                            $"hand={TrackingData.HandTrackingOn} trackingType={TrackingData.TrackingTypeValue} " +
                            $"state={_state} connectInited={_connectInited}";
            if (status == _lastDirectTrackingGateStatus)
            {
                return;
            }

            _lastDirectTrackingGateStatus = status;
            string message = $"TCP enterprise direct gate changed: {status}";
            LogWindow.Info(message);
            Debug.Log($"{Tag}{message}");
        }

        private void ResetDirectTrackingCache()
        {
            _lastDirectTrackingHeadSampleSeq = 0;
            _lastDirectTrackingControllerSampleSeq = 0;
        }

        private static int GetClampedSleepMs(int sleepMs)
        {
            return Mathf.Max(0, sleepMs);
        }

        private void RecordTrackingPacketSent(string mode)
        {
            _trackingPacketsSentInWindow++;
            _lastTrackingSendMode = mode;
            LogTrackingRateIfNeeded();
        }

        private void LogTrackingRateIfNeeded()
        {
            float intervalSeconds = Mathf.Max(0.1f, trackingRateLogIntervalSeconds);
            double elapsedSeconds = _trackingRateStopwatch.Elapsed.TotalSeconds;
            if (elapsedSeconds < intervalSeconds)
            {
                return;
            }

            double sendHz = elapsedSeconds > 0 ? _trackingPacketsSentInWindow / elapsedSeconds : 0;
            string rateMessage =
                $"TCP tracking send rate: {sendHz:F1}Hz mode={_lastTrackingSendMode} pending={_pendingDirectTrackingPackets}";
            if (outputTrackingRateToLogWindow)
            {
                LogWindow.Info(rateMessage);
            }

            Debug.Log($"{Tag}{rateMessage}");
            _trackingPacketsSentInWindow = 0;
            _trackingRateStopwatch.Restart();
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
                                if (sendData.IsDirectTracking)
                                {
                                    Interlocked.Decrement(ref _pendingDirectTrackingPackets);
                                }

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

                                if (sendData.IsDirectTracking)
                                {
                                    RecordTrackingPacketSent("enterprise_direct");
                                }
                            }

                            //Tracking data transmission
                            if (_connectInited && SendTrackingData && !ShouldSendEnterpriseTrackingDirectly())
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

                                    RecordTrackingPacketSent("legacy");
                                }
                            }
                            else
                            {
                                if (ShouldSendEnterpriseTrackingDirectly())
                                {
                                    Thread.Yield();
                                }
                                else
                                {
                                    Thread.Sleep(14);
                                }
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

            ResetDirectTrackingCache();
            Interlocked.Exchange(ref _pendingDirectTrackingPackets, 0);
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
            public bool IsDirectTracking;

            public SendData(byte cmd, byte[] content, bool isDirectTracking = false)
            {
                Cmd = cmd;
                Content = content;
                IsDirectTracking = isDirectTracking;
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
