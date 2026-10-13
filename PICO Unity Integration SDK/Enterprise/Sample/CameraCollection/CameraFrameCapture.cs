#if !PICO_OPENXR_SDK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;
using Debug = UnityEngine.Debug;

namespace Pico.CameraCapture
{
    /// <summary>
    /// PICO 4 Ultra Enterprise: unrendered for4U frames + optional JPEG session + poses.
    /// Preview is always from the for4U buffer (never game-view / UI screenshot).
    /// </summary>
    public class CameraFrameCapture : MonoBehaviour
    {
        const string Tag = "[CameraCapture]";

        [Header("Optional UI")]
        public Text statusText;
        public Text fpsText;
        public Text pathText;
        public RawImage previewImage;
        public Renderer previewRenderer;

        [Header("Runtime overrides (0 = use CaptureSettings.json)")]
        public int overrideTargetFps;
        public int overrideSaveFps;

        CaptureSettings _settings;
        byte[] _imgByte;
        GCHandle _imgHandle;
        CameraCollectionWriter _writer;
        PoseRecorder _poseRecorder;
        PXRCaptureRenderMode _mode = PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT;

        Texture2D _fullTex;
        Texture2D _previewTex;

        bool _bound;
        bool _cameraOpen;
        bool _capturing;
        bool _recording;
        bool _reopen;
        bool _prevPrimary;
        bool _prevSecondary;

        int _callbackCount;
        int _savedIndex;
        float _uiDelta;
        int _uiCount;
        uint _lastDatasize;
        int _lastSensorStatus;

        long _lastSavedMonoNs;
        long _saveIntervalNs;

        readonly Stopwatch _mono = Stopwatch.StartNew();
        readonly object _frameLock = new object();

        byte[] _pendingRgba;
        int _pendingLen;
        ulong _pendingTs;
        long _pendingMonoNs;
        string _pendingWall;
        uint _pendingDatasize;
        bool _pendingReady;
        bool _pendingSave;
        bool _pendingHasCamPose;
        UnityEngine.Pose _pendingCamPose;
        int _pendingSensorStatus;

        public bool IsBound => _bound;
        public bool IsCapturing => _capturing;
        public bool IsRecording => _recording;
        public string SessionDirectory => _writer?.SessionDirectory;
        public CaptureSettings Settings => _settings;

        public event Action<string> StatusChanged;
        public event Action RecordingStateChanged;

        void Awake()
        {
            _settings = CaptureSettings.LoadOrDefault();
            if (overrideTargetFps > 0) _settings.targetFps = overrideTargetFps;
            if (overrideSaveFps > 0) _settings.saveFps = overrideSaveFps;

            ApplyEyeMode(_settings.eyeMode);
            ApplySaveInterval();

            int bytes = _settings.width * _settings.height * 4;
            _imgByte = new byte[bytes];
            _pendingRgba = new byte[bytes];
            _imgHandle = GCHandle.Alloc(_imgByte, GCHandleType.Pinned);

            _poseRecorder = GetComponent<PoseRecorder>();
            if (_poseRecorder == null)
            {
                _poseRecorder = gameObject.AddComponent<PoseRecorder>();
            }

            LogStatus(
                $"ready package={_settings.packageName} {_settings.width}x{_settings.height} " +
                $"eye={_settings.eyeMode} raw={_settings.outputCameraRawData} save={_settings.saveFormat} " +
                $"targetFps={_settings.targetFps} saveFps={_settings.saveFps} poseFps={_settings.poseFps}");

            if (Application.identifier != _settings.packageName)
            {
                Debug.LogWarning(
                    $"{Tag} Application.identifier='{Application.identifier}' != CaptureSettings.packageName='{_settings.packageName}'. " +
                    "Enterprise camera auth is SN+package bound; fix Player Settings before expecting frames.");
            }

            // libtrackingclient.pxr / enterprise natives exist on headset only — never call in Editor.
            if (!IsEnterpriseNativeAvailable())
            {
                _bound = false;
                LogStatus("EDITOR MODE: UI only. Enterprise for4U needs APK on PICO device (no libtrackingclient.pxr in Editor).");
                InvokeRepeating(nameof(HeartbeatLog), 2.0f, 2.0f);
                return;
            }

            PXR_Manager.EnableVideoSeeThrough = true;
            PXR_Enterprise.UseGlobalPose(true);
            PXR_Enterprise.InitEnterpriseService();
            PXR_Enterprise.BindEnterpriseService(ok =>
            {
                _bound = ok;
                LogStatus($"BindEnterpriseService={ok}");
            });

            Invoke(nameof(StartPreviewPipeline), 1.0f);
            InvokeRepeating(nameof(HeartbeatLog), 2.0f, 2.0f);

            if (_settings.autoStartOnAwake)
            {
                Invoke(nameof(StartRecording), 2.0f);
            }
        }

