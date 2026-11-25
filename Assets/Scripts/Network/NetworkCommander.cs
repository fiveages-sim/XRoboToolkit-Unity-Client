using System;
using Robot;
using Robot.Network;
using Robot.V2.Network;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif
using XRoboToolkit.Network;

namespace Network
{
    public static class NetworkCommand
    {
        public const string OPEN_CAMERA = "OPEN_CAMERA";
        public const string AUDIO_CONFIG = "AUDIO_CONFIG";
        public const string AUDIO_SESSION = "AUDIO_SESSION";
        public const string OPEN_CAMERA_ACK = "OPEN_CAMERA_ACK";
        public const string RECORD_STATUS = "RECORD_STATUS";
        public const string CLOSE_CAMERA = "CLOSE_CAMERA";
        public const string PING = "PING";
        public const string PONG = "PONG";
    }

    public class NetworkCommander : MonoBehaviour
    {
        private NetworkDataProcessor processor;
        private TcpManager tcpManager;

        private string logTag = "NetworkCommander";

        public static NetworkCommander Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        public NetworkDataProcessor Processor
        {
            get { return processor; }
        }

        private void Start()
        {
            InitializeNetworkProcessor();
        }

        private void InitializeNetworkProcessor()
        {
            processor = new NetworkDataProcessor();

            // Register example command handlers
            processor.RegisterCommandHandler(NetworkCommand.OPEN_CAMERA, new OpenCameraHandler());
            processor.RegisterCommandHandler(NetworkCommand.CLOSE_CAMERA, new CloseCameraHandler());

            Debug.Log("Network data processor initialized with " + processor.GetRegisteredCommands().Length +
                      " handlers");
        }

        void OnDestroy()
        {
            // Clean up
            processor?.ClearAllHandlers();
        }

        #region Send Data

        public void SendCommand(string command, byte[] data = null)
        {
            var protocol = new NetworkDataProtocol(command, data);
            var serializedData = NetworkDataProtocolSerializer.Serialize(protocol);
            // Send the packet using TcpManager
            TcpManager.Instance.ClientSend(serializedData);
        }

        public void OpenCamera(byte[] data)
        {
            LogWindow.Warn("Sending OPEN_CAMERA command.");
            SendCommand(NetworkCommand.OPEN_CAMERA, data);
        }

        public void CloseCamera()
        {
            LogWindow.Warn("Sending CLOSE_CAMERA command.");
            SendCommand(NetworkCommand.CLOSE_CAMERA, new byte[0]);
        }

        #endregion

        private class OpenCameraHandler : ICommandHandler
        {
            public void HandleCommand(byte[] data)
            {
                Debug.Log("Handling OPEN_CAMERA command with data: " + BitConverter.ToString(data));
                // Add logic to open camera

                var cameraConfig = CameraRequestSerializer.Deserialize(data);
                Utils.WriteLog("OpenCameraHandler", $"Received camera config: {cameraConfig}");
                LogWindow.Warn($"Handling OPEN_CAMERA: {cameraConfig.ToString()}");

                // The stream only works for the VR headset (PICO4U/4UE VST).
                if (cameraConfig.camera.Equals("VR"))
                {
                    StartVrCameraWithPermissionGate(cameraConfig);
                }
            }

            private static void StartVrCameraWithPermissionGate(CameraRequestData cameraConfig)
            {
                CameraHandle.SetLogContext(
                    $"rv-{cameraConfig.width}x{cameraConfig.height}@{cameraConfig.fps}");

                if (!Utils.IsPico4U())
                {
                    LogWindow.Error(
                        "OPEN_CAMERA VR requires PICO 4 Ultra Enterprise with VST camera auth.");
                    Toast.Show("Need PICO4UE + enterprise camera permission");
                    CrashProbe.Breadcrumb("open_camera.not_pico4u", "", LogType.Error);
                    return;
                }

#if UNITY_ANDROID && !UNITY_EDITOR
                // Official main prompts CAMERA earlier via OpenVSTCamera on enable.
                // Enterprise Main disabled OpenVSTCamera (OOM guard), so H1 may never
                // have been granted runtime CAMERA before Listen → native -99
                // "Miss Permission" from PxrCaptureServiceClient.
                if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                {
                    LogWindow.Warn("CAMERA runtime permission missing; requesting before VST open.");
                    CrashProbe.Breadcrumb("open_camera.request_camera_permission");
                    PXR_Enterprise.RequestUserPermission(
                        _ => StartVrCameraPreview(cameraConfig),
                        denied =>
                        {
                            LogWindow.Error(
                                "CAMERA permission denied; OPEN_CAMERA aborted. " + denied);
                            Toast.Show("Camera permission denied on source headset");
                            CrashProbe.Breadcrumb(
                                "open_camera.camera_permission_denied",
                                denied,
                                LogType.Error);
                        });
                    return;
                }
#endif

                StartVrCameraPreview(cameraConfig);
            }

            private static void StartVrCameraPreview(CameraRequestData cameraConfig)
            {
                CrashProbe.Breadcrumb("open_camera.vr_start", cameraConfig.ToString());
                LogWindow.Warn(
                    "Starting VST preview. If CaptureError -99 / Miss Permission follows, " +
                    "PICO enterprise camera auth (package+SN+signing) is missing — " +
                    "same as official README VST special approval, not a resolution bug.");
                CameraHandle.StartCameraPreview(
                    cameraConfig.width,
                    cameraConfig.height,
                    cameraConfig.fps,
                    cameraConfig.bitrate,
                    cameraConfig.enableMvHevc,
                    cameraConfig.renderMode,
                    () =>
                    {
                        CrashProbe.Breadcrumb(
                            "open_camera.vr_preview_ready",
                            $"{cameraConfig.ip}:{cameraConfig.port}");
                        CameraHandle.StartSendImage(cameraConfig.ip, cameraConfig.port);
                    });
            }
        }

        private class CloseCameraHandler : ICommandHandler
        {
            public void HandleCommand(byte[] data)
            {
                Debug.Log("Handling CLOSE_CAMERA command with data: " + BitConverter.ToString(data));
                LogWindow.Warn("Handling CLOSE_CAMERA command");
                // Stop preview then fully release the capture session. StopPreview
                // alone can leave enterprise VST (esp. 2160×810) stuck open — the
                // official "cannot close camera" symptom on the source headset.
                CameraHandle.StopPreview();
                try
                {
                    CameraHandle.CloseCamera();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("CLOSE_CAMERA CloseCamera failed: " + exception.Message);
                }
            }
        }
    }
}