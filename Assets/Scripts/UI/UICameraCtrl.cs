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
    private bool _useApiCapture;

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
        if (RecordStatusText != null)
        {
            RecordStatusText.text = text;
        }
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
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;

        RectTransform rect = statusObject.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(1f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(1f, 1f);
        rect.anchoredPosition = new Vector2(-8f, -6f);
        rect.sizeDelta = new Vector2(150f, 20f);
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

        if (_apiSession.IsActive)
        {
            _apiSession.RequestStop("object destroyed");
        }
    }

    private void OnRecordBtn(bool on)
    {
        if (on)
        {
            if (_session.IsActive || _apiSession.IsActive)
            {
                return;
            }

            OpenRecord();
        }
        else
        {
            StopRecord();
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
                    RecordDialog.ExpEnableMctf, RecordDialog.ExpEnableMfnr);
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
        bool expEnableMfnr = true)
    {
        _useApiCapture = expImage == 1;
        if (_useApiCapture)
        {
            // Width/Height stay in the dialog for path A only. Path B uses the for4U buffer size.
            _apiSession.Begin(this, fps, onTrackingData,
                expOutputRaw, expEnableEis, expEnableMctf, expEnableMfnr);
            return;
        }

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
        if (_apiSession.IsActive)
        {
            _apiSession.RequestStop("user");
            return;
        }

        _session.RequestStop("user");
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