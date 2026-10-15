using System;
using System.Collections;
using System.IO;
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
    private readonly Robot.ApiFrameSession _apiSession = new Robot.ApiFrameSession();
    private readonly Robot.SurfaceEncodeProbe _surfaceProbe = new Robot.SurfaceEncodeProbe();
    private bool _useApiCapture;
    private bool _useSurfaceProbe;

    private bool _recordTrackingData
    {
        get
        {
            return _useApiCapture ? _apiSession.TrackingWriteEnabled : _session.TrackingWriteEnabled;
        }
        set
        {
            if (!_useApiCapture)
            {
                _session.TrackingWriteEnabled = value;
            }
        }
    }

    private void Awake()
    {
        RecordBtn.OnChange += OnRecordBtn;
        TcpHandler.ReceiveFunctionEvent += OnNetReceive;
        CameraHandle.AddStateListener(OnCameraStateChanged);
        UnityMainThreadDispatcher.Instance();
        EnsureRecordStatusText();
        SetRemoteVisionStatus("Idle");
        SetRecordStatus("Idle");

        // Refactoring
        listenBtn.OnChange += OnListenCameraBtn;

        // Bind event
        tcpManager.OnServerReceived += OnServerReceived;
        tcpManager.OnClientReceived += OnClientReceived;
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
            CameraSendInputDialog.Show(RequestCameraStream);
        }
        else
        {
            RemoteCameraWindowObj.SetActive(false);
            SetRemoteVisionStatus("Idle");
        }

        // Update button
        listenBtn.SetOn(on);
    }

    public void RequestCameraStream(string ip)
    {
        StartCoroutine(RequestCameraStreamCoroutine(ip));
    }

    IEnumerator RequestCameraStreamCoroutine(string ip)
    {
        if (TcpServer.Status == ServerStatus.Started)
        {
            // Close TcpServer first
            TcpManager.Instance.StopServer();
        }

        yield return new WaitForSeconds(0.1f);

        // Get camera parameters
        var camPara = VideoSourceConfigManager.Instance.CameraParameters;

        SetRemoteVisionStatus("Listening");
        // Start listening to the camera
        RemoteCameraWindowObj.SetActive(true);
        RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>().StartListen(camPara.width, camPara.height, camPara.fps,
            camPara.bitrate, streamingPort);

        yield return new WaitForSeconds(0.2f);

        // Reset LERE
        setLere.ResetCanvases();

        yield return new WaitForSeconds(0.1f);

        if (TcpClient.Status != ClientStatus.Connected)
        {
            // initialize TcpClient, server IP is the video source IP
            TcpManager.Instance.StartClient(ip);
        }

        yield return new WaitForSeconds(0.5f);

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

        // Utils.WriteLog(logTag, $"send camera config: {customConfig}");
        var data = CameraRequestSerializer.Serialize(customConfig);

        // Use network commander
        NetworkCommander.Instance.OpenCamera(data);
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
        TcpHandler.ReceiveFunctionEvent -= OnNetReceive;
        CameraHandle.RemoveStateListener(OnCameraStateChanged);
        if (_session.IsActive)
        {
            _session.RequestStop("object destroyed");
        }

        if (_surfaceProbe.IsActive)
        {
            _surfaceProbe.RequestStop("object destroyed");
        }

        if (_apiSession.IsActive)
        {
            _apiSession.RequestStop("object destroyed");
        }
    }

    private bool AnyRecordSessionActive()
    {
        return _session.IsActive || _apiSession.IsActive || _surfaceProbe.IsActive;
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
                    RecordDialog.ExpPreviewWidth, RecordDialog.ExpPreviewHeight);
            }, null);
        }
        else
        {
            if (trackingToggle.isOn)
            {
                _useApiCapture = false;
                _session.Begin(this, 2160, 810, 30, 0, false, true);
            }
        }
    }

    private void StartRecord(int width, int height, int fps, int bitrate,
        bool onTrackingData, int expImage = 0,
        bool expOutputRaw = false, bool expEnableEis = false, bool expEnableMctf = false,
        bool expEnableMfnr = true, string eyeMode = "LEFT", int targetFps = 30,
        int bufferWidth = 2048, int bufferHeight = 1536,
        int previewWidth = 2048, int previewHeight = 1536)
    {
        // Optional B0 pattern probe only. B1/production Surface is exp_Image=1.
        int probeMode = Robot.SurfaceEncodeProbe.ResolveMode();
        if (probeMode == Robot.SurfaceEncodeProbe.ModePatternB0)
        {
            _useSurfaceProbe = true;
            _useApiCapture = false;
            LogWindow.Warn("SurfaceEncodeProbe B0 pattern — skipping Vision/for4U.");
            SetRecordStatus("B0…");
            _surfaceProbe.BeginPattern(this, fps, onTrackingData);
            return;
        }

        _useSurfaceProbe = false;
        if (expImage == 1)
        {
            _useApiCapture = false;
            _useSurfaceProbe = true;
            int surfaceFps = targetFps == 60 ? 60 : 30;
            int surfaceBitrate = bitrate > 0 ? bitrate : Robot.SurfaceEncodeProbe.DefaultBitrateBps;
            LogWindow.Info("Starting for4U Surface encode (exp_Image=1) " + eyeMode +
                           " encode=" + bufferWidth + "x" + bufferHeight +
                           " preview=" + previewWidth + "x" + previewHeight +
                           "@" + surfaceFps +
                           " raw=" + (expOutputRaw ? 1 : 0) +
                           " eis=" + (expEnableEis ? 1 : 0) +
                           " mctf=" + (expEnableMctf ? 1 : 0) +
                           " mfnr=" + (expEnableMfnr ? 1 : 0));
            SetRecordStatus("Surface…");
            _surfaceProbe.BeginCamera(this, eyeMode, bufferWidth, bufferHeight, surfaceFps,
                surfaceBitrate, onTrackingData, expOutputRaw, expEnableEis, expEnableMctf,
                expEnableMfnr, previewWidth, previewHeight);
            return;
        }

        _useApiCapture = false;
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

        if (_apiSession.IsActive)
        {
            _apiSession.RequestStop("user");
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
        if (_useSurfaceProbe || _surfaceProbe.IsActive)
        {
            _surfaceProbe.Pump();
            return;
        }

        if (_useApiCapture || _apiSession.IsActive)
        {
            _apiSession.Pump();
            if (_apiSession.TrackingWriteEnabled)
            {
                _apiSession.WriteTrackingSample();
            }

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
        if (!pauseStatus)
        {
            return;
        }

        if (_surfaceProbe.IsActive)
        {
            _surfaceProbe.RequestStop("application paused");
        }

        if (_apiSession.IsActive)
        {
            _apiSession.RequestStop("application paused");
        }

        if (_session.IsActive)
        {
            _session.RequestStop("application paused");
        }
    }
}