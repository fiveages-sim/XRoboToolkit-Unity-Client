using System;
using System.Collections;
using System.IO;
using System.Text;
using LitJson;
using Network;
using Robot;
using Robot.Conf;
using Robot.Network;
using Robot.V2.Network;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.UI;
using XRoboToolkit.Network;

public partial class UICameraCtrl : MonoBehaviour
{
    public GameObject RemoteCameraWindowObj;

    public CameraRequestDialog CameraRequestDialog;
    public ResolutionDialog ResolutionDialog;
    public CameraSendInputDialog CameraSendInputDialog;
    public CustomButton ListenCameraBtn;
    public CustomButton CameraSendToBtn;

    public CustomButton ListenPCCameraBtn;
    public Dropdown cameraDropdown;

    public SetLERE setLere;

    private JsonData _recordJson;
    public Text CameraStatusText;
    public Text RecordStatusText;

    public CustomButton listenBtn;
    public VideoSourceManager videoSourceManager;

    public TcpManager tcpManager;
    private string logTag => "UICameraCtrl";

    private int streamingPort = 12345;

    [Space(30)] [Header("Record")] public RecordDialog RecordDialog;
    public CustomButton RecordBtn;

    public Toggle trackingToggle;
    public Toggle visionToggle;

    private readonly Robot.VisionTrackingSession _session = new Robot.VisionTrackingSession();
    private readonly Robot.SurfaceEncodeProbe _surfaceProbe = new Robot.SurfaceEncodeProbe();
    private readonly Robot.For4UBufferDumpSession _bufferDump = new Robot.For4UBufferDumpSession();
    private bool _useSurfaceProbe;
    private bool _useBufferDump;

    private bool _recordTrackingData
    {
        get { return _session.TrackingWriteEnabled; }
        set { _session.TrackingWriteEnabled = value; }
    }

    private void Awake()
    {
        RecordBtn.OnChange += OnRecordBtn;
        TcpHandler.ReceiveFunctionEvent += OnNetReceive;
        CameraHandle.AddStateListener(OnCameraStateChanged);
        UnityMainThreadDispatcher.Instance();
        Robot.SurfaceEncodeProbe.ClearLegacyMarkers();
        EnsureRecordStatusText();
        SetRemoteVisionStatus("Idle");
        SetRecordStatus("Idle");

        // Refactoring
        listenBtn.OnChange += OnListenCameraBtn;

        // Bind event
        tcpManager.OnServerReceived += OnServerReceived;
        tcpManager.OnClientReceived += OnClientReceived;

        operatorControlClient = new OperatorControlClient();
        operatorControlClient.Connected += OnControlClientConnected;
        operatorControlClient.Disconnected += OnControlClientDisconnected;
        operatorControlClient.DataReceived += OnClientDataReceived;
        operatorControlClient.Error += OnControlClientError;
        EnsureVideoProjectionRenderer();
        EnsureRecordStatusOverlay();
    }

    private void OnServerReceived(byte[] data)
    {
        // apply protocol
        Utils.WriteLog(logTag, $"OnServerReceived: {data.Length} bytes");

        // Log first few bytes for debugging
        if (data.Length > 0)
        {
            string hexDump = BitConverter.ToString(data, 0, Math.Min(data.Length, 32));
            Utils.WriteLog(logTag, $"First bytes (hex): {hexDump}");
        }

        EventExecutor.ExecuteInUpdate(() =>
        {
            try
            {
                Utils.WriteLog(logTag, $"Processing data...");

                // Check if it's a complete message first
                if (!NetworkDataProtocolSerializer.IsCompleteMessage(data))
                {
                    Utils.WriteLog(logTag, $"Incomplete message received");
                    return;
                }

                var protocol = NetworkDataProtocolSerializer.Deserialize(data);
                Utils.WriteLog(logTag,
                    $"Successfully deserialized: command='{protocol.command}', data length={protocol.data.Length}");

                // Process the command
                if (NetworkCommander.Instance == null)
                {
                    Utils.WriteLog(logTag, $"NetworkCommander.Instance is null");
                    return;
                }

                if (NetworkCommander.Instance.Processor == null)
                {
                    Utils.WriteLog(logTag, $"NetworkCommander.Instance.Processor is null");
                    return;
                }

                bool handled = NetworkCommander.Instance.Processor.ProcessCommand(protocol);
                Utils.WriteLog(logTag, $"Command processed: {handled}");
            }
            catch (Exception e)
            {
                Utils.WriteLog(logTag, $"Error processing command: {e.Message}");
                Utils.WriteLog(logTag, $"Stack trace: {e.StackTrace}");

                // Log detailed buffer analysis
                string bufferDebug = NetworkDataProtocolSerializer.DebugBufferContents(data);
                Utils.WriteLog(logTag, $"Buffer analysis:\n{bufferDebug}");
            }
        });
    }

    private void OnClientReceived(string msg)
    {
        Utils.WriteLog(logTag, $"OnClientReceived: {msg}");
    }

    public void OnListenCameraBtn(bool on)
    {
        if (on)
        {
            // check if the dropdown is updated
            if (cameraDropdown.options == null || cameraDropdown.options.Count == 0)
            {
                SetRemoteVisionStatus("Idle");
                return;
            }

            // get the camera source from the dropdown
            var cameraSource = cameraDropdown.options[cameraDropdown.value].text;

            // Update camera source, including shaders, etc.
            videoSourceManager.UpdateVideoSource(cameraSource);

            SetRemoteVisionStatus("Requesting");
            // send video stream request to the server
            CameraSendInputDialog.Show(
                RequestCameraStream,
                cameraSource,
                () => listenBtn.SetOn(false));
        }
        else
        {
            StopRemoteVisionSession();
            return;
        }

        // Update button
        listenBtn.SetOn(on);
    }

    public void RequestCameraStream(string ip)
    {
        if (listenBtn == null || !listenBtn.On || string.IsNullOrWhiteSpace(ip))
        {
            LogWindow.Warn("Camera/audio request ignored because Listen is off or the operator IP is empty.");
            return;
        }

        StopCameraRequestCoroutine();
        cameraRequestCoroutine = StartCoroutine(RequestCameraStreamCoroutine(ip));
    }

