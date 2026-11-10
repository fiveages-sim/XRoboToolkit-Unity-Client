using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LitJson;
using Network;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.XR;
using CommonUsages = UnityEngine.XR.CommonUsages;
using InputDevice = UnityEngine.XR.InputDevice;

namespace Robot
{
    public class TcpHandler : MonoBehaviour
    {
        public const string Tag = ">>Tcp ";
        public const int RECEIVE_TIME_OUT_DEFAULT = 25000;
        public const int BUFFER_LEN = 1024 * 63;
        public const int TCP_PORT = 63901;

        public delegate void ReceiveFunctionMsg(string functionName, string value);

        public delegate void ReceiveMassage(NetPacket packet);

        public static event ReceiveMassage ReceiveEvent;
        public static event ReceiveFunctionMsg ReceiveFunctionEvent;

        public static volatile bool SendTrackingData = false;
        public static volatile bool EnterpriseDirectTrackingEnabled = true;
        private static object _sendObject = new object();
        private readonly ConcurrentQueue<NetPacket> _receivePackages = new ConcurrentQueue<NetPacket>();
        private static readonly ConcurrentQueue<SendData> _sendDatas = new ConcurrentQueue<SendData>();

        private Socket _socket;
        private int _connectionGeneration;
        private SocketState _state = SocketState.NONE;

        private bool _connectInited = false;
        private static string _address = "127.0.0.1"; // PC IP Address
        private static string _localAddress = ""; // Optional source address for deterministic routing
        private int _port = 8888; //PC Port
        private int _sendTimeout = 15000; // timeout
        [SerializeField] private int trackingThreadIdleSleepMs = 5;
        [SerializeField] private int trackingThreadWaitForHeadSleepMs = 5;
        [SerializeField] private int trackingThreadQueueBackpressureSleepMs = 5;
        // Max block when waiting for the next sample event (not a rate limiter).
        [SerializeField] private int trackingThreadSampleWaitTimeoutMs = 5;
        // Brief spin before WaitOne so a same-timeslice Publish does not always sleep.
        [SerializeField] private int trackingThreadWakeSpinUs = 50;
        [SerializeField] private bool outputTrackingRateToLogWindow = false;
        [SerializeField] private float trackingRateLogIntervalSeconds = 1f;
        [SerializeField] private bool outputEnterpriseControllerPayloadToLog = false;
        [SerializeField] private float enterpriseControllerPayloadLogIntervalSeconds = 1f;
        /// <summary>
        /// B0: only attach sources whose seq advanced since last sent packet. Missing key =
        /// fa-py keeps previous cache. false = official always-bundle of enabled sources.
        /// </summary>
        [SerializeField] private bool omitUnchangedSources = true;
        /// <summary>
        /// When over bandwidthBudgetKiBps (or pending stuck), lower Hand/Body/Motion sample
        /// caps first; Head packet rate is never reduced here.
        /// </summary>
        [SerializeField] private bool autoThrottleEnabled = true;
        [SerializeField] private float bandwidthBudgetKiBps = 1200f;
        [SerializeField] private float bandwidthRecoverMarginKiBps = 250f;
        // Start at panel-class rate; auto-throttle steps down under bandwidth pressure.
        [SerializeField] private int auxThrottleMaxHz = 400;
        [SerializeField] private int auxThrottleMinHz = 15;
        [SerializeField] private int auxThrottleStepHz = 25;
        private Thread _sendThread;
        private Thread _trackingThread;
        private static readonly AutoResetEvent s_sendSignal = new AutoResetEvent(false);
        // Sample threads Set this; OnTrackingThread waits instead of Sleep-polling.
        private static readonly AutoResetEvent s_trackingSampleSignal = new AutoResetEvent(false);
        private static int s_omitUnchangedSources = 1;
        private static int s_autoThrottleEnabled = 1;
        private static int s_auxThrottleHz = 400;
        private ByteBuffer receiveBuffer;
        private string _appVersion = "";
        private string _deviceSN = "";
        private JsonData _trackingJsonData = new JsonData();
        private JsonData _sendJson = new JsonData();
        private TrackingData _trackingData = new TrackingData();
        private ConcurrentQueue<string> _sendTrackingMsg = new ConcurrentQueue<string>();
        private int _pendingDirectTrackingPackets = 0;
        private bool _cachedAppFocus = true;
        private int _cachedActiveInputDevice = (int)ActiveInputDevice.ControllerActive;
        private readonly object _controllerInputLock = new object();
        private ControllerInputState _cachedLeftControllerInput;
        private ControllerInputState _cachedRightControllerInput;
        private long _lastDirectTrackingHeadSampleSeq;
        private long _lastDirectTrackingControllerSampleSeq;
        private long _lastDirectTrackingHandSampleSeq;
        private long _lastDirectTrackingBodySampleSeq;
        private long _lastDirectTrackingMotionSampleSeq;
        private float _lastHeardSend = 0;
        private float _lastReconnectTime = 0;
        private bool _reconnectEnable = false;
        private readonly System.Diagnostics.Stopwatch _trackingRateStopwatch = new System.Diagnostics.Stopwatch();
        private int _trackingPacketsSentInWindow = 0;
        private long _trackingBytesSentInWindow = 0;
        private string _lastTrackingSendMode = "idle";
        private string _lastDirectTrackingGateStatus;
        private long _lastEnterpriseControllerPayloadLogTicks;

        // Direct tracking instrumentation counters (reset each reporting window)
        private long _directLoopCount;
        private long _directStaleReadCount;
        private long _directNewSampleCount;
        private long _directEnqueueCount;
        private long _lastDirectStatsLogTicks;

        private void Awake()
        {
            _appVersion = Application.version;
            _sendThread = new Thread(OnSendThread);
            _trackingThread = new Thread(OnTrackingThread);
            _trackingRateStopwatch.Start();
            ApplyTrackingSendPolicy();
        }

        private void OnValidate()
        {
            ApplyTrackingSendPolicy();
        }

        private void ApplyTrackingSendPolicy()
        {
            Volatile.Write(ref s_omitUnchangedSources, omitUnchangedSources ? 1 : 0);
            Volatile.Write(ref s_autoThrottleEnabled, autoThrottleEnabled ? 1 : 0);
            int maxHz = Mathf.Clamp(auxThrottleMaxHz, 1, 1000);
            int minHz = Mathf.Clamp(auxThrottleMinHz, 1, maxHz);
            auxThrottleMaxHz = maxHz;
            auxThrottleMinHz = minHz;
            int cur = Volatile.Read(ref s_auxThrottleHz);
            // Clamp into range; prefer max when unset/stale so Hand can track panel until budget bites.
            if (cur < minHz || cur > maxHz || cur == 0)
            {
                Volatile.Write(ref s_auxThrottleHz, maxHz);
            }
        }

        private void Start()
        {
            // Fresh run: begin at max aux rate; auto-throttle steps down if needed.
            Volatile.Write(ref s_auxThrottleHz, Mathf.Clamp(auxThrottleMaxHz, 1, 1000));
            ApplyTrackingSendPolicy();
        }

        public static bool OmitUnchangedSourcesEnabled
        {
            get { return Volatile.Read(ref s_omitUnchangedSources) != 0; }
        }