        static bool IsEnterpriseNativeAvailable()
        {
#if UNITY_EDITOR
            return false;
#else
            return Application.platform == RuntimePlatform.Android;
#endif
        }

        void HeartbeatLog()
        {
            Debug.Log(
                $"{Tag} heartbeat bound={_bound} capturing={_capturing} recording={_recording} " +
                $"cb={_callbackCount} saved={_savedIndex} datasize={_lastDatasize} " +
                $"eye={_settings?.eyeMode} pkg={Application.identifier}");

            if (_capturing && _lastDatasize == 0 && _callbackCount > 0)
            {
                LogStatus($"datasize=0 · {_settings.width}x{_settings.height} eye={_settings.eyeMode} — check SN+package auth");
            }
        }

        void OnDestroy()
        {
            StopRecording();
            StopCapturePipeline();
            if (_imgHandle.IsAllocated)
            {
                _imgHandle.Free();
            }
            if (_fullTex != null) Destroy(_fullTex);
            if (_previewTex != null) Destroy(_previewTex);
        }

        void OnApplicationPause(bool pauseStatus)
        {
            if (!_capturing) return;

            if (pauseStatus)
            {
                PXR_Enterprise.CloseCamerafor4U();
                _cameraOpen = false;
            }
            else
            {
                OpenCameraInternal(ret =>
                {
                    _reopen = ret;
                    LogStatus($"Resume OpenCameraAsync={ret}");
                });
            }
        }

        void Update()
        {
            if (_reopen)
            {
                _reopen = false;
                BeginGetImageData();
            }

            PollControllerButtons();
            ProcessPendingFrame();

            _uiDelta += Time.deltaTime;
            if (_uiDelta >= 1f)
            {
                if (fpsText != null)
                {
                    float fps = _uiCount > 0 ? _uiCount / _uiDelta : 0f;
                    string rec = _recording ? "REC" : "IDLE";
                    fpsText.text =
                        $"{rec} cb={fps:0.0}Hz saved={_savedIndex} drop={_writer?.DroppedCount ?? 0} " +
                        $"ds={_lastDatasize} eye={_settings.eyeMode} {_settings.width}x{_settings.height}";
                }
                _uiCount = 0;
                _uiDelta = 0f;
            }
        }

        void PollControllerButtons()
        {
            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!right.isValid) return;

            right.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary);
            right.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary);

            if (primary && !_prevPrimary)
            {
                if (_recording) StopRecording();
                else StartRecording();
            }

            if (secondary && !_prevSecondary)
            {
                StopRecording();
            }

            _prevPrimary = primary;
            _prevSecondary = secondary;
        }

        public void StartPreviewPipeline()
        {
            StartCapturePipeline(record: false);
        }

        public void StartCapturePipeline()
        {
            StartCapturePipeline(record: _settings.autoStartOnAwake);
        }

        void StartCapturePipeline(bool record)
        {
            if (!_bound)
            {
                LogStatus("Enterprise service not bound yet; retrying bind then start.");
                PXR_Enterprise.BindEnterpriseService(ok =>
                {
                    _bound = ok;
                    if (ok) StartCapturePipeline(record);
                    else LogStatus("Bind failed; cannot start capture.");
                });
                return;
            }

            if (_capturing && _cameraOpen)
            {
                if (record && !_recording)
                {
                    BeginRecordingWriter();
                }
                return;
            }

            ConfigureCamera();
            OpenCameraInternal(ret =>
            {
                _cameraOpen = ret;
                LogStatus($"OpenCameraAsyncfor4U={ret} eye={_settings.eyeMode} {_settings.width}x{_settings.height}");
                if (ret)
                {
                    BeginGetImageData();
                    if (record) BeginRecordingWriter();
                }
            });
        }

        public void StartRecording()
        {
            if (_recording)
            {
                LogStatus("Already recording.");
                return;
            }

            if (!_capturing || !_cameraOpen)
            {
                StartCapturePipeline(record: true);
                return;
            }

            BeginRecordingWriter();
        }

        public void StopRecording()
        {
            if (!_recording && _writer == null) return;

            _recording = false;
            if (_poseRecorder != null && _poseRecorder.IsActive)
            {
                _writer?.SetPoseSampleCount(_poseRecorder.WrittenCount);
                _poseRecorder.End();
            }

            string dir = _writer?.SessionDirectory;
            StopWriterOnly();
            if (!string.IsNullOrEmpty(dir))
            {
                UpdatePathText(dir + "  (COMPLETE if frames/poses>0)");
                LogStatus($"Recording stopped. Path:\n{dir}");
            }
            else
            {
                LogStatus("Recording stopped.");
            }
            RecordingStateChanged?.Invoke();
        }

        public void StopCapturePipeline()
        {
            StopRecording();
            _capturing = false;
            if (_cameraOpen)
            {
                PXR_Enterprise.CloseCamerafor4U();
                _cameraOpen = false;
            }
            LogStatus("Camera stream stopped.");
        }

        void BeginRecordingWriter()
        {
            StopWriterOnly();
            _writer = new CameraCollectionWriter(_settings);
            _savedIndex = 0;
            _lastSavedMonoNs = 0;
            ApplySaveInterval();
            _recording = true;
            _poseRecorder?.Begin(_writer.SessionDirectory, _settings);
            UpdatePathText(_writer.SessionDirectory);
            LogStatus($"Recording → {_writer.SessionDirectory}");
            RecordingStateChanged?.Invoke();
        }

        void StopWriterOnly()
        {
            if (_poseRecorder != null && _poseRecorder.IsActive)
            {
                _writer?.SetPoseSampleCount(_poseRecorder.WrittenCount);
                _poseRecorder.End();
            }

            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
            _recording = false;
        }

        void ConfigureCamera()
        {
            PXR_Enterprise.Configurefor4U(false, _settings.targetFps);

            var rawCfg = new Dictionary<string, string>
            {
                {
                    PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA,
                    _settings.outputCameraRawData ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                }
            };
            PXR_Enterprise.Configurefor4U(rawCfg);
            LogStatus($"Configure targetFps={_settings.targetFps} raw={_settings.outputCameraRawData} (for4U≠VST)");
        }

        void OpenCameraInternal(Action<bool> callback)
        {
            bool mctf = _settings.enableMctf && _settings.targetFps <= 30;
            var openParams = new Dictionary<string, string>
            {
                { PXRCapture.KEY_MCTF, mctf ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE },
                { PXRCapture.KEY_EIS, _settings.enableEis ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE },
                { PXRCapture.KEY_MFNR, _settings.enableMfnr ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE }
            };

            PXR_Enterprise.OpenCameraAsyncfor4U(callback, openParams);
        }

        void BeginGetImageData()
        {
            IntPtr data = _imgHandle.AddrOfPinnedObject();
            PXR_Enterprise.SetCameraFrameBufferfor4U(_settings.width, _settings.height, ref data, OnImageAvailable);

            bool ok = PXR_Enterprise.StartGetImageDatafor4U(_mode, _settings.width, _settings.height);
            _capturing = ok;
            LogStatus($"StartGetImageDatafor4U mode={_mode} ret={ok}");
        }

        void OnImageAvailable(Frame frame)
        {
            _callbackCount++;
            _uiCount++;
            _lastDatasize = frame.datasize;
            _lastSensorStatus = frame.status;
            _writer?.NotifyCallback(frame.datasize);

            long monoNs = (long)(_mono.Elapsed.TotalMilliseconds * 1_000_000.0);
            string wall = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            if (frame.datasize == 0)
            {
                if (_callbackCount <= 5 || _callbackCount % 60 == 0)
                {
                    Debug.LogWarning($"{Tag} datasize=0 timestamp={frame.timestamp} (check SN+package camera auth)");
                }
                return;
            }

            int expected = _settings.width * _settings.height * 4;
            int copyLen = expected;
            if (frame.datasize > 0 && frame.datasize < expected)
            {
                copyLen = (int)frame.datasize;
            }

            bool wantSave = _recording && _writer != null && ShouldSave(monoNs);

            lock (_frameLock)
            {
                Buffer.BlockCopy(_imgByte, 0, _pendingRgba, 0, copyLen);
                _pendingLen = copyLen;
                _pendingTs = frame.timestamp;
                _pendingMonoNs = monoNs;
                _pendingWall = wall;
                _pendingDatasize = frame.datasize;
                _pendingSave = wantSave;
                _pendingCamPose = frame.pose;
                _pendingHasCamPose = true;
                _pendingSensorStatus = frame.status;
                _pendingReady = true;
            }

            if (wantSave)
            {
                _lastSavedMonoNs = monoNs;
            }
        }

        void ProcessPendingFrame()
        {
            byte[] rgba = null;
            int len = 0;
            ulong ts = 0;
            long monoNs = 0;
            string wall = null;
            uint datasize = 0;
            bool save = false;
            bool hasCamPose = false;
            UnityEngine.Pose camPose = default;
            int sensorStatus = 0;

            lock (_frameLock)
            {
                if (!_pendingReady) return;
                _pendingReady = false;
                len = _pendingLen;
                rgba = new byte[len];
                Buffer.BlockCopy(_pendingRgba, 0, rgba, 0, len);
                ts = _pendingTs;
                monoNs = _pendingMonoNs;
                wall = _pendingWall;
                datasize = _pendingDatasize;
                save = _pendingSave;
                hasCamPose = _pendingHasCamPose;
                camPose = _pendingCamPose;
                sensorStatus = _pendingSensorStatus;
                _pendingSave = false;
            }

            // Preview exclusively from for4U buffer copy — not Camera.main / ScreenCapture.
            UpdatePreview(rgba, len);

            if (!save || _writer == null || !_recording) return;

            byte[] payload;
            string container;
            int expected = _settings.width * _settings.height * 4;

            if (_settings.SaveAsJpeg)
            {
                EnsureFullTexture();
                if (len < expected)
                {
                    var padded = new byte[expected];
                    Buffer.BlockCopy(rgba, 0, padded, 0, len);
                    _fullTex.LoadRawTextureData(padded);
                }
                else
                {
                    _fullTex.LoadRawTextureData(rgba);
                }
                _fullTex.Apply(false, false);
                int q = Mathf.Clamp(_settings.jpegQuality, 1, 100);
                payload = _fullTex.EncodeToJPG(q);
                container = "jpeg";
            }
            else
            {
                payload = rgba;
                container = "rgba";
            }

            if (payload == null || payload.Length == 0) return;

            var job = new CameraCollectionWriter.FrameJob
            {
                Index = _savedIndex,
                FrameTimestampNs = ts,
                MonoNs = monoNs,
                WallTimeUtc = wall,
                Width = _settings.width,
                Height = _settings.height,
                Payload = payload,
                DataSize = datasize,
                Container = container,
                Saved = false,
                HasCameraPose = hasCamPose,
                CamPx = camPose.position.x,
                CamPy = camPose.position.y,
                CamPz = camPose.position.z,
                CamQx = camPose.rotation.x,
                CamQy = camPose.rotation.y,
                CamQz = camPose.rotation.z,
                CamQw = camPose.rotation.w,
                SensorStatus = sensorStatus
            };

            if (_writer.TryEnqueue(job))
            {
                _savedIndex++;
                if (_savedIndex <= 3 || _savedIndex % 30 == 0)
                {
                    Debug.Log($"{Tag} saved#{_savedIndex - 1} ts={ts} ds={datasize} bytes={payload.Length} {container}");
                }
            }
        }

        void UpdatePreview(byte[] rgba, int len)
        {
            int srcW = _settings.width;
            int srcH = _settings.height;
            int expected = srcW * srcH * 4;
            if (rgba == null || len < 4) return;

            int maxSide = Mathf.Max(64, _settings.previewMaxSide);
            float scale = Mathf.Min(1f, maxSide / (float)Mathf.Max(srcW, srcH));
            int dstW = Mathf.Max(1, Mathf.RoundToInt(srcW * scale));
            int dstH = Mathf.Max(1, Mathf.RoundToInt(srcH * scale));

            if (_previewTex == null || _previewTex.width != dstW || _previewTex.height != dstH)
            {
                if (_previewTex != null) Destroy(_previewTex);
                _previewTex = new Texture2D(dstW, dstH, TextureFormat.RGBA32, false);
                _previewTex.wrapMode = TextureWrapMode.Clamp;
                _previewTex.filterMode = FilterMode.Bilinear;
                if (previewImage != null) previewImage.texture = _previewTex;
                if (previewRenderer != null)
                {
                    if (previewRenderer.material == null)
                    {
                        var sh = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
                        previewRenderer.material = new Material(sh) { name = "PreviewMaterial" };
                    }
                    previewRenderer.material.mainTexture = _previewTex;
                }
            }

            var pixels = new Color32[dstW * dstH];
            int safeLen = Mathf.Min(len, expected);
            for (int y = 0; y < dstH; y++)
            {
                int sy = (y * srcH) / dstH;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = (x * srcW) / dstW;
                    int si = (sy * srcW + sx) * 4;
                    if (si + 2 >= safeLen)
                    {
                        pixels[y * dstW + x] = Color.black;
                        continue;
                    }
                    pixels[y * dstW + x] = new Color32(rgba[si], rgba[si + 1], rgba[si + 2], 255);
                }
            }

            _previewTex.SetPixels32(pixels);
            _previewTex.Apply(false, false);
        }

        void EnsureFullTexture()
        {
            if (_fullTex != null &&
                _fullTex.width == _settings.width &&
                _fullTex.height == _settings.height)
            {
                return;
            }

            if (_fullTex != null) Destroy(_fullTex);
            _fullTex = new Texture2D(_settings.width, _settings.height, TextureFormat.RGBA32, false);
        }

        bool ShouldSave(long monoNs)
        {
            if (_saveIntervalNs <= 0) return true;
            if (_lastSavedMonoNs == 0) return true;
            return monoNs - _lastSavedMonoNs >= _saveIntervalNs;
        }

        void ApplySaveInterval()
        {
            if (_settings.saveFps <= 0)
            {
                _saveIntervalNs = 0;
            }
            else
            {
                _saveIntervalNs = (long)(1_000_000_000.0 / _settings.saveFps);
            }
        }

        void ApplyEyeMode(string mode)
        {
            switch ((mode ?? "LEFT").Trim().ToUpperInvariant())
            {
                case "RIGHT":
                    _mode = PXRCaptureRenderMode.PXRCapture_RenderMode_RIGHT;
                    break;
                case "3D":
                    _mode = PXRCaptureRenderMode.PXRCapture_RenderMode_3D;
                    break;
                case "INTERLACE":
                    _mode = PXRCaptureRenderMode.PXRCapture_RenderMode_Interlace;
                    break;
                default:
                    _mode = PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT;
                    break;
            }
        }

        void UpdatePathText(string path)
        {
            if (pathText != null)
            {
                pathText.text = path ?? "";
            }
        }

        void LogStatus(string msg)
        {
            Debug.Log($"{Tag} {msg}");
            if (statusText != null)
            {
                statusText.text = msg;
            }
            StatusChanged?.Invoke(msg);
        }
    }
}
#endif