    IEnumerator RequestCameraStreamCoroutine(string ip)
    {
        int sessionId = BeginAudioSession(ip);

        if (TcpServer.Status == ServerStatus.Started)
        {
            // Close TcpServer first
            TcpManager.Instance.StopServer();
        }

        yield return new WaitForSeconds(0.1f);

        // Get camera parameters
        var camPara = VideoSourceConfigManager.Instance.CameraParameters;

        SetRemoteVisionStatus("Listening");
        bool panoramaSource = IsCurrentSourcePanorama();
        RemoteCameraWindowObj.SetActive(true);
        EnsureVideoProjectionRenderer();

        if (panoramaSource)
        {
            // Skybox only — never LE/RE or floating RawImage flash.
            setLere?.ForceSessionOff();
            videoProjectionRenderer?.PreparePendingPanorama();
        }
        else
        {
            // PICO4U / ZEDMINI: LE/RE stereo on Listen. Right-hand B toggles near
            // clip (ToggleCameraClippingPlane) so SBS floating window + main panel
            // become visible again; RemoteCamera X still closes the session.
            setLere?.BeginFlatStereoSession();
        }

        RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>().StartListen(camPara.width, camPara.height, camPara.fps,
            camPara.bitrate, streamingPort);

        yield return new WaitForSeconds(0.3f);

        if (panoramaSource)
        {
            setLere?.ForceSessionOff();
            videoProjectionRenderer?.PreparePendingPanorama();
        }
        else
        {
            setLere?.BeginFlatStereoSession();
        }

        yield return new WaitForSeconds(0.1f);

        // Reconnect for every Listen request so an existing client from another
        // operator/profile cannot leak a stale AUDIO_CONFIG into this session.
        if (operatorControlClient != null && operatorControlClient.IsRunning)
        {
            controlReconnectSuppressed = true;
            operatorControlClient.DisconnectSilently();
            controlClientConnected = false;
            yield return new WaitForSeconds(0.1f);
        }

        controlClientConnected = false;
        controlReconnectSuppressed = false;
        operatorControlClient.Connect(ip, tcpManager != null ? tcpManager.port : 13579);
        float connectDeadline = Time.realtimeSinceStartup + ControlConnectTimeoutSeconds;
        while (sessionId == audioSessionId &&
               listenBtn != null &&
               listenBtn.On &&
               !controlClientConnected &&
               Time.realtimeSinceStartup < connectDeadline)
        {
            yield return null;
        }

        if (sessionId != audioSessionId ||
            listenBtn == null ||
            !listenBtn.On)
        {
            cameraRequestCoroutine = null;
            yield break;
        }

        if (!controlClientConnected)
        {
            LogWindow.Warn("Operator control connection timed out; retrying.");
            controlReconnectSuppressed = true;
            operatorControlClient.DisconnectSilently();
            controlReconnectSuppressed = false;
            yield return new WaitForSecondsRealtime(ControlReconnectDelaySeconds);
            cameraRequestCoroutine = null;
            if (sessionId == audioSessionId && listenBtn.On)
            {
                RequestCameraStream(ip);
            }
            yield break;
        }

        var localIP = Utils.GetLocalIPv4();

        // Send request to the server
        var customConfig = CameraRequestSerializer.FromCameraParameters(
            camPara,
            0,
            2, // (int)PXRCaptureRenderMode.PXRCapture_RenderMode_3D
            VideoSourceConfigManager.Instance.CurrentVideoSource.camera,
            localIP, // local ip
            streamingPort);

        LogWindow.Info("Requesting camera stream with config: " + customConfig.ToString());

        var data = CameraRequestSerializer.Serialize(customConfig);

        clientProtocolBuffer.Clear();
        acceptAudioPortConfig = true;
        operatorControlClient.SendCommand(
            NetworkCommand.AUDIO_SESSION,
            Encoding.ASCII.GetBytes(currentAudioRequestId));
        operatorControlClient.SendCommand(NetworkCommand.OPEN_CAMERA, data);

        float ackDeadline = Time.realtimeSinceStartup + AudioPortAckTimeoutSeconds;
        while (sessionId == audioSessionId &&
               !audioPortAckReceived &&
               Time.realtimeSinceStartup < ackDeadline)
        {
            yield return null;
        }

        if (sessionId == audioSessionId)
        {
            if (!audioPortAckReceived)
            {
                acceptAudioPortConfig = false;
                LogWindow.Warn(
                    $"AUDIO_CONFIG timed out; using configured ports " +
                    $"{GetRemoteAudioPort()}/{GetMicrophoneUploadPort()}.");
            }

            StartDuplexAudioForSession(ip, sessionId);
        }

        cameraRequestCoroutine = null;
    }

    private void OnCameraStateChanged(int state)
    {
        _session.NoteCaptureState(state);
    }

    public void SetRecordVisual(string phase)
    {
        if (RecordBtn == null)
        {
            return;
        }

        if (phase == "starting")
        {
            RecordBtn.SetVisual(true, false, "Starting...");
        }
        else if (phase == "recording")
        {
            RecordBtn.SetVisual(true, true, "Recording");
        }
        else if (phase == "stopping")
        {
            RecordBtn.SetVisual(true, false, "Stopping...");
        }
        else
        {
            RecordBtn.SetVisual(false, false, "Record");
        }
    }

    public void SetRemoteVisionStatus(string text)
    {
        if (CameraStatusText != null)
        {
            CameraStatusText.text = text;
        }
    }

