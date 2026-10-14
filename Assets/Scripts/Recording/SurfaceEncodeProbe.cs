using System;
using System.Collections;
using System.Globalization;
using System.IO;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// for4U Surface → MediaCodec MP4 (production, exp_Image=1) and optional B0 pattern probe.
    /// Production path: BeginCamera with panel eye/fps/flags. B0: PrefsMode=1 or surface_probe_b0.
    /// </summary>
    public sealed class SurfaceEncodeProbe
    {
        public const string PrefsModeKey = "SurfaceEncodeProbeMode";
        public const int ModeOff = 0;
        public const int ModePatternB0 = 1;
        public const int ModeCameraB1 = 2;

        public const int ProbeWidth = 1024;
        public const int ProbeHeight = 1024;
        public const int EyeWidth = 2048;
        public const int EyeHeight = 1536;
        public const int SbsWidth = 4096;
        public const int DefaultWidth = EyeWidth;
        public const int DefaultHeight = EyeHeight;
        public const int DefaultBitrateBps = 8 * 1000 * 1000;
        public const string EncodePathId = "for4u-surface-mediacodec";
        /// <summary>Max seconds stuck in Starting before ForceReset (open camera + crop + preview).</summary>
        public const float StartTimeoutSeconds = 20f;

        private UICameraCtrl _host;
        private Coroutine _routine;
        private JsonData _meta;
        private string _sessionId;
        private string _metaPath;
        private string _videoPath;
        private string _frameMapPath;
        private string _trackingPath;
        private StreamWriter _trackingWriter;
        private TrackingData _trackingData;
        private JsonData _trackingJson;
        private RecordingSessionPhase _phase = RecordingSessionPhase.Idle;
        private bool _finalized;
        private bool _stopRequested;
        private bool _cameraOpen;
        private bool _trackingEnabled;
        private int _mode;
        private int _fps;
        private int _bitrateBps;
        private int _width;
        private int _height;
        private int _previewWidth;
        private int _previewHeight;
        private int _requestedBitrateBps;
        private string _eyeMode = "LEFT";
        private bool _useEyeCrop;
        private bool _outputRaw;
        private bool _enableEis;
        private bool _enableMctf;
        private bool _enableMfnr;
        private int _trackingSamples;
        private long _trackingFirstElapsedRealtimeNs;
        private long _trackingLastElapsedRealtimeNs;
        private long _videoStartElapsedRealtimeNs;
        private double _videoStartPredictTimeUs;
        private string _videoStartUtc;
        private long _lastDiskCheckMs;
        private float _startedRealtime;
        private AndroidJavaObject _encoder;
        private AndroidJavaObject _inputSurfaceJava;
        private AndroidJavaObject _cameraSurfaceJava;
        private IntPtr _surfaceGlobalRef = IntPtr.Zero;

        public bool IsActive
        {
            get
            {
                return _phase == RecordingSessionPhase.Starting ||
                       _phase == RecordingSessionPhase.Recording ||
                       _phase == RecordingSessionPhase.Stopping;
            }
        }

        /// <summary>True when phase is active but the Run coroutine is gone (stuck UI).</summary>
        public bool NeedsForceStop()
        {
            return IsActive && _routine == null && !_finalized;
        }

        public string SessionId
        {
            get { return _sessionId; }
        }

        public static int PrefsMode
        {
            get { return PlayerPrefs.GetInt(PrefsModeKey, ModeOff); }
            set
            {
                PlayerPrefs.SetInt(PrefsModeKey, value);
                PlayerPrefs.Save();
            }
        }

        /// <summary>
        /// Optional B0 pattern probe only (PrefsMode=1 or /sdcard/Download/surface_probe_b0).
        /// Production Surface recording is exp_Image=1; B1 prefs/markers are ignored.
        /// </summary>
        public static int ResolveMode()
        {
            if (PrefsMode == ModePatternB0)
            {
                return ModePatternB0;
            }

            try
            {
                if (File.Exists(Path.Combine(VisionTrackingSession.DownloadDir, "surface_probe_b0")))
                {
                    return ModePatternB0;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] marker check failed: " + exception.Message);
            }

            return ModeOff;
        }

        /// <summary>Deletes legacy Download marker files that used to force probe mode.</summary>
        public static void ClearLegacyMarkers()
        {
            try
            {
                string dir = VisionTrackingSession.DownloadDir;
                string b0 = Path.Combine(dir, "surface_probe_b0");
                string b1 = Path.Combine(dir, "surface_probe_b1");
                if (File.Exists(b0))
                {
                    File.Delete(b0);
                }

                if (File.Exists(b1))
                {
                    File.Delete(b1);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] ClearLegacyMarkers: " + exception.Message);
            }
        }

        /// <summary>
        /// Clears a stuck Starting/Stopping state so Record dialog can open again.
        /// </summary>
        public void ForceReset(string reason)
        {
            if (_phase == RecordingSessionPhase.Idle && !_finalized)
            {
                return;
            }

            Debug.LogWarning("[SurfaceEncodeProbe] ForceReset phase=" + _phase + " reason=" + reason);
            try
            {
                if (!_finalized && _phase != RecordingSessionPhase.Idle)
                {
                    Finish(RecordingSessionPhase.Failed, "force_reset:" + reason);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[SurfaceEncodeProbe] ForceReset Finish failed: " + exception.Message);
            }

            _phase = RecordingSessionPhase.Idle;
            _finalized = false;
            _stopRequested = false;
            _routine = null;
            ReleaseJavaResources();
        }

        private void ReleaseJavaResources()
        {
            ReleaseSurfaceGlobalRef();
            if (_inputSurfaceJava != null)
            {
                try
                {
                    _inputSurfaceJava.Dispose();
                }
                catch
                {
                }

                _inputSurfaceJava = null;
            }

            if (_cameraSurfaceJava != null)
            {
                try
                {
                    _cameraSurfaceJava.Dispose();
                }
                catch
                {
                }

                _cameraSurfaceJava = null;
            }

            if (_encoder != null)
            {
                try
                {
                    _encoder.Dispose();
                }
                catch
                {
                }

                _encoder = null;
            }
        }

        private void ReleaseSurfaceGlobalRef()
        {
            if (_surfaceGlobalRef != IntPtr.Zero)
            {
                try
                {
                    AndroidJNI.DeleteGlobalRef(_surfaceGlobalRef);
                }
                catch
                {
                }

                _surfaceGlobalRef = IntPtr.Zero;
            }
        }

        public void BeginPattern(UICameraCtrl host, int fps, bool tracking)
        {
            Begin(host, ModePatternB0, "LEFT", ProbeWidth, ProbeHeight, ProbeWidth, ProbeHeight,
                fps, DefaultBitrateBps, tracking, false, false, false, false);
        }

        public void BeginCamera(UICameraCtrl host, string eyeMode, int width, int height, int fps,
            int bitrateBps, bool tracking, bool outputRaw, bool enableEis, bool enableMctf,
            bool enableMfnr, int previewWidth = SbsWidth, int previewHeight = EyeHeight)
        {
            Begin(host, ModeCameraB1, eyeMode, width, height, previewWidth, previewHeight, fps,
                bitrateBps, tracking, outputRaw, enableEis, enableMctf, enableMfnr);
        }

        public void Begin(UICameraCtrl host, int mode, string eyeMode, int width, int height,
            int previewWidth, int previewHeight, int fps, int bitrateBps, bool tracking,
            bool outputRaw, bool enableEis, bool enableMctf, bool enableMfnr)
        {
            if (IsActive || host == null)
            {
                return;
            }

            if (mode != ModePatternB0 && mode != ModeCameraB1)
            {
                Debug.LogError("[SurfaceEncodeProbe] mode must be 1 (B0) or 2 (camera)");
                return;
            }

            _host = host;
            _mode = mode;
            _eyeMode = NormalizeEyeMode(eyeMode);
            if (mode == ModeCameraB1)
            {
                int encodeW;
                int encodeH;
                RecordDialog.ResolveEncodeSize(_eyeMode, out encodeW, out encodeH);
                _width = encodeW;
                _height = encodeH;
                _previewWidth = previewWidth > 0 ? previewWidth : SbsWidth;
                _previewHeight = previewHeight > 0 ? previewHeight : EyeHeight;
                _useEyeCrop = _eyeMode != "3D";
            }
            else
            {
                _width = ProbeWidth;
                _height = ProbeHeight;
                _previewWidth = ProbeWidth;
                _previewHeight = ProbeHeight;
                _eyeMode = "LEFT";
                _useEyeCrop = false;
                outputRaw = false;
                enableEis = false;
                enableMctf = false;
                enableMfnr = false;
            }

            _fps = fps == 60 ? 60 : Mathf.Clamp(fps, 1, 60);
            if (_fps != 30 && _fps != 60)
            {
                _fps = 30;
            }

            _requestedBitrateBps = bitrateBps > 0 ? bitrateBps : DefaultBitrateBps;
            int scale = Mathf.Max(1, _width / EyeWidth);
            _bitrateBps = _requestedBitrateBps * scale;
            _trackingEnabled = tracking;
            _outputRaw = outputRaw;
            _enableEis = enableEis;
            _enableMctf = enableMctf;
            _enableMfnr = enableMfnr;
            if (_outputRaw && (_enableEis || _enableMctf || _enableMfnr))
            {
                _enableEis = false;
                _enableMctf = false;
                _enableMfnr = false;
            }

            ReleaseJavaResources();
            _finalized = false;
            _stopRequested = false;
            _cameraOpen = false;
            _trackingSamples = 0;
            _trackingFirstElapsedRealtimeNs = 0;
            _trackingLastElapsedRealtimeNs = 0;
            _lastDiskCheckMs = 0;
            _startedRealtime = Time.realtimeSinceStartup;
            _phase = RecordingSessionPhase.Starting;
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string download = VisionTrackingSession.DownloadDir;
            if (File.Exists(Path.Combine(download, "recording_meta_" + _sessionId + ".json")))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            string videoName = mode == ModePatternB0
                ? "surf_b0_CameraRecord_" + _sessionId + ".mp4"
                : "for4u_CameraRecord_" + _sessionId + ".mp4";
            string mapName = mode == ModePatternB0
                ? "surf_b0_frame_map_" + _sessionId + ".jsonl"
                : "for4u_frame_map_" + _sessionId + ".jsonl";
            _videoPath = Path.Combine(download, videoName);
            _frameMapPath = Path.Combine(download, mapName);
            _metaPath = Path.Combine(download, "recording_meta_" + _sessionId + ".json");
            _trackingPath = Path.Combine(download, "trackingData_" + _sessionId + ".txt");

            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["exp_Image"] = mode == ModeCameraB1 ? 1 : -1;
            _meta["saveType"] = mode == ModePatternB0 ? "surface-encode-probe" : "for4u-surface-mp4";
            _meta["probePhase"] = mode == ModePatternB0 ? "B0-pattern" : "for4u-surface";
            _meta["status"] = "starting";
            _meta["requestUtc"] = DateTime.UtcNow.ToString("o");
            _meta["videoPath"] = _videoPath;
            _meta["frameMapPath"] = _frameMapPath;
            _meta["frameMapSemantics"] =
                "t_video_ns = video_pts_us * 1000. Compare to tracking elapsedRealtimeNs. " +
                "host_elapsedRealtimeNs_at_output is dequeue time, not exposure.";
            _meta["alignFormula"] = "t_video_ns = video_pts_us * 1000";
            CaptureReport.WriteEncodeTimestampContract(_meta, EncodePathId,
                "mp4_pts_boottime_family", _eyeMode, _width, _height, _fps, _bitrateBps, "avc",
                _enableEis, _enableMctf, _enableMfnr, _outputRaw, EncodePathId);
            _meta["timestampStrategyNote"] =
                "video_pts_us shares the boottime family with elapsedRealtimeNs but is encoder PTS, " +
                "not proven exposure time. Clock proximity alone does not complete exposure sync.";
            _meta["requestedEyeMode"] = _eyeMode;
            _meta["requestedWidth"] = _width;
            _meta["requestedHeight"] = _height;
            _meta["encodeSize"] = _width + "x" + _height;
            _meta["previewSize"] = _previewWidth + "x" + _previewHeight;
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : "direct";
            _meta["cropHalf"] = !_useEyeCrop ? "none" :
                (_eyeMode == "RIGHT" ? "right" : "left");
            if (_useEyeCrop)
            {
                _meta["layout"] = _eyeMode + " SBS half cropped to full " + _width + "x" + _height +
                                  " (4:3)";
            }
            else if (_eyeMode == "3D")
            {
                _meta["layout"] = "3D SBS " + _width + "x" + _height +
                                  " (8:3, each eye 4:3)";
            }
            else
            {
                _meta["layout"] = _eyeMode + " " + _width + "x" + _height;
            }

            _meta["requestedFps"] = _fps;
            _meta["requestedBitrateBps"] = _requestedBitrateBps;
            _meta["outputCameraRawData"] = _outputRaw;
            _meta["enableEis"] = _enableEis;
            _meta["enableMctf"] = _enableMctf;
            _meta["enableMfnr"] = _enableMfnr;

            long estimated = CaptureReport.EstimateSurfaceMp4Bytes(_bitrateBps, 20 * 60);
            long free;
            long required;
            string diskError;
            bool diskOk = DiskSafety.EnsureSpace(download, estimated, out free, out required, out diskError);
            CaptureReport.WriteDiskBudget(_meta, estimated, free, required, diskOk, download);
            WriteMeta();
            SetVisual("starting");
            if (!diskOk)
            {
                Finish(RecordingSessionPhase.Failed, diskError ?? "disk budget failed");
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            _routine = host.StartCoroutine(Run());
#else
            Finish(RecordingSessionPhase.Failed, "SurfaceEncodeProbe requires Android device");
#endif
        }

        private static string NormalizeEyeMode(string mode)
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

        private static PXRCaptureRenderMode ParseRenderMode(string eyeMode)
        {
            if (eyeMode == "RIGHT")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_RIGHT;
            }

            if (eyeMode == "3D")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_3D;
            }

            return PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT;
        }

        public void RequestStop(string reason)
        {
            if (!IsActive || _finalized)
            {
                return;
            }

            _stopRequested = true;
            if (_meta != null)
            {
                _meta["stopReason"] = reason ?? "user";
            }
        }

        public void Pump()
        {
            if (!IsActive || _finalized)
            {
                return;
            }

            // Starting watchdog: dead coroutine or total start budget exceeded → ForceReset.
            if (_phase == RecordingSessionPhase.Starting)
            {
                if (NeedsForceStop())
                {
                    ForceReset("starting_coroutine_lost");
                    return;
                }

                if (Time.realtimeSinceStartup - _startedRealtime >= StartTimeoutSeconds)
                {
                    if (_meta != null)
                    {
                        _meta["startTimeoutSec"] = StartTimeoutSeconds;
                    }

                    ForceReset("starting_timeout");
                    return;
                }
            }

            long now = (long)(Time.realtimeSinceStartup * 1000.0);
            if (now - _lastDiskCheckMs >= 30000)
            {
                _lastDiskCheckMs = now;
                long free = DiskSafety.GetFreeBytes(VisionTrackingSession.DownloadDir);
                if (_meta != null)
                {
                    _meta["lastFreeBytes"] = free;
                }

                if (free >= 0 && free < DiskSafety.ReserveBytes)
                {
                    RequestStop("disk free below 2GB reserve");
                }
            }

            if (_trackingEnabled && _trackingWriter != null && _phase == RecordingSessionPhase.Recording)
            {
                WriteTrackingSample();
            }
        }

        public void WriteTrackingSample()
        {
            if (!_trackingEnabled || _trackingWriter == null || _phase != RecordingSessionPhase.Recording)
            {
                return;
            }

            try
            {
                if (_trackingData == null)
                {
                    _trackingData = new TrackingData();
                    _trackingJson = new JsonData();
                }

                long elapsed = Utils.GetElapsedRealtimeNs();
                _trackingData.Get(ref _trackingJson);
                _trackingJson["elapsedRealtimeNs"] = elapsed;
                _trackingJson["sessionId"] = _sessionId;
                _trackingWriter.WriteLine(_trackingJson.ToJson());
                if (_trackingSamples == 0)
                {
                    _trackingFirstElapsedRealtimeNs = elapsed;
                }

                _trackingLastElapsedRealtimeNs = elapsed;
                _trackingSamples++;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] tracking write failed: " + exception.Message);
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator Run()
        {
            SetStatus("Starting…");
            string failReason = null;
            // Cooperative start budget (Pump also ForceResets at StartTimeoutSeconds).
            float startDeadline = _startedRealtime + StartTimeoutSeconds;

            int startResult = -99;
            Exception startEx = null;
            try
            {
                _encoder = new AndroidJavaObject("com.xrobotoolkit.enterprise.SurfaceVideoEncoder");
                startResult = _encoder.Call<int>("start", _videoPath, _frameMapPath, _width,
                    _height, _fps, _bitrateBps);
            }
            catch (Exception exception)
            {
                startEx = exception;
            }

            if (startEx != null)
            {
                failReason = "encoder start exception: " + startEx.Message;
            }
            else if (startResult != 0)
            {
                failReason = "encoder start returned " + startResult +
                             " (rebuild APK if class SurfaceVideoEncoder missing)";
            }

            if (failReason == null)
            {
                _videoStartUtc = DateTime.UtcNow.ToString("o");
                _videoStartElapsedRealtimeNs = Utils.GetElapsedRealtimeNs();
                try
                {
                    _videoStartPredictTimeUs = PXR_Enterprise.GetPredictedDisplayTime() * 1000.0;
                }
                catch (Exception exception)
                {
                    _videoStartPredictTimeUs = 0;
                    _meta["predictTimeError"] = exception.Message;
                }

                _meta["videoStartUtc"] = _videoStartUtc;
                _meta["videoStartElapsedRealtimeNs"] = _videoStartElapsedRealtimeNs;
                _meta["videoStartPredictTimeUs"] = _videoStartPredictTimeUs;
                WriteMeta();

                if (_mode == ModePatternB0)
                {
                    int patternResult = -1;
                    Exception patternEx = null;
                    try
                    {
                        patternResult = _encoder.Call<int>("startPatternProducer", _fps);
                    }
                    catch (Exception exception)
                    {
                        patternEx = exception;
                    }

                    _meta["patternStartResult"] = patternResult;
                    _meta["patternProducer"] = "EGL_glClear_swapBuffers";
                    if (patternEx != null)
                    {
                        failReason = "pattern producer exception: " + patternEx.Message;
                    }
                    else if (patternResult != 0)
                    {
                        failReason = "pattern producer failed " + patternResult;
                    }
                }
                else
                {
                    yield return OpenCameraAndPreview();
                    if (_meta != null && _meta.ContainsKey("previewFailReason"))
                    {
                        failReason = _meta["previewFailReason"].ToString();
                    }
                }
            }

            if (failReason == null && !_finalized && !_stopRequested
                && Time.realtimeSinceStartup >= startDeadline)
            {
                failReason = "starting timeout after " + StartTimeoutSeconds + "s";
                if (_meta != null)
                {
                    _meta["startTimeoutSec"] = StartTimeoutSeconds;
                }
            }

            if (failReason == null && !_finalized && !_stopRequested)
            {
                if (_trackingEnabled)
                {
                    OpenTrackingWriter();
                }

                _phase = RecordingSessionPhase.Recording;
                _meta["status"] = "recording";
                WriteMeta();
                SetVisual("recording");
                SetStatus(_mode == ModePatternB0 ? "B0 recording" : "Recording");
                LogWindow.Info("[SurfaceEncodeProbe] recording " +
                               (_mode == ModePatternB0 ? "B0" : "for4U Surface") +
                               " " + _eyeMode + " " + _width + "x" + _height + "@" + _fps +
                               " session=" + _sessionId);
                if (_trackingEnabled)
                {
                    WriteTrackingSample();
                }

                while (!_stopRequested && !_finalized)
                {
                    yield return null;
                }
            }

            // Avoid StopCoroutine(self) if Finish is invoked from here.
            _routine = null;
            if (!_finalized)
            {
                if (!string.IsNullOrEmpty(failReason))
                {
                    Finish(RecordingSessionPhase.Failed, failReason);
                }
                else
                {
                    Finish(RecordingSessionPhase.Completed,
                        _meta != null && _meta.ContainsKey("stopReason")
                            ? _meta["stopReason"].ToString()
                            : "stop");
                }
            }
        }

        private IEnumerator OpenCameraAndPreview()
        {
            // Note: iterators cannot yield inside try/catch. Keep yields outside catch blocks.
            Exception configEx = null;
            try
            {
                PXR_Enterprise.Configurefor4U(false, _fps);
                var flags = new System.Collections.Generic.Dictionary<string, string>
                {
                    {
                        PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA,
                        _outputRaw ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                    },
                };
                PXR_Enterprise.Configurefor4U(flags);
            }
            catch (Exception exception)
            {
                configEx = exception;
            }

            if (configEx != null)
            {
                _meta["previewFailReason"] = "Configurefor4U: " + configEx.Message;
                yield break;
            }

            var openFlags = new System.Collections.Generic.Dictionary<string, string>
            {
                {
                    PXRCapture.KEY_EIS,
                    _enableEis ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                },
                {
                    PXRCapture.KEY_MCTF,
                    _enableMctf ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                },
                {
                    PXRCapture.KEY_MFNR,
                    _enableMfnr ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                },
            };

            bool openDone = false;
            bool openOk = false;
            Exception openEx = null;
            try
            {
                PXR_Enterprise.OpenCameraAsyncfor4U(ok =>
                {
                    openOk = ok;
                    openDone = true;
                }, openFlags);
            }
            catch (Exception exception)
            {
                openEx = exception;
            }

            if (openEx != null)
            {
                _meta["previewFailReason"] = "OpenCameraAsyncfor4U: " + openEx.Message;
                yield break;
            }

            float waited = 0f;
            while (!openDone && waited < 12f && !_stopRequested)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            _meta["openCameraOk"] = openOk;
            _meta["openCameraWaitSec"] = waited.ToString("R", CultureInfo.InvariantCulture);
            if (!openOk)
            {
                _meta["previewFailReason"] = "OpenCameraAsyncfor4U failed or timed out";
                yield break;
            }

            _cameraOpen = true;

            int previewW = _useEyeCrop ? _previewWidth : _width;
            int previewH = _useEyeCrop ? _previewHeight : _height;
            if (_useEyeCrop)
            {
                int cropCreate = -99;
                Exception cropCreateEx = null;
                try
                {
                    cropCreate = _encoder.Call<int>("createCameraSurface", previewW, previewH);
                }
                catch (Exception exception)
                {
                    cropCreateEx = exception;
                }

                _meta["createCameraSurfaceResult"] = cropCreate;
                if (cropCreateEx != null)
                {
                    _meta["previewFailReason"] = "createCameraSurface: " + cropCreateEx.Message;
                    yield break;
                }

                if (cropCreate != 0)
                {
                    _meta["previewFailReason"] = "createCameraSurface returned " + cropCreate;
                    yield break;
                }

                int cropStart = -99;
                Exception cropStartEx = null;
                try
                {
                    cropStart = _encoder.Call<int>("startEyeCropProducer", _eyeMode);
                }
                catch (Exception exception)
                {
                    cropStartEx = exception;
                }

                _meta["startEyeCropResult"] = cropStart;
                if (cropStartEx != null)
                {
                    _meta["previewFailReason"] = "startEyeCropProducer: " + cropStartEx.Message;
                    yield break;
                }

                if (cropStart != 0)
                {
                    _meta["previewFailReason"] = "startEyeCropProducer returned " + cropStart;
                    yield break;
                }
            }

            // Prefer Java Surface jobject (NewGlobalRef). Reflection getNativeObject can SIGSEGV.
            IntPtr surfacePtr = IntPtr.Zero;
            string surfaceMethod = "none";
            Exception surfaceEx = null;
            string surfaceGetter = _useEyeCrop ? "getCameraSurface" : "getInputSurface";
            string nativeGetter = _useEyeCrop ? "getCameraSurfaceNativeHandle" : "getInputSurfaceNativeHandle";
            try
            {
                AndroidJavaObject surfaceObj = _encoder.Call<AndroidJavaObject>(surfaceGetter);
                if (_useEyeCrop)
                {
                    _cameraSurfaceJava = surfaceObj;
                }
                else
                {
                    _inputSurfaceJava = surfaceObj;
                }

                if (surfaceObj != null)
                {
                    IntPtr local = surfaceObj.GetRawObject();
                    if (local != IntPtr.Zero)
                    {
                        _surfaceGlobalRef = AndroidJNI.NewGlobalRef(local);
                        surfacePtr = _surfaceGlobalRef;
                        surfaceMethod = "jobject_NewGlobalRef_" + surfaceGetter;
                    }
                }
            }
            catch (Exception exception)
            {
                surfaceEx = exception;
            }

            if (surfaceEx != null)
            {
                _meta["surfaceJobjectError"] = surfaceEx.Message;
            }

            if (surfacePtr == IntPtr.Zero)
            {
                Exception nativeEx = null;
                try
                {
                    long nativeHandle = _encoder.Call<long>(nativeGetter);
                    _meta["previewSurfaceNativeHandle"] =
                        nativeHandle.ToString(CultureInfo.InvariantCulture);
                    if (nativeHandle != 0)
                    {
                        surfacePtr = new IntPtr(nativeHandle);
                        surfaceMethod = "reflection_" + nativeGetter;
                    }
                }
                catch (Exception exception)
                {
                    nativeEx = exception;
                }

                if (nativeEx != null)
                {
                    _meta["nativeHandleError"] = nativeEx.Message;
                }
            }

            _meta["surfacePassMethod"] = surfaceMethod;
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : "direct";
            _meta["startPreviewPluginSize"] =
                previewW + "x" + previewH + " passed to native startPreview";
            _meta["encodeSize"] = _width + "x" + _height;
            _meta["previewSize"] = previewW + "x" + previewH;
            _meta["outputCameraRawData"] = _outputRaw;
            _meta["previewEyeMode"] = _eyeMode;
            if (surfacePtr == IntPtr.Zero)
            {
                _meta["previewFailReason"] = "no Surface ptr for StartPreviewfor4U";
                yield break;
            }

            bool previewOk = false;
            Exception previewEx = null;
            PXRCaptureRenderMode renderMode = ParseRenderMode(_eyeMode);
            try
            {
                previewOk = PXR_Enterprise.StartPreviewfor4U(surfacePtr, renderMode, previewW, previewH);
            }
            catch (Exception exception)
            {
                previewEx = exception;
            }

            if (previewEx != null)
            {
                _meta["previewFailReason"] = "StartPreviewfor4U exception: " + previewEx.Message;
                yield break;
            }

            _meta["startPreviewfor4U"] = previewOk;
            _meta["frameCallbackExpected"] =
                "StartPreviewfor4U Surface path has no Frame.timestamp; use video_pts_us * 1000.";
            WriteMeta();
            if (!previewOk)
            {
                _meta["previewFailReason"] = "StartPreviewfor4U returned false";
            }
        }
#else
        private IEnumerator Run()
        {
            yield break;
        }
#endif

        private void OpenTrackingWriter()
        {
            try
            {
                long headerElapsed = Utils.GetElapsedRealtimeNs();
                _trackingWriter = new StreamWriter(_trackingPath, false);
                JsonData header = new JsonData();
                header["type"] = "header";
                header["sessionId"] = _sessionId;
                header["elapsedRealtimeNs"] = headerElapsed;
                header["encodePath"] = EncodePathId;
                header["alignFormula"] = "t_video_ns = video_pts_us * 1000";
                header["clockNote"] =
                    "elapsedRealtimeNs is CLOCK_BOOTTIME. Align with video_pts_us * 1000; " +
                    "PTS is encoder time in the same family, not proven exposure.";
                _trackingWriter.WriteLine(header.ToJson());
                _meta["trackingPath"] = _trackingPath;
                _meta["trackingHeaderElapsedRealtimeNs"] = headerElapsed;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] tracking open failed: " + exception.Message);
                _trackingWriter = null;
            }
        }

        private void Finish(RecordingSessionPhase phase, string reason)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
            _stopRequested = true;
            _phase = RecordingSessionPhase.Stopping;
            SetVisual("stopping");
            SetStatus("Stopping…");

            if (_routine != null && _host != null)
            {
                _host.StopCoroutine(_routine);
                _routine = null;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // Stop producer before encoder EOS (B1: camera; B0: pattern inside encoder.stop).
            if (_cameraOpen)
            {
                try
                {
                    PXR_Enterprise.CloseCamerafor4U();
                    _meta["stopProducer"] = "CloseCamerafor4U";
                }
                catch (Exception exception)
                {
                    _meta["closeCameraError"] = exception.Message;
                    phase = RecordingSessionPhase.FailedPartial;
                }

                _cameraOpen = false;
            }

            try
            {
                if (_encoder != null)
                {
                    // Always stop pattern/crop producers inside Java stop().
                    bool stopProducers = _mode == ModePatternB0 || _useEyeCrop;
                    int stopResult = _encoder.Call<int>("stop", stopProducers);
                    _meta["encoderStopResult"] = stopResult;
                    _meta["encoderStats"] = _encoder.Call<string>("getStatsJson");
                    _meta["encoderStateTimeline"] = _encoder.Call<string>("getStateTimeline");
                    _meta["encoderLastError"] = _encoder.Call<string>("getLastError");
                    _meta["outputSampleCount"] = _encoder.Call<long>("getOutputSampleCount");
                    _meta["patternSubmitted"] = _encoder.Call<long>("getPatternSubmitted");
                    _meta["firstPtsUs"] = _encoder.Call<long>("getFirstPtsUs");
                    _meta["lastPtsUs"] = _encoder.Call<long>("getLastPtsUs");
                    _meta["eosSeen"] = _encoder.Call<bool>("getEosSeen");
                    _meta["muxerStopOk"] = _encoder.Call<bool>("getMuxerStopOk");
                    long submitted = _encoder.Call<long>("getPatternSubmitted");
                    long outputs = _encoder.Call<long>("getOutputSampleCount");
                    int encoderDrops = 0;
                    if (_mode == ModePatternB0 && submitted > 0 && outputs < submitted)
                    {
                        // Soft signal only: codec may legally output fewer access units than canvas posts.
                        encoderDrops = (int)Math.Max(0, submitted - outputs);
                    }

                    CaptureReport.WriteDroppedFrames(_meta, CountPtsGaps(), encoderDrops, 0);
                    WriteMeasuredTiming();
                    if (stopResult != 0)
                    {
                        phase = RecordingSessionPhase.FailedPartial;
                    }
                }
            }
            catch (Exception exception)
            {
                _meta["encoderStopException"] = exception.Message;
                phase = RecordingSessionPhase.FailedPartial;
            }

            ReleaseJavaResources();
#endif

            if (_trackingWriter != null)
            {
                try
                {
                    _trackingWriter.Flush();
                    _trackingWriter.Close();
                }
                catch
                {
                }

                _trackingWriter = null;
            }

            CaptureReport.WriteMp4Result(_meta, _videoPath);
            _meta["eyeMode"] = _eyeMode;
            _meta["width"] = _width;
            _meta["height"] = _height;
            _meta["encodeSize"] = _width + "x" + _height;
            _meta["previewSize"] = _previewWidth + "x" + _previewHeight;
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : "direct";
            _meta["cropHalf"] = !_useEyeCrop ? "none" :
                (_eyeMode == "RIGHT" ? "right" : "left");
            _meta["requestedFps"] = _fps;
            _meta["requestedBitrateBps"] = _requestedBitrateBps;
            _meta["bitrateBps"] = _bitrateBps;
            _meta["raw"] = _outputRaw;
            _meta["eis"] = _enableEis;
            _meta["mctf"] = _enableMctf;
            _meta["mfnr"] = _enableMfnr;
            _meta["trackingSamples"] = _trackingSamples;
            if (_trackingFirstElapsedRealtimeNs > 0)
            {
                _meta["trackingFirstElapsedRealtimeNs"] = _trackingFirstElapsedRealtimeNs;
            }

            if (_trackingLastElapsedRealtimeNs > 0)
            {
                _meta["trackingLastElapsedRealtimeNs"] = _trackingLastElapsedRealtimeNs;
            }

            WriteClockSemantics();
            _meta["completedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["reason"] = reason ?? "";
            _meta["status"] = phase == RecordingSessionPhase.Completed ? "completed" :
                phase == RecordingSessionPhase.FailedPartial ? "failed_partial" : "failed";
            _meta["alignHint"] =
                "t_video_ns = video_pts_us * 1000 vs tracking elapsedRealtimeNs. " +
                "Same clock family does not prove exposure time.";
            WriteMeta();
            _phase = RecordingSessionPhase.Idle;
            SetVisual("idle");
            SetStatus(_meta["status"].ToString());
            LogWindow.Info("[SurfaceEncodeProbe] " + _meta["status"] + " " + reason + " session=" + _sessionId);
        }

        private int CountPtsGaps()
        {
            if (string.IsNullOrEmpty(_frameMapPath) || !File.Exists(_frameMapPath))
            {
                return 0;
            }

            try
            {
                long prev = -1;
                int drops = 0;
                long expectedUs = (long)(1e6 / Math.Max(1, _fps));
                long threshold = (long)(expectedUs * 1.8);
                foreach (string line in File.ReadLines(_frameMapPath))
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    JsonData row = JsonMapper.ToObject(line);
                    if (row == null || !row.ContainsKey("video_pts_us"))
                    {
                        continue;
                    }

                    long pts = long.Parse(row["video_pts_us"].ToString(), CultureInfo.InvariantCulture);
                    if (prev >= 0 && pts - prev > threshold)
                    {
                        drops++;
                    }

                    prev = pts;
                }

                return drops;
            }
            catch
            {
                return 0;
            }
        }

        private void WriteMeasuredTiming()
        {
            if (_meta == null)
            {
                return;
            }

            long first = ReadMetaLong("firstPtsUs", -1);
            long last = ReadMetaLong("lastPtsUs", -1);
            long samples = ReadMetaLong("outputSampleCount", 0);
            if (first >= 0 && last > first && samples > 1)
            {
                double spanSec = (last - first) / 1e6;
                _meta["durationSec"] = spanSec.ToString("R", CultureInfo.InvariantCulture);
                _meta["measuredFps"] = ((samples - 1) / spanSec).ToString("R", CultureInfo.InvariantCulture);
            }
            else if (_meta.ContainsKey("mp4") && _meta["mp4"] != null && _meta["mp4"].IsObject &&
                     _meta["mp4"].ContainsKey("durationSec"))
            {
                _meta["durationSec"] = _meta["mp4"]["durationSec"];
            }
        }

        private long ReadMetaLong(string key, long fallback)
        {
            if (_meta == null || !_meta.ContainsKey(key) || _meta[key] == null)
            {
                return fallback;
            }

            long value;
            return long.TryParse(_meta[key].ToString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        private void WriteClockSemantics()
        {
            JsonData clocks = new JsonData();
            clocks["video_pts_us"] =
                "MediaCodec presentationTimeUs. On this device family it tracks CLOCK_BOOTTIME in microseconds. " +
                "Use t_video_ns = video_pts_us * 1000. Not proven exposure time.";
            clocks["elapsedRealtimeNs"] =
                "Device CLOCK_BOOTTIME nanoseconds for tracking samples.";
            clocks["host_elapsedRealtimeNs_at_output"] =
                "App dequeue time for an encoded sample. Includes encode delay; not capture time.";
            clocks["sdk_timestamp_ns"] =
                "Absent on StartPreviewfor4U Surface path.";
            clocks["align"] =
                "Compare video_pts_us * 1000 with tracking elapsedRealtimeNs. " +
                "Clock agreement does not complete exposure sync research.";
            _meta["clockSemantics"] = clocks;
        }

        private void WriteMeta()
        {
            try
            {
                if (!Directory.Exists(VisionTrackingSession.DownloadDir))
                {
                    Directory.CreateDirectory(VisionTrackingSession.DownloadDir);
                }

                File.WriteAllText(_metaPath, _meta.ToJson());
            }
            catch (Exception exception)
            {
                Debug.LogError("[SurfaceEncodeProbe] meta write failed: " + exception.Message);
            }
        }

        private void SetVisual(string phase)
        {
            if (_host != null)
            {
                _host.SetRecordVisual(phase);
            }
        }

        private void SetStatus(string text)
        {
            if (_host != null)
            {
                _host.SetRecordStatus(text);
            }
        }
    }
}