        /// <summary>
        /// Dynamic cap for Hand/Body/Motion sampling (auto-throttle). Head unaffected.
        /// </summary>
        public static int SharedAuxThrottleHz
        {
            get { return Math.Max(1, Volatile.Read(ref s_auxThrottleHz)); }
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

        public static string GetSourceIP
        {
            get { return _localAddress; }
        }

        public void Connect(string address)
        {
            Connect(address, null);
        }

        public void Connect(string address, string localAddress)
        {
            LogWindow.Info(
                string.IsNullOrEmpty(localAddress)
                    ? $"Attempting to connect to {address}"
                    : $"Attempting to connect from {localAddress} to {address}");
            _address = address;
            _localAddress = localAddress ?? "";
            _reconnectEnable = false;
            Connect();
        }

        private void Connect()
        {
            _port = TCP_PORT;
            ConnectErrorInfo = "";
            int generation;
            Socket previous;
            lock (_sendObject)
            {
                generation = ++_connectionGeneration;
                _state = SocketState.CREATE;
                _connectInited = false;
                previous = _socket;
                _socket = null;
            }

            DisposeSocket(previous);
            Debug.Log(
                $"{Tag}connect to server: source " +
                $"{(string.IsNullOrEmpty(_localAddress) ? "auto" : _localAddress)} " +
                $"target {_address} port {_port} generation {generation}");

            Socket socket = null;
            try
            {
                IPAddress ia = IPAddress.Parse(_address);
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Blocking = true;
                socket.SendTimeout = _sendTimeout;
                socket.NoDelay = true;
                socket.ReceiveTimeout = RECEIVE_TIME_OUT_DEFAULT;
                if (!string.IsNullOrEmpty(_localAddress))
                {
                    IPAddress localIp = IPAddress.Parse(_localAddress);
                    socket.Bind(new IPEndPoint(localIp, 0));
                }

                bool stillCurrent;
                lock (_sendObject)
                {
                    stillCurrent = _connectionGeneration == generation;
                    if (stillCurrent)
                    {
                        _socket = socket;
                        _state = SocketState.CONNECTING;
                    }
                }

                if (!stillCurrent)
                {
                    DisposeSocket(socket);
                    return;
                }

                socket.BeginConnect(ia, _port, ConnectCallback, new IoAttempt(generation, socket));
            }
            catch (Exception e)
            {
                DisposeSocket(socket);
                lock (_sendObject)
                {
                    if (_connectionGeneration == generation)
                    {
                        _socket = null;
                        _state = SocketState.CONNECT_ERROR;
                        ConnectErrorInfo = e.Message;
                    }
                }

                LogWindow.Error($"TCP connection failed: {e.Message}");
                Debug.LogError(Tag + "Connection failed: " + e.Message);
            }
        }

        private void ConnectCallback(IAsyncResult async)
        {
            var attempt = (IoAttempt)async.AsyncState;
            try
            {
                attempt.Socket.EndConnect(async);
                bool currentAndConnected;
                lock (_sendObject)
                {
                    currentAndConnected = IsCurrentAttemptLocked(attempt) && attempt.Socket.Connected;
                    if (currentAndConnected)
                    {
                        _reconnectEnable = true;
                        _state = SocketState.WORKING;
                        _connectInited = false;
                    }
                    else if (IsCurrentAttemptLocked(attempt))
                    {
                        _state = SocketState.CONNECT_ERROR;
                        ConnectErrorInfo = "connect error";
                    }
                }

                if (!currentAndConnected)
                {
                    if (attempt.Generation == _connectionGeneration)
                    {
                        LogWindow.Error("TCP connection failed - socket not connected");
                        Debug.LogError(Tag + "connect Error");
                    }

                    return;
                }

                if (!IsCurrentAttempt(attempt))
                {
                    return;
                }

                LogWindow.Info("TCP socket connection established successfully");
                if (_sendThread.ThreadState == ThreadState.Unstarted)
                {
                    _sendThread.Start();
                }

                if (_trackingThread.ThreadState == ThreadState.Unstarted)
                {
                    _trackingThread.Start();
                }

                receiveBuffer = new ByteBuffer(BUFFER_LEN);
                if (!IsCurrentAttempt(attempt))
                {
                    return;
                }

                attempt.Socket.BeginReceive(receiveBuffer.data, receiveBuffer.GetReadableCount(),
                    receiveBuffer.GetRemainCapacity(), SocketFlags.None, OnDataReceived,
                    attempt);
                if (!string.IsNullOrEmpty(_deviceSN))
                {
                    ConnectInit();
                }

                Debug.Log(Tag + "Socket Connected!  ");
            }
            catch (Exception e)
            {
                bool current;
                lock (_sendObject)
                {
                    current = IsCurrentAttemptLocked(attempt);
                    if (current)
                    {
                        ConnectErrorInfo = e.Message;
                        _state = SocketState.CONNECT_ERROR;
                    }
                }

                if (!current)
                {
                    return;
                }

                LogWindow.Error($"TCP connection exception: {e.Message}");
                Debug.LogError(Tag + "Connect error,Exception " + e);
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
            var attempt = ar.AsyncState as IoAttempt;
            if (attempt == null)
            {
                return;
            }

            int bytesRead;
            try
            {
                bytesRead = attempt.Socket.EndReceive(ar);
            }
            catch (Exception ex)
            {
                if (!IsCurrentAttempt(attempt))
                {
                    return;
                }

                LogWindow.Error($"TCP data receive error: {ex.Message}");
                Debug.LogError($"Error: {ex.Message}");
                CloseIfCurrent(attempt.Generation);
                return;
            }

            if (!IsCurrentAttempt(attempt) || _state != SocketState.WORKING)
            {
                return;
            }

            try
            {
                if (bytesRead > 0)
                {
                    receiveBuffer.AddWriteIndex(bytesRead);
                    bool msgEnough;
                    do
                    {
                        msgEnough = PackageHandle.Unpack(receiveBuffer, out var package);
                        if (msgEnough)
                        {
                            if (package.Cmd == NetCMD.PACKET_CMD_FROM_CONTROLLER_COMMON_FUNCTION)
                            {
                                string content = package.ToString();
                                if (content != null && content.Contains("timeTest"))
                                {
                                    Send(NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION, "timeTest");
                                }
                            }

                            _receivePackages.Enqueue(package);
                        }
                    } while (msgEnough);

                    receiveBuffer.RemoveReadedBytes();

                    if (!IsCurrentAttempt(attempt))
                    {
                        return;
                    }

                    attempt.Socket.BeginReceive(receiveBuffer.data, receiveBuffer.GetReadableCount(),
                        receiveBuffer.GetRemainCapacity(), SocketFlags.None, OnDataReceived,
                        attempt);
                }
                else
                {
                    LogWindow.Warn("TCP client disconnected - no data received");
                    Debug.Log("Client disconnected.");
                    CloseIfCurrent(attempt.Generation);
                }
            }
            catch (Exception ex)
            {
                if (!IsCurrentAttempt(attempt))
                {
                    return;
                }

                LogWindow.Error($"TCP data receive error: {ex.Message}");
                Debug.LogError($"Error: {ex.Message}");
                CloseIfCurrent(attempt.Generation);
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
            s_sendSignal.Set();
        }

        public static void SendCustomData(byte[] msg)
        {
            _sendDatas.Enqueue(new SendData(NetCMD.PACKET_CMD_CUSTOM_TO_PC, msg));
            s_sendSignal.Set();
        }

        private void Update()
        {
            _cachedAppFocus = Application.isFocused;
            _cachedActiveInputDevice = (int)PXR_HandTracking.GetActiveInputDevice();
            bool captureControllerInput = SendTrackingData &&
                                          TrackingData.ControllerOn &&
                                          TrackingDataSourceCtrl.UseEnterpriseSDK;
            if (captureControllerInput)
            {
                ControllerInputState leftInput = ReadControllerInput(XRNode.LeftHand);
                ControllerInputState rightInput = ReadControllerInput(XRNode.RightHand);
                lock (_controllerInputLock)
                {
                    _cachedLeftControllerInput = leftInput;
                    _cachedRightControllerInput = rightInput;
                }
            }
            else
            {
                lock (_controllerInputLock)
                {
                    _cachedLeftControllerInput = default(ControllerInputState);
                    _cachedRightControllerInput = default(ControllerInputState);
                }
            }

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

            // USB binds a source address and is retried by UIOperate. This loop only
            // covers a manually entered route, so the two retries cannot close each other.
            if (_reconnectEnable && string.IsNullOrEmpty(_localAddress))
            {
                if (State == SocketState.CLOSE || State == SocketState.CONNECT_ERROR)
                {
                    if (!string.IsNullOrEmpty(_address))
                    {
                        if (Time.time - _lastReconnectTime > 2)
                        {
                            Reconnect();
                            _lastReconnectTime = Time.time;
                        }
                    }
                }
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
                        if (ReceiveFunctionEvent != null)
                        {
                            ReceiveFunctionEvent.Invoke(functionName, json["value"].ToString());
                        }
                    }
                    else if (ReceiveEvent != null)
                    {
                        ReceiveEvent.Invoke(packet);
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
            Connect(_address, _localAddress);
        }


        /// <summary>
        /// Wake OnTrackingThread after a TCP cache seq advance. Sample threads call this;
        /// wire protocol is unchanged.
        /// </summary>
        public static void NotifyTrackingSampleAvailable()
        {
            try
            {
                s_trackingSampleSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void WaitForTrackingSample(int timeoutMs)
        {
            int spinUs = Mathf.Max(0, trackingThreadWakeSpinUs);
            if (spinUs > 0)
            {
                long spinTicks = Math.Max(1,
                    (System.Diagnostics.Stopwatch.Frequency * spinUs) / 1000000L);
                long spinUntil = System.Diagnostics.Stopwatch.GetTimestamp() + spinTicks;
                while (System.Diagnostics.Stopwatch.GetTimestamp() < spinUntil)
                {
                    if (s_trackingSampleSignal.WaitOne(0))
                    {
                        return;
                    }

                    Thread.SpinWait(8);
                }
            }

            s_trackingSampleSignal.WaitOne(Mathf.Max(1, GetClampedSleepMs(timeoutMs)));
        }

        private void OnTrackingThread()
        {
            while (_state != SocketState.DESTROY)
            {
                if (!IsEnterpriseDirectTrackingReady())
                {
                    LogEnterpriseDirectTrackingGateIfChanged();
                    ResetDirectTrackingCache();
                    WaitForTrackingSample(trackingThreadIdleSleepMs);
                    continue;
                }

                // Backpressure: do not build another direct packet. Wait for send to drain
                // (NotifyTrackingSampleAvailable after pending--), not Sleep-and-miss the beat.
                if (_pendingDirectTrackingPackets >= 2)
                {
                    WaitForTrackingSample(trackingThreadQueueBackpressureSleepMs);
                    continue;
                }

                Interlocked.Increment(ref _directLoopCount);

                // Read the latest cache and its sequence numbers first. Do not build any
                // JsonData objects until we know that at least one source has advanced.
                string headPose = null;
                int headStatus = 0;
                EnterpriseCollectionRecorder.EnterpriseControllerTcpPose leftController =
                    default(EnterpriseCollectionRecorder.EnterpriseControllerTcpPose);
                EnterpriseCollectionRecorder.EnterpriseControllerTcpPose rightController =
                    default(EnterpriseCollectionRecorder.EnterpriseControllerTcpPose);
                string handJson = null;
                string bodyJson = null;
                string motionJson = null;
                long headSampleSeq = _lastDirectTrackingHeadSampleSeq;
                long controllerSampleSeq = _lastDirectTrackingControllerSampleSeq;
                long handSampleSeq = _lastDirectTrackingHandSampleSeq;
                long bodySampleSeq = _lastDirectTrackingBodySampleSeq;
                long motionSampleSeq = _lastDirectTrackingMotionSampleSeq;
                bool headNew = false;
                bool controllerNew = false;
                bool handNew = false;
                bool bodyNew = false;
                bool motionNew = false;

                LogEnterpriseDirectTrackingGateIfChanged();
                if (TrackingData.HeadOn)
                {
                    if (!EnterpriseCollectionRecorder.TryGetLatestEnterpriseHeadForTcp(
                            out headPose,
                            out headStatus,
                            out headSampleSeq))
                    {
                        // Head requested but not ready yet: do not block Hand/Tracker-only sends.
                        if (!TrackingData.ControllerOn &&
                            !TrackingData.HandTrackingOn &&
                            TrackingData.TrackingTypeValue <= TrackingData.TrackingType.None)
                        {
                            WaitForTrackingSample(trackingThreadWaitForHeadSleepMs);
                            continue;
                        }
                    }
                    else
                    {
                        headNew = headSampleSeq != _lastDirectTrackingHeadSampleSeq;
                    }
                }

                if (TrackingData.ControllerOn)
                {
                    EnterpriseCollectionRecorder.TryGetLatestEnterpriseControllerForTcp(
                        out leftController,
                        out rightController,
                        out controllerSampleSeq);
                    if (!IsControllerActiveInput())
                    {
                        leftController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                        rightController = EnterpriseCollectionRecorder.CreateInvalidEnterpriseControllerTcpPose();
                    }

                    controllerNew = controllerSampleSeq != _lastDirectTrackingControllerSampleSeq;
                }

                if (TrackingData.HandTrackingOn &&
                    EnterpriseAuxTrackingSampler.TryGetLatestHandJson(out handJson, out handSampleSeq))
                {
                    handNew = handSampleSeq != _lastDirectTrackingHandSampleSeq;
                }

                if (TrackingData.TrackingTypeValue == TrackingData.TrackingType.Body &&
                    EnterpriseAuxTrackingSampler.TryGetLatestBodyJson(out bodyJson, out bodySampleSeq))
                {
                    bodyNew = bodySampleSeq != _lastDirectTrackingBodySampleSeq;
                }

                if (TrackingData.TrackingTypeValue == TrackingData.TrackingType.Motion &&
                    EnterpriseAuxTrackingSampler.TryGetLatestMotionJson(out motionJson, out motionSampleSeq))
                {
                    motionNew = motionSampleSeq != _lastDirectTrackingMotionSampleSeq;
                }

                bool hasNewTrackingSample = headNew || controllerNew || handNew || bodyNew || motionNew;
                if (!hasNewTrackingSample)
                {
                    Interlocked.Increment(ref _directStaleReadCount);
                    WaitForTrackingSample(trackingThreadSampleWaitTimeoutMs);
                    continue;
                }

                Interlocked.Increment(ref _directNewSampleCount);

                if (!SendTrackingData)
                {
                    WaitForTrackingSample(trackingThreadIdleSleepMs);
                    continue;
                }

                // A packet already carries the latest sample of every enabled source, so
                // emitting one per source event duplicates payloads. Trigger on the first
                // enabled source that has data instead: that keeps packets 1:1 with the
                // sampling period, and unlike a sleep-based rate limiter it does not
                // overshoot (Android sleep granularity is several milliseconds).
                bool headReady = headPose != null;
                bool controllerReady = controllerSampleSeq != 0;
                bool handReady = !string.IsNullOrEmpty(handJson);
                bool bodyReady = !string.IsNullOrEmpty(bodyJson);
                bool primaryNew;
                if (TrackingData.HeadOn && headReady)
                {
                    primaryNew = headNew;
                }
                else if (TrackingData.ControllerOn && controllerReady)
                {
                    primaryNew = controllerNew;
                }
                else if (TrackingData.HandTrackingOn && handReady)
                {
                    primaryNew = handNew;
                }
                else if (bodyReady)
                {
                    primaryNew = bodyNew;
                }
                else
                {
                    primaryNew = motionNew;
                }

                if (!primaryNew)
                {
                    // Non-primary source advanced only: wait for primary (usually Head) event.
                    WaitForTrackingSample(trackingThreadSampleWaitTimeoutMs);
                    continue;
                }

                bool omitUnchanged = OmitUnchangedSourcesEnabled;
                // Include source if official bundle, or B0 and seq advanced. Always emit
                // timeStampNs so fa-py frame gate advances even when only Head changed.
                bool includeHead = TrackingData.HeadOn && !string.IsNullOrEmpty(headPose) &&
                                   (!omitUnchanged || headNew);
                bool includeController = TrackingData.ControllerOn &&
                                         (!omitUnchanged || controllerNew);
                bool includeHand = TrackingData.HandTrackingOn &&
                                   !string.IsNullOrEmpty(handJson) &&
                                   (!omitUnchanged || handNew);
                bool includeBody = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Body &&
                                   !string.IsNullOrEmpty(bodyJson) &&
                                   (!omitUnchanged || bodyNew);
                bool includeMotion = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Motion &&
                                     !string.IsNullOrEmpty(motionJson) &&
                                     (!omitUnchanged || motionNew);

                StringBuilder valueBuilder = new StringBuilder(1024);
                bool hasField = false;
                valueBuilder.Append('{');

                if (includeHead)
                {
                    AppendJsonField(valueBuilder, ref hasField, "Head");
                    valueBuilder.Append("{\"pose\":\"").Append(headPose)
                        .Append("\",\"status\":").Append(headStatus).Append('}');
                }

                if (includeController)
                {
                    GetControllerInputSnapshot(out ControllerInputState leftInput, out ControllerInputState rightInput);
                    if (!IsControllerActiveInput())
                    {
                        leftInput = default(ControllerInputState);
                        rightInput = default(ControllerInputState);
                    }
                    JsonData controller = BuildEnterpriseControllerJson(
                        leftController,
                        rightController,
                        leftInput,
                        rightInput);
                    AppendJsonField(valueBuilder, ref hasField, "Controller");
                    valueBuilder.Append(controller.ToJson());
                    LogEnterpriseControllerPayloadIfNeeded(controller, controllerSampleSeq);
                }

                // Hand/Body/Motion: pre-serialized JSON embedded as-is.
                if (includeHand)
                {
                    AppendJsonField(valueBuilder, ref hasField, "Hand");
                    valueBuilder.Append(handJson);
                }

                if (includeBody)
                {
                    AppendJsonField(valueBuilder, ref hasField, "Body");
                    valueBuilder.Append(bodyJson);
                }

                if (includeMotion)
                {
                    AppendJsonField(valueBuilder, ref hasField, "Motion");
                    valueBuilder.Append(motionJson);
                }

                AppendJsonField(valueBuilder, ref hasField, "timeStampNs");
                valueBuilder.Append(Utils.GetCurrentTimestamp());
                AppendJsonField(valueBuilder, ref hasField, "appState");
                valueBuilder.Append(_cachedAppFocus ? "{\"focus\":true}" : "{\"focus\":false}");
                AppendJsonField(valueBuilder, ref hasField, "Input");
                valueBuilder.Append(_cachedActiveInputDevice);
                valueBuilder.Append('}');

                _sendDatas.Enqueue(new SendData(
                    NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION,
                    Encoding.UTF8.GetBytes(BuildTrackingFunctionPacket(valueBuilder)),
                    true));
                s_sendSignal.Set();
                Interlocked.Increment(ref _directEnqueueCount);
                Interlocked.Increment(ref _pendingDirectTrackingPackets);
                // B0: only advance last-sent seq for sources actually carried; skipped
                // sources stay "new" for a later packet (or until throttle allows).
                if (!omitUnchanged || includeHead)
                {
                    _lastDirectTrackingHeadSampleSeq = headSampleSeq;
                }

                if (!omitUnchanged || includeController)
                {
                    _lastDirectTrackingControllerSampleSeq = controllerSampleSeq;
                }

                if (!omitUnchanged || includeHand)
                {
                    _lastDirectTrackingHandSampleSeq = handSampleSeq;
                }

                if (!omitUnchanged || includeBody)
                {
                    _lastDirectTrackingBodySampleSeq = bodySampleSeq;
                }

                if (!omitUnchanged || includeMotion)
                {
                    _lastDirectTrackingMotionSampleSeq = motionSampleSeq;
                }

                LogDirectTrackingStatsIfNeeded(headSampleSeq, controllerSampleSeq);
            }
        }

        private bool ShouldSendEnterpriseTrackingDirectly()
        {
            return SendTrackingData &&
                   EnterpriseDirectTrackingEnabled &&
                   TrackingDataSourceCtrl.UseEnterpriseSDK &&
                   (TrackingData.HeadOn ||
                    TrackingData.ControllerOn ||
                    TrackingData.HandTrackingOn ||
                    TrackingData.TrackingTypeValue > TrackingData.TrackingType.None);
        }

        private static void AppendJsonField(StringBuilder builder, ref bool hasField, string fieldName)
        {
            if (hasField)
            {
                builder.Append(',');
            }

            hasField = true;
            builder.Append('"').Append(fieldName).Append("\":");
        }

        /// <summary>
        /// Wraps an assembled value object into the Tracking packet. The value travels as a
        /// JSON string, so it is escaped in one pass here instead of being written into a
        /// JsonData tree and serialized twice per packet.
        /// </summary>
        private static string BuildTrackingFunctionPacket(StringBuilder valueBuilder)
        {
            StringBuilder packet = new StringBuilder(valueBuilder.Length + 64);
            packet.Append("{\"functionName\":\"Tracking\",\"value\":\"");
            for (int i = 0; i < valueBuilder.Length; i++)
            {
                char c = valueBuilder[i];
                switch (c)
                {
                    case '"':
                        packet.Append("\\\"");
                        break;
                    case '\\':
                        packet.Append("\\\\");
                        break;
                    case '\n':
                        packet.Append("\\n");
                        break;
                    case '\r':
                        packet.Append("\\r");
                        break;
                    case '\t':
                        packet.Append("\\t");
                        break;
                    default:
                        packet.Append(c);
                        break;
                }
            }

            packet.Append("\"}");
            return packet.ToString();
        }

        private static JsonData BuildEnterpriseControllerJson(
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose left,
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose right,
            ControllerInputState leftInput,
            ControllerInputState rightInput)
        {
            JsonData controller = new JsonData();
            controller["left"] = BuildEnterpriseControllerSideJson(left, leftInput);
            controller["right"] = BuildEnterpriseControllerSideJson(right, rightInput);
            return controller;
        }

        private static JsonData BuildEnterpriseControllerSideJson(
            EnterpriseCollectionRecorder.EnterpriseControllerTcpPose pose,
            ControllerInputState input)
        {
            JsonData json = new JsonData();
            json["axisX"] = input.AxisX;
            json["axisY"] = input.AxisY;
            json["axisClick"] = input.AxisClick;
            json["grip"] = input.Grip;
            json["trigger"] = input.Trigger;
            json["primaryButton"] = input.PrimaryButton;
            json["secondaryButton"] = input.SecondaryButton;
            json["menuButton"] = input.MenuButton;
            json["inputDeviceValid"] = input.DeviceValid;
            json["axisValid"] = input.AxisValid;
            json["axisClickValid"] = input.AxisClickValid;
            json["gripValid"] = input.GripValid;
            json["triggerValid"] = input.TriggerValid;
            json["primaryButtonValid"] = input.PrimaryButtonValid;
            json["secondaryButtonValid"] = input.SecondaryButtonValid;
            json["menuButtonValid"] = input.MenuButtonValid;
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

        private static ControllerInputState ReadControllerInput(XRNode node)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            bool axisValid = device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 axis);
            bool axisClickValid = device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool axisClick);
            bool gripValid = device.TryGetFeatureValue(CommonUsages.grip, out float grip);
            bool triggerValid = device.TryGetFeatureValue(CommonUsages.trigger, out float trigger);
            bool primaryButtonValid = device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primaryButton);
            bool secondaryButtonValid = device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondaryButton);
            bool menuButtonValid = device.TryGetFeatureValue(CommonUsages.menuButton, out bool menuButton);
            return new ControllerInputState
            {
                DeviceValid = device.isValid,
                AxisX = axis.x,
                AxisY = axis.y,
                AxisClick = axisClick,
                Grip = grip,
                Trigger = trigger,
                PrimaryButton = primaryButton,
                SecondaryButton = secondaryButton,
                MenuButton = menuButton,
                AxisValid = device.isValid && axisValid,
                AxisClickValid = device.isValid && axisClickValid,
                GripValid = device.isValid && gripValid,
                TriggerValid = device.isValid && triggerValid,
                PrimaryButtonValid = device.isValid && primaryButtonValid,
                SecondaryButtonValid = device.isValid && secondaryButtonValid,
                MenuButtonValid = device.isValid && menuButtonValid
            };
        }

        private void GetControllerInputSnapshot(
            out ControllerInputState left,
            out ControllerInputState right)
        {
            lock (_controllerInputLock)
            {
                left = _cachedLeftControllerInput;
                right = _cachedRightControllerInput;
            }
        }

        private bool IsControllerActiveInput()
        {
            return _cachedActiveInputDevice == (int)ActiveInputDevice.ControllerActive;
        }

        private struct ControllerInputState
        {
            public bool DeviceValid;
            public float AxisX;
            public float AxisY;
            public bool AxisClick;
            public float Grip;
            public float Trigger;
            public bool PrimaryButton;
            public bool SecondaryButton;
            public bool MenuButton;
            public bool AxisValid;
            public bool AxisClickValid;
            public bool GripValid;
            public bool TriggerValid;
            public bool PrimaryButtonValid;
            public bool SecondaryButtonValid;
            public bool MenuButtonValid;
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
                "inputDeviceValid=bool *Valid=bool status=double timeStampNs=double " +
                "type=double poseError=double";
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
            _lastDirectTrackingHandSampleSeq = 0;
            _lastDirectTrackingBodySampleSeq = 0;
            _lastDirectTrackingMotionSampleSeq = 0;
        }

        private static int GetClampedSleepMs(int sleepMs)
        {
            return Mathf.Max(0, sleepMs);
        }

        private void RecordTrackingPacketSent(string mode)
        {
            // Panel "FPS" is the tracking packet rate, so both send paths must feed it.
            FPSDisplay.UpdateTime();
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
            double kibPerSecond = elapsedSeconds > 0
                ? Interlocked.Exchange(ref _trackingBytesSentInWindow, 0) / 1024.0 / elapsedSeconds
                : 0;
            UpdateAutoThrottle(kibPerSecond, _pendingDirectTrackingPackets);
            string rateMessage =
                $"TCP tracking send rate: {sendHz:F1}Hz {kibPerSecond:F1}KiB/s mode={_lastTrackingSendMode} " +
                $"pending={_pendingDirectTrackingPackets} omitUnchanged={OmitUnchangedSourcesEnabled} " +
                $"auxThrottleHz={SharedAuxThrottleHz}";
            if (outputTrackingRateToLogWindow)
            {
                LogWindow.Info(rateMessage);
            }

            Debug.Log($"{Tag}{rateMessage}");
            _trackingPacketsSentInWindow = 0;
            _trackingRateStopwatch.Restart();
        }

        private void UpdateAutoThrottle(double kibPerSecond, int pending)
        {
            if (Volatile.Read(ref s_autoThrottleEnabled) == 0)
            {
                return;
            }

            int maxHz = Mathf.Clamp(auxThrottleMaxHz, 1, 1000);
            int minHz = Mathf.Clamp(auxThrottleMinHz, 1, maxHz);
            int step = Mathf.Max(1, auxThrottleStepHz);
            int cur = Volatile.Read(ref s_auxThrottleHz);
            cur = Mathf.Clamp(cur, minHz, maxHz);

            bool overBudget = kibPerSecond > bandwidthBudgetKiBps || pending >= 2;
            bool underBudget = kibPerSecond < (bandwidthBudgetKiBps - Mathf.Max(0f, bandwidthRecoverMarginKiBps)) &&
                               pending <= 0;
            int next = cur;
            if (overBudget && cur > minHz)
            {
                next = Math.Max(minHz, cur - step);
            }
            else if (underBudget && cur < maxHz)
            {
                next = Math.Min(maxHz, cur + step);
            }

            if (next != cur)
            {
                Volatile.Write(ref s_auxThrottleHz, next);
                Debug.Log($"{Tag}auto-throttle auxHz {cur} -> {next} " +
                          $"(kib/s={kibPerSecond:F1} budget={bandwidthBudgetKiBps:F0} pending={pending})");
            }
        }

        private void LogDirectTrackingStatsIfNeeded(long headSeq, long controllerSeq)
        {
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_lastDirectStatsLogTicks == 0)
            {
                _lastDirectStatsLogTicks = nowTicks;
                return;
            }

            double elapsedSeconds = (nowTicks - _lastDirectStatsLogTicks) /
                                    (double)System.Diagnostics.Stopwatch.Frequency;
            if (elapsedSeconds < 1.0)
            {
                return;
            }

            long loops = Interlocked.Exchange(ref _directLoopCount, 0);
            long stale = Interlocked.Exchange(ref _directStaleReadCount, 0);
            long fresh = Interlocked.Exchange(ref _directNewSampleCount, 0);
            long enqueued = Interlocked.Exchange(ref _directEnqueueCount, 0);
            _lastDirectStatsLogTicks = nowTicks;

            double loopHz = loops / elapsedSeconds;
            double staleHz = stale / elapsedSeconds;
            double freshHz = fresh / elapsedSeconds;
            double enqueueHz = enqueued / elapsedSeconds;

            Debug.Log($"{Tag}direct stats: loopHz={loopHz:F0} staleReadHz={staleHz:F0} " +
                      $"newSampleHz={freshHz:F0} enqueueHz={enqueueHz:F0} " +
                      $"headSeq={headSeq} controllerSeq={controllerSeq} " +
                      $"seqDelta={headSeq - controllerSeq} pending={_pendingDirectTrackingPackets}");
        }

        private void OnSendThread()
        {
            while (_state != SocketState.DESTROY)
            {
                Socket socket;
                int generation;
                bool closeStaleWorkingSocket;
                lock (_sendObject)
                {
                    if (_state == SocketState.DESTROY)
                    {
                        return;
                    }

                    closeStaleWorkingSocket = _state == SocketState.WORKING &&
                                              (_socket == null || !_socket.Connected);
                    if (_state != SocketState.WORKING || _socket == null || !_socket.Connected)
                    {
                        socket = null;
                        generation = _connectionGeneration;
                    }
                    else
                    {
                        socket = _socket;
                        generation = _connectionGeneration;
                    }
                }

                if (closeStaleWorkingSocket)
                {
                    CloseIfCurrent(generation);
                    continue;
                }

                if (socket == null)
                {
                    Thread.Sleep(100);
                    continue;
                }

                try
                {
                    bool sentQueuedPacket = false;
                    while (_sendDatas.TryDequeue(out SendData sendData))
                    {
                        sentQueuedPacket = true;
                        // A close/reconnect resets the counter while packets can still be
                        // queued, so heal it instead of trusting it to stay non-negative.
                        if (sendData.IsDirectTracking &&
                            Interlocked.Decrement(ref _pendingDirectTrackingPackets) < 0)
                        {
                            Interlocked.Exchange(ref _pendingDirectTrackingPackets, 0);
                        }

                        if (sendData.IsDirectTracking)
                        {
                            // Unblock tracking thread waiting on pending>=2.
                            NotifyTrackingSampleAvailable();
                        }

                        byte[] data = PackageHandle.Pack(sendData.Cmd, sendData.Content);
                        if (!TrySendAll(socket, data, out SocketError socketError))
                        {
                            LogWindow.Error($"TCP send error: {socketError}");
                            Debug.LogError(Tag + "send SocketError:" + socketError);
                            CloseIfCurrent(generation);
                            break;
                        }

                        if (sendData.Cmd == NetCMD.PACKET_CCMD_SEND_VERSION)
                        {
                            LogWindow.Info("PC connection established - version packet sent successfully");
                            Debug.Log("pc connected !");
                            _connectInited = true;
                        }

                        if (sendData.IsDirectTracking)
                        {
                            Interlocked.Add(ref _trackingBytesSentInWindow, data.Length);
                            RecordTrackingPacketSent("enterprise_direct");
                        }
                    }

                    if (sentQueuedPacket)
                    {
                        continue;
                    }

                    if (_connectInited && SendTrackingData && !ShouldSendEnterpriseTrackingDirectly())
                    {
                        if (_sendTrackingMsg.TryDequeue(out var msg))
                        {
                            _sendJson["functionName"] = "Tracking";
                            _sendJson["value"] = msg;
                            byte[] data = PackageHandle.Pack(NetCMD.PACKET_CCMD_TO_CONTROLLER_FUNCTION,
                                Encoding.UTF8.GetBytes(_sendJson.ToJson()));
                            int res = socket.Send(data, 0, data.Length, SocketFlags.None, out SocketError socketError);
                            if (res < data.Length)
                            {
                                Debug.LogWarning(Tag + "Incomplete data occurrence!");
                            }

                            if (socketError != SocketError.Success)
                            {
                                LogWindow.Error($"TCP tracking data send error: {socketError}");
                                Debug.LogError(Tag + "SocketError:" + socketError);
                                CloseIfCurrent(generation);
                                continue;
                            }

                            Interlocked.Add(ref _trackingBytesSentInWindow, data.Length);
                            RecordTrackingPacketSent("legacy");
                        }
                        else
                        {
                            s_sendSignal.WaitOne(1);
                        }
                    }
                    else
                    {
                        // Wait for the next queued packet instead of spinning. Direct tracking
                        // sets the signal on every enqueue, so this costs no extra latency.
                        s_sendSignal.WaitOne(14);
                    }
                }
                catch (Exception e)
                {
                    LogWindow.Error($"TCP send thread error: {e.Message}");
                    Debug.LogError(Tag + "Error OnSendThread:" + e);
                    CloseIfCurrent(generation);
                }
            }
        }

        private static bool TrySendAll(Socket socket, byte[] data, out SocketError socketError)
        {
            int totalBytes = data.Length;
            int bytesSent = 0;
            socketError = SocketError.Success;
            while (bytesSent < totalBytes)
            {
                int remainingBytes = totalBytes - bytesSent;
                bytesSent += socket.Send(data, bytesSent, remainingBytes, SocketFlags.None, out socketError);
                if (socketError != SocketError.Success)
                {
                    return false;
                }
            }

            return true;
        }

        private void OnDestroy()
        {
            if (_state != SocketState.CLOSE)
            {
                Close();
            }

            lock (_sendObject)
            {
                _state = SocketState.DESTROY;
            }

            NotifyTrackingSampleAvailable();
            try
            {
                s_sendSignal.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Close()
        {
            LogWindow.Info("Closing TCP connection");
            Debug.Log(Tag + "Close:");
            ClearReceiveQueue();
            Socket socket;
            lock (_sendObject)
            {
                _connectionGeneration++;
                _state = SocketState.CLOSE;
                _connectInited = false;
                socket = _socket;
                _socket = null;
            }

            ResetDirectTrackingCache();
            EnterpriseAuxTrackingSampler.ClearCaches();
            Interlocked.Exchange(ref _pendingDirectTrackingPackets, 0);
            NotifyTrackingSampleAvailable();
            DisposeSocket(socket);
        }

        private void CloseIfCurrent(int generation)
        {
            Socket socket;
            lock (_sendObject)
            {
                if (_connectionGeneration != generation)
                {
                    return;
                }

                _connectionGeneration++;
                _state = SocketState.CLOSE;
                _connectInited = false;
                socket = _socket;
                _socket = null;
            }

            LogWindow.Info("Closing TCP connection");
            Debug.Log(Tag + "Close:");
            ClearReceiveQueue();
            ResetDirectTrackingCache();
            EnterpriseAuxTrackingSampler.ClearCaches();
            Interlocked.Exchange(ref _pendingDirectTrackingPackets, 0);
            NotifyTrackingSampleAvailable();
            DisposeSocket(socket);
        }

        private void ClearReceiveQueue()
        {
            while (_receivePackages.TryDequeue(out _))
            {
            }
        }

        private bool IsCurrentAttempt(IoAttempt attempt)
        {
            lock (_sendObject)
            {
                return IsCurrentAttemptLocked(attempt);
            }
        }

        private bool IsCurrentAttemptLocked(IoAttempt attempt)
        {
            return attempt != null &&
                   attempt.Generation == _connectionGeneration &&
                   ReferenceEquals(_socket, attempt.Socket);
        }

        private static void DisposeSocket(Socket socket)
        {
            if (socket == null)
            {
                return;
            }

            try
            {
                socket.Close();
            }
            catch (Exception e)
            {
                LogWindow.Error($"TCP socket cleanup error: {e.Message}");
                Debug.LogError(Tag + "Clear Error:" + e);
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

        private sealed class IoAttempt
        {
            public readonly int Generation;
            public readonly Socket Socket;

            public IoAttempt(int generation, Socket socket)
            {
                Generation = generation;
                Socket = socket;
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