    public void SetRecordStatus(string text)
    {
        if (RecordStatusText == null)
        {
            return;
        }

        string clipped = text ?? "";
        if (clipped.Length > 18)
        {
            clipped = clipped.Substring(0, 17) + "…";
        }

        RecordStatusText.text = clipped;
    }

    private void EnsureRecordStatusText()
    {
        if (RecordStatusText != null || RecordBtn == null)
        {
            return;
        }

        Transform buttons = RecordBtn.transform.parent;
        Transform panel = buttons != null ? buttons.parent : null;
        if (panel == null)
        {
            return;
        }

        GameObject statusObject = new GameObject("RecordStatus", typeof(RectTransform));
        statusObject.transform.SetParent(panel, false);
        LayoutElement layout = statusObject.AddComponent<LayoutElement>();
        layout.ignoreLayout = true;

        Text text = statusObject.AddComponent<Text>();
        text.font = CameraStatusText != null && CameraStatusText.font != null
            ? CameraStatusText.font
            : Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = 14;
        text.color = Color.white;
        text.alignment = TextAnchor.MiddleRight;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.raycastTarget = false;

        RectTransform rect = statusObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-8f, -6f);
        rect.sizeDelta = new Vector2(120f, 36f);
        RecordStatusText = text;
    }


    private void OnDestroy()
    {
        StopCameraRequestCoroutine();
        StopDuplexAudio();
        TcpHandler.ReceiveFunctionEvent -= OnNetReceive;
        CameraHandle.RemoveStateListener(OnCameraStateChanged);
        if (tcpManager != null)
        {
            tcpManager.OnServerReceived -= OnServerReceived;
            tcpManager.OnClientReceived -= OnClientReceived;
        }
        if (operatorControlClient != null)
        {
            operatorControlClient.Connected -= OnControlClientConnected;
            operatorControlClient.Disconnected -= OnControlClientDisconnected;
            operatorControlClient.DataReceived -= OnClientDataReceived;
            operatorControlClient.Error -= OnControlClientError;
            operatorControlClient.Dispose();
            operatorControlClient = null;
        }
        if (_session.IsActive)
        {
            _session.RequestStop("object destroyed");
        }

        if (_surfaceProbe.IsActive)
        {
            _surfaceProbe.RequestStop("object destroyed");
        }

        if (_bufferDump.IsActive)
        {
            _bufferDump.RequestStop("object destroyed");
        }
    }

    private bool AnyRecordSessionActive()
    {
        return _session.IsActive || _surfaceProbe.IsActive || _bufferDump.IsActive;
    }

    private void OnRecordBtn(bool on)
    {
        bool anyActive = AnyRecordSessionActive();

        // CustomButton toggles via Invoke(!On) but only SetVisual/SetOn updates On.
        // If a probe left the button out of sync, a click may arrive as on=true while
        // a session is already active — treat that as Stop, not a no-op.
        if (anyActive)
        {
            // Show Stopping first; Vision Finish is synchronous and sets idle inside StopRecord.
            // Do not leave the button stuck on Stopping after a sync stop completes.
            SetRecordVisual("stopping");
            StopRecord();
            if (!AnyRecordSessionActive())
            {
                SetRecordVisual("idle");
            }

            return;
        }

        if (on)
        {
            OpenRecord();
        }
        else
        {
            // Idle + off click: keep button visual reset.
            SetRecordVisual("idle");
        }
    }

    private void OpenRecord()
    {
        if (!visionToggle.isOn && !trackingToggle.isOn)
        {
            LogWindow.Error("Please select at least one option: Tracking or Vision.");
            return;
        }

        // Vision data
        if (visionToggle.isOn)
        {
            bool withTracking = trackingToggle.isOn;
            RecordDialog.Show(() =>
            {
                StartRecord(RecordDialog.ResolutionWidth, RecordDialog.ResolutionHeight,
                    RecordDialog.Fps, RecordDialog.Bitrate, withTracking, RecordDialog.ExpImage,
                    RecordDialog.ExpOutputRaw, RecordDialog.ExpEnableEis,
                    RecordDialog.ExpEnableMctf, RecordDialog.ExpEnableMfnr,
                    RecordDialog.ExpEyeMode, RecordDialog.ExpTargetFps,
                    RecordDialog.ExpBufferWidth, RecordDialog.ExpBufferHeight,
                    RecordDialog.ExpPreviewWidth, RecordDialog.ExpPreviewHeight,
                    RecordDialog.ExpCrop, RecordDialog.ExpBuf);
            }, null);
        }
        else
        {
            if (trackingToggle.isOn)
            {
                _session.Begin(this, 2160, 810, 30, 0, false, true);
            }
        }
    }

    private void StartRecord(int width, int height, int fps, int bitrate,
        bool onTrackingData, int expImage = 0,
        bool expOutputRaw = false, bool expEnableEis = false, bool expEnableMctf = false,
        bool expEnableMfnr = true, string eyeMode = "LEFT", int targetFps = 30,
        int bufferWidth = 2048, int bufferHeight = 1536,
        int previewWidth = 2048, int previewHeight = 1536,
        bool expCrop = true, bool expBuf = false)
    {
        _useSurfaceProbe = false;
        _useBufferDump = false;
        if (expImage == 1 && expBuf)
        {
            _useBufferDump = true;
            int saveFps = Mathf.Clamp(targetFps == 60 ? 10 : 10, 1, 30);
            LogWindow.Info("Starting for4U CPU buffer dump (exp_buf=1) " + eyeMode +
                           " " + Robot.For4UBufferDumpSession.DefaultWidth + "x" +
                           Robot.For4UBufferDumpSession.DefaultHeight +
                           " target=" + (targetFps == 60 ? 60 : 30) +
                           " save=" + saveFps + " raw=1 (no Surface/crop)");
            SetRecordStatus("Buf…");
            _bufferDump.Begin(this, eyeMode,
                Robot.For4UBufferDumpSession.DefaultWidth,
                Robot.For4UBufferDumpSession.DefaultHeight,
                targetFps == 60 ? 60 : 30, saveFps, true);
            return;
        }

        if (expImage == 1)
        {
            _useSurfaceProbe = true;
            int surfaceFps = targetFps == 60 ? 60 : 30;
            int surfaceBitrate = bitrate > 0 ? bitrate : Robot.SurfaceEncodeProbe.DefaultBitrateBps;
            LogWindow.Info("Starting for4U Surface encode (exp_Image=1) " + eyeMode +
                           " encode=" + bufferWidth + "x" + bufferHeight +
                           " preview=" + previewWidth + "x" + previewHeight +
                           "@" + surfaceFps +
                           " raw=" + (expOutputRaw ? 1 : 0) +
                           " crop=" + (expCrop ? 1 : 0) +
                           " eis=" + (expEnableEis ? 1 : 0) +
                           " mctf=" + (expEnableMctf ? 1 : 0) +
                           " mfnr=" + (expEnableMfnr ? 1 : 0));
            SetRecordStatus("Surface…");
            _surfaceProbe.BeginCamera(this, eyeMode, bufferWidth, bufferHeight, surfaceFps,
                surfaceBitrate, onTrackingData, expOutputRaw, expEnableEis, expEnableMctf,
                expEnableMfnr, previewWidth, previewHeight, expCrop);
            return;
        }

        if (height > 0 && width > 0 && (width / (float)height) > 2.2f)
        {
            LogWindow.Info("Vision " + width + "x" + height +
                           " is very wide — vertical FOV will look heavily cropped vs 4:3. " +
                           "Prefer 2048x1536 or use exp_Image+Buf for PICO buffer dump.");
        }

        LogWindow.Info("Starting Vision StartRecord (exp_Image=0) " + width + "x" + height + "@" + fps);
        SetRecordStatus("Vision…");
        _session.Begin(this, width, height, fps, bitrate, true, onTrackingData);
    }

    public void UpdateCameraParamsNew()
    {
        ResolutionDialog.Show("Setting the resolution", (width, height) =>
        {
            string cameraIntrinsics = CameraHandle.GetCameraIntrinsics(width, height);
            string cameraExtrinsics = CameraHandle.GetCameraExtrinsics();

            string saveStr = "CameraExtrinsics:" + cameraExtrinsics + "\n";
            saveStr += "cameraIntrinsics:" + cameraIntrinsics;
            WriteLocalText(string.Format("cameraParams_{0}x{1}", width, height), saveStr);
            Toast.Show("Parameter saved successfully!");
        }, null);
    }

    private void WriteLocalText(string fileName, string content)
    {
        string parentPath = Application.persistentDataPath;
        string filePath = Path.Combine(parentPath, fileName + ".txt");
        using (StreamWriter writer = new StreamWriter(filePath, false))
        {
            writer.Write(content + "\n");
        }

        Debug.Log("The file has been successfully written: " + filePath);
    }

    private void StopRecord()
    {
        if (_bufferDump.IsActive)
        {
            _bufferDump.RequestStop("user");
            if (_bufferDump.NeedsForceStop())
            {
                _bufferDump.ForceReset("user_stop");
            }

            _useBufferDump = false;
            return;
        }

        if (_surfaceProbe.IsActive)
        {
            _surfaceProbe.RequestStop("user");
            // Coroutine gone but phase still active → hard reset so the button can recover.
            if (_surfaceProbe.NeedsForceStop())
            {
                _surfaceProbe.ForceReset("user_stop");
            }

            _useSurfaceProbe = false;
            return;
        }

        if (_session.IsActive)
        {
            _session.RequestStop("user");
            if (_session.NeedsForceStop())
            {
                _session.ForceReset("user_stop");
            }

            return;
        }

        _useSurfaceProbe = false;
        _useBufferDump = false;
        SetRecordVisual("idle");
    }

    private void StopSendImage()
    {
        CameraHandle.StopPreview();
        CameraHandle.CloseCamera();
        CameraSendToBtn.SetOn(false);
    }

    public void OnRemoteCameraBtn()
    {
        Toast.Show("Request  camera screen on PC!");
        //Request camera screen on PC
        TcpHandler.SendFunctionValue("requestCameraList", "");
    }

    private void Update()
    {
        RunControlConnectionWatchdog();

        if (_useBufferDump || _bufferDump.IsActive)
        {
            _bufferDump.Pump();
            return;
        }

        if (_useSurfaceProbe || _surfaceProbe.IsActive)
        {
            _surfaceProbe.Pump();
            return;
        }

        _session.Pump();
        if (_recordTrackingData)
        {
            _session.WriteTrackingSample();
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (pauseStatus)
        {
            resumeDuplexAudioAfterPause = listenBtn != null &&
                                          listenBtn.On &&
                                          !string.IsNullOrEmpty(activeAudioHost);
            StopDuplexAudio(false);
            controlReconnectSuppressed = true;
            if (operatorControlClient != null && operatorControlClient.IsRunning)
            {
                operatorControlClient.DisconnectSilently();
            }
            controlClientConnected = false;

            if (_bufferDump.IsActive)
            {
                _bufferDump.RequestStop("application paused");
            }

            if (_surfaceProbe.IsActive)
            {
                _surfaceProbe.RequestStop("application paused");
            }

            if (_session.IsActive)
            {
                _session.RequestStop("application paused");
            }
        }

        if (!pauseStatus &&
            resumeDuplexAudioAfterPause &&
            listenBtn != null &&
            listenBtn.On &&
            !string.IsNullOrEmpty(activeAudioHost))
        {
            RequestCameraStream(activeAudioHost);
            resumeDuplexAudioAfterPause = false;
        }
    }
}