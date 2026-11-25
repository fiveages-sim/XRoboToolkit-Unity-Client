using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Closest-to-PICO dump: SetCameraFrameBufferfor4U + StartGetImageDatafor4U (no StartPreview,
    /// no GLES eye-crop, no capturelib Vision). Writes JPEG frames under CameraCollection/session_*.
    /// </summary>
    public sealed class For4UBufferDumpSession
    {
        public const string EncodePathId = "for4u-cpu-buffer";
        public const int DefaultWidth = 2048;
        public const int DefaultHeight = 1536;
        public const int DefaultSaveFps = 10;
        public const float StartTimeoutSeconds = 20f;

        private UICameraCtrl _host;
        private Coroutine _routine;
        private RecordingSessionPhase _phase = RecordingSessionPhase.Idle;
        private bool _finalized;
        private bool _stopRequested;
        private bool _cameraOpen;
        private bool _capturing;

        private string _sessionId;
        private string _sessionDir;
        private string _framesDir;
        private string _eyeMode = "LEFT";
        private int _width = DefaultWidth;
        private int _height = DefaultHeight;
        private int _targetFps = 30;
        private int _saveFps = DefaultSaveFps;
        private bool _outputRaw = true;
        private int _jpegQuality = 90;

        private byte[] _imgByte;
        private GCHandle _imgHandle;
        private readonly object _frameLock = new object();
        private byte[] _pending;
        private int _pendingLen;
        private ulong _pendingTs;
        private long _pendingMonoNs;
        private uint _pendingDatasize;
        private bool _pendingReady;
        private bool _pendingSave;

        private int _callbackCount;
        private int _savedCount;
        private int _zeroDataCount;
        private long _lastSavedMonoNs;
        private long _saveIntervalNs;
        private JsonData _meta;
        private CollectionSessionWriter _sessionWriter;
        private TrackingConfigSnapshot _trackingSnapshot;
        private readonly System.Diagnostics.Stopwatch _mono = System.Diagnostics.Stopwatch.StartNew();

        public bool IsActive
        {
            get
            {
                return _phase == RecordingSessionPhase.Starting ||
                       _phase == RecordingSessionPhase.Recording ||
                       _phase == RecordingSessionPhase.Stopping;
            }
        }

        public bool NeedsForceStop()
        {
            return IsActive && _routine == null && !_finalized;
        }

        public string SessionDir
        {
            get { return _sessionDir; }
        }

        public void Begin(UICameraCtrl host, string eyeMode, int width, int height, int targetFps,
            int saveFps, bool outputRaw)
        {
            if (IsActive || host == null)
            {
                return;
            }

            _host = host;
            _eyeMode = NormalizeEye(eyeMode);
            // Buffer dump keeps PICO's per-eye buffer; use 2048×1536 (not SBS encode canvas).
            _width = width > 0 ? width : DefaultWidth;
            _height = height > 0 ? height : DefaultHeight;
            if (_width > DefaultWidth)
            {
                // Avoid accidental 4096 SBS request on CPU path unless caller insists.
                _width = DefaultWidth;
            }

            _targetFps = targetFps == 60 ? 60 : 30;
            _saveFps = Mathf.Clamp(saveFps > 0 ? saveFps : DefaultSaveFps, 1, _targetFps);
            _outputRaw = outputRaw;
            _saveIntervalNs = (long)(1e9 / _saveFps);
            _finalized = false;
            _stopRequested = false;
            _cameraOpen = false;
            _capturing = false;
            _callbackCount = 0;
            _savedCount = 0;
            _zeroDataCount = 0;
            _lastSavedMonoNs = 0;
            _phase = RecordingSessionPhase.Starting;

            int bytes = _width * _height * 4;
            _imgByte = new byte[bytes];
            _pending = new byte[bytes];
            if (_imgHandle.IsAllocated)
            {
                _imgHandle.Free();
            }

            _imgHandle = GCHandle.Alloc(_imgByte, GCHandleType.Pinned);

            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string download = VisionTrackingSession.DownloadDir;
            string root = Path.Combine(download, CollectionSessionWriter.RootFolderName);
            if (Directory.Exists(Path.Combine(root, "session_" + _sessionId)))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            _trackingSnapshot = TrackingConfigSnapshot.CaptureFromPanel();
            _trackingSnapshot.Head = false;
            _trackingSnapshot.Controller = false;
            _trackingSnapshot.Hand = false;
            _trackingSnapshot.FullBody = false;
            _trackingSnapshot.MotionTracker = false;

            _sessionWriter = new CollectionSessionWriter(true, download);
            _sessionWriter.Start(
                _trackingSnapshot,
                EnterpriseCollectionRecorder.SharedEnterpriseSampleHz,
                90,
                false,
                SystemInfo.deviceModel,
                Utils.IsPico4U(),
                "session_" + _sessionId);
            _sessionDir = _sessionWriter.SessionDir;
            _framesDir = Path.Combine(_sessionDir, "frames");
            Directory.CreateDirectory(_framesDir);

            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["sessionDir"] = _sessionDir;
            _meta["exp_Image"] = 1;
            _meta["exp_buf"] = 1;
            _meta["encodePath"] = EncodePathId;
            _meta["saveType"] = "for4u-cpu-buffer-jpeg";
            _meta["status"] = "starting";
            _meta["eyeMode"] = _eyeMode;
            _meta["width"] = _width;
            _meta["height"] = _height;
            _meta["requestedFps"] = _targetFps;
            _meta["saveFps"] = _saveFps;
            _meta["raw"] = _outputRaw;
            _meta["exp_crop"] = 0;
            _meta["surfacePath"] = "none-cpu-buffer";
            _meta["layout"] =
                "SetCameraFrameBufferfor4U + StartGetImageDatafor4U; no StartPreview; no eye-crop. " +
                "Frames are as filled by PICO into the pinned RGBA buffer.";
            _meta["note"] =
                "This is the closest in-app restore of PICO-delivered pixels. " +
                "Not Bayer sensor raw; still SDK RGBA at requested WxH.";
            WriteMeta();

            _routine = _host.StartCoroutine(Run());
            LogWindow.Info("[For4UBuffer] start " + _eyeMode + " " + _width + "x" + _height +
                           " target=" + _targetFps + " save=" + _saveFps + " raw=" + (_outputRaw ? 1 : 0));
        }

        public void RequestStop(string reason)
        {
            _stopRequested = true;
            if (_phase == RecordingSessionPhase.Recording || _phase == RecordingSessionPhase.Starting)
            {
                _phase = RecordingSessionPhase.Stopping;
            }

            if (_meta != null)
            {
                _meta["stopReason"] = reason ?? "user";
            }
        }

        public void ForceReset(string reason)
        {
            RequestStop(reason);
            Finish(RecordingSessionPhase.FailedPartial, reason ?? "force");
        }

        public void Pump()
        {
            if (!IsActive)
            {
                return;
            }

            DrainPendingFrame();
            if (_stopRequested && _phase != RecordingSessionPhase.Idle && !_finalized)
            {
                // Finish is also invoked from coroutine end; Force if coroutine died.
                if (_routine == null)
                {
                    Finish(RecordingSessionPhase.FailedPartial, "pump_force");
                }
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator Run()
        {
            float deadline = Time.realtimeSinceStartup + StartTimeoutSeconds;
            Exception cfgEx = null;
            try
            {
                PXR_Enterprise.Configurefor4U(false, _targetFps);
                var rawCfg = new System.Collections.Generic.Dictionary<string, string>
                {
                    {
                        PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA,
                        _outputRaw ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                    }
                };
                PXR_Enterprise.Configurefor4U(rawCfg);
            }
            catch (Exception exception)
            {
                cfgEx = exception;
            }

            if (cfgEx != null)
            {
                _meta["fail"] = "Configurefor4U: " + cfgEx.Message;
                Finish(RecordingSessionPhase.FailedPartial, "configure");
                yield break;
            }

            bool openDone = false;
            bool openOk = false;
            try
            {
                var openFlags = new System.Collections.Generic.Dictionary<string, string>
                {
                    { PXRCapture.KEY_EIS, PXRCapture.VALUE_FALSE },
                    { PXRCapture.KEY_MCTF, PXRCapture.VALUE_FALSE },
                    { PXRCapture.KEY_MFNR, PXRCapture.VALUE_FALSE }
                };
                PXR_Enterprise.OpenCameraAsyncfor4U(ok =>
                {
                    openOk = ok;
                    openDone = true;
                }, openFlags);
            }
            catch (Exception exception)
            {
                _meta["fail"] = "OpenCameraAsyncfor4U: " + exception.Message;
                Finish(RecordingSessionPhase.FailedPartial, "open");
                yield break;
            }

            while (!openDone && !_stopRequested && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            _meta["openCameraOk"] = openOk;
            if (!openDone || !openOk)
            {
                _meta["fail"] = openDone ? "open returned false" : "open timeout";
                Finish(RecordingSessionPhase.FailedPartial, "open");
                yield break;
            }

            _cameraOpen = true;
            AttachCalibration();

            IntPtr data = _imgHandle.AddrOfPinnedObject();
            try
            {
                PXR_Enterprise.SetCameraFrameBufferfor4U(_width, _height, ref data, OnImageAvailable);
            }
            catch (Exception exception)
            {
                _meta["fail"] = "SetCameraFrameBufferfor4U: " + exception.Message;
                Finish(RecordingSessionPhase.FailedPartial, "buffer");
                yield break;
            }

            PXRCaptureRenderMode mode = ParseMode(_eyeMode);
            bool started;
            try
            {
                started = PXR_Enterprise.StartGetImageDatafor4U(mode, _width, _height);
            }
            catch (Exception exception)
            {
                _meta["fail"] = "StartGetImageDatafor4U: " + exception.Message;
                Finish(RecordingSessionPhase.FailedPartial, "start_get");
                yield break;
            }

            _meta["StartGetImageDatafor4U"] = started;
            if (!started)
            {
                _meta["fail"] = "StartGetImageDatafor4U returned false";
                Finish(RecordingSessionPhase.FailedPartial, "start_get");
                yield break;
            }

            _capturing = true;
            _phase = RecordingSessionPhase.Recording;
            _meta["status"] = "recording";
            WriteMeta();
            if (_host != null)
            {
                _host.SetRecordStatus("Buf…");
            }

            while (!_stopRequested)
            {
                DrainPendingFrame();
                yield return null;
            }

            Finish(RecordingSessionPhase.Completed, "user");
        }
#else
        private IEnumerator Run()
        {
            _meta["fail"] = "buffer dump requires Android device";
            Finish(RecordingSessionPhase.FailedPartial, "editor");
            yield break;
        }
#endif

        private void OnImageAvailable(Frame frame)
        {
            Interlocked.Increment(ref _callbackCount);
            if (frame.datasize == 0)
            {
                Interlocked.Increment(ref _zeroDataCount);
                return;
            }

            long monoNs = (long)(_mono.Elapsed.TotalMilliseconds * 1_000_000.0);
            int expected = _width * _height * 4;
            int copyLen = expected;
            if (frame.datasize > 0 && frame.datasize < expected)
            {
                copyLen = (int)frame.datasize;
            }

            bool wantSave = _lastSavedMonoNs == 0 ||
                            (monoNs - _lastSavedMonoNs) >= _saveIntervalNs;
            if (wantSave)
            {
                _lastSavedMonoNs = monoNs;
            }

            lock (_frameLock)
            {
                Buffer.BlockCopy(_imgByte, 0, _pending, 0, copyLen);
                _pendingLen = copyLen;
                _pendingTs = frame.timestamp;
                _pendingMonoNs = monoNs;
                _pendingDatasize = frame.datasize;
                _pendingSave = wantSave;
                _pendingReady = true;
            }
        }

        private void DrainPendingFrame()
        {
            byte[] rgba = null;
            int len = 0;
            ulong ts = 0;
            long monoNs = 0;
            uint datasize = 0;
            bool save = false;
            lock (_frameLock)
            {
                if (!_pendingReady)
                {
                    return;
                }

                _pendingReady = false;
                save = _pendingSave;
                if (!save)
                {
                    return;
                }

                len = _pendingLen;
                rgba = new byte[len];
                Buffer.BlockCopy(_pending, 0, rgba, 0, len);
                ts = _pendingTs;
                monoNs = _pendingMonoNs;
                datasize = _pendingDatasize;
            }

            if (rgba == null || len < _width * _height * 4)
            {
                return;
            }

            try
            {
                var tex = new Texture2D(_width, _height, TextureFormat.RGBA32, false);
                tex.LoadRawTextureData(rgba);
                tex.Apply(false, false);
                byte[] jpg = tex.EncodeToJPG(_jpegQuality);
                UnityEngine.Object.Destroy(tex);
                string name = _savedCount.ToString("D6", CultureInfo.InvariantCulture) + ".jpg";
                string path = Path.Combine(_framesDir, name);
                File.WriteAllBytes(path, jpg);
                string line = "{\"index\":" + _savedCount +
                              ",\"frame_timestamp_ns\":" + ts +
                              ",\"mono_ns\":" + monoNs +
                              ",\"datasize\":" + datasize +
                              ",\"path\":\"frames/" + name + "\"}\n";
                File.AppendAllText(Path.Combine(_sessionDir, "frames.jsonl"), line);
                _savedCount++;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[For4UBuffer] save: " + exception.Message);
            }
        }

        private void AttachCalibration()
        {
            try
            {
                JsonData calib = CaptureReport.CalibrationNote(_width, _height, true);
                _meta["calibration"] = calib;
                string intrinsicsE = Utils.GetCameraIntrinsicsStrE(_width, _height);
                string extrinsicsE = Utils.GetCameraExtrinsicsStrE();
                _meta["cameraIntrinsicsE"] = intrinsicsE ?? "";
                _meta["cameraExtrinsicsE"] = extrinsicsE ?? "";
                File.WriteAllText(Path.Combine(_sessionDir, "camera_calib.json"), calib.ToJson());
                string bundle = "CameraExtrinsics:" + extrinsicsE + "\n" +
                               "cameraIntrinsics:" + intrinsicsE + "\n" +
                               "applicableWidth:" + _width + "\n" +
                               "applicableHeight:" + _height + "\n" +
                               "encodePath:" + EncodePathId + "\n" +
                               "raw:" + (_outputRaw ? 1 : 0) + "\n";
                File.WriteAllText(Path.Combine(_sessionDir, "camera_params.txt"), bundle);
                WriteMeta();
            }
            catch (Exception exception)
            {
                _meta["calibrationError"] = exception.Message;
            }
        }

        private void Finish(RecordingSessionPhase phase, string reason)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
            _phase = RecordingSessionPhase.Stopping;
            DrainPendingFrame();

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                if (_capturing || _cameraOpen)
                {
                    PXR_Enterprise.CloseCamerafor4U();
                }
            }
            catch (Exception exception)
            {
                _meta["closeError"] = exception.Message;
            }
#endif
            _capturing = false;
            _cameraOpen = false;

            if (_imgHandle.IsAllocated)
            {
                _imgHandle.Free();
            }

            _meta["callbackCount"] = _callbackCount;
            _meta["savedCount"] = _savedCount;
            _meta["zeroDataCount"] = _zeroDataCount;
            _meta["status"] = phase == RecordingSessionPhase.Completed ? "completed" : "failed_partial";
            _meta["reason"] = reason ?? "";
            _meta["completedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["offlineHint"] =
                "python3 Docs/tools/buffer_frames_to_mp4.py <session_dir>  →  restores MP4 from frames/ (no calib warp).";
            WriteMeta();

            try
            {
                if (_sessionWriter != null)
                {
                    _sessionWriter.SetCaptureExtra(_meta);
                    _sessionWriter.Stop(
                        phase == RecordingSessionPhase.Completed ? "completed" : "failed_partial",
                        reason ?? "",
                        "none",
                        reason ?? "",
                        2000);
                    _sessionWriter.Dispose();
                    _sessionWriter = null;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[For4UBuffer] writer: " + exception.Message);
            }

            _phase = RecordingSessionPhase.Idle;
            _routine = null;
            if (_host != null)
            {
                _host.SetRecordStatus(phase == RecordingSessionPhase.Completed ? "Buf done" : "Buf fail");
            }

            LogWindow.Info("[For4UBuffer] " + _meta["status"] + " saved=" + _savedCount +
                           " cb=" + _callbackCount + " zero=" + _zeroDataCount +
                           " dir=" + _sessionDir);
            if (_zeroDataCount > 0 && _savedCount == 0)
            {
                Toast.Show("Buffer empty — check SN/package camera auth");
            }
            else if (phase == RecordingSessionPhase.Completed)
            {
                Toast.Show("Buf saved " + _savedCount + " frames");
            }
        }

        private void WriteMeta()
        {
            try
            {
                if (_sessionWriter != null && _meta != null)
                {
                    _sessionWriter.SetCaptureExtra(_meta);
                    _sessionWriter.FlushMeta();
                }
            }
            catch
            {
            }
        }

        private static string NormalizeEye(string mode)
        {
            if (string.Equals(mode, "RIGHT", StringComparison.OrdinalIgnoreCase))
            {
                return "RIGHT";
            }

            if (string.Equals(mode, "3D", StringComparison.OrdinalIgnoreCase))
            {
                return "3D";
            }

            return "LEFT";
        }

        private static PXRCaptureRenderMode ParseMode(string eye)
        {
            if (eye == "RIGHT")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_RIGHT;
            }

            if (eye == "3D")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_3D;
            }

            return PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT;
        }
    }
}
