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
    /// Production path: BeginCamera with panel eye/fps/flags. B0 pattern is Editor/Development only.
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
        private string _sessionDir;
        private string _videoPath;
        private string _frameMapPath;
        private string _trackingPath;
        private CollectionSessionWriter _sessionWriter;
        private TrackingConfigSnapshot _trackingSnapshot;
        private TrackingData _trackingData;
        private JsonData _trackingJson;
        private long _headSeq;
        private long _controllerSeq;
        private long _handSeq;
        private long _bodySeq;
        private long _motionSeq;
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
        private bool _expCrop = true;
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
        /// B0 pattern hijack is disabled in production. Prefer BeginPattern() explicitly
        /// under DEVELOPMENT_BUILD / Editor when needed.
        /// </summary>
        public static int ResolveMode()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (PrefsMode == ModePatternB0)
            {
                return ModePatternB0;
            }
#endif
            return ModeOff;
        }

        /// <summary>Deletes legacy Download marker files that used to force probe mode.</summary>
        public static void ClearLegacyMarkers()
        {
            try
            {
                if (PrefsMode != ModeOff)
                {
                    PrefsMode = ModeOff;
                }

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
                fps, DefaultBitrateBps, tracking, false, false, false, false, true);
        }

        public void BeginCamera(UICameraCtrl host, string eyeMode, int width, int height, int fps,
            int bitrateBps, bool tracking, bool outputRaw, bool enableEis, bool enableMctf,
            bool enableMfnr, int previewWidth = EyeWidth, int previewHeight = EyeHeight,
            bool expCrop = true)
        {
            Begin(host, ModeCameraB1, eyeMode, width, height, previewWidth, previewHeight, fps,
                bitrateBps, tracking, outputRaw, enableEis, enableMctf, enableMfnr, expCrop);
        }

        public void Begin(UICameraCtrl host, int mode, string eyeMode, int width, int height,
            int previewWidth, int previewHeight, int fps, int bitrateBps, bool tracking,
            bool outputRaw, bool enableEis, bool enableMctf, bool enableMfnr, bool expCrop = true)
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
            _expCrop = expCrop;
            if (mode == ModeCameraB1)
            {
                int encodeW;
                int encodeH;
                RecordDialog.ResolveEncodeSize(_eyeMode, outputRaw, expCrop, out encodeW, out encodeH);
                _width = encodeW;
                _height = encodeH;
                // Processed (raw=0) fills the requested eye buffer (direct).
                // Raw paints one eye into half of a SBS Surface (other half black).
                // exp_crop=1: GLES half → 2048 encode. exp_crop=0: encode full 4096 SBS.
                bool rawMono = outputRaw && _eyeMode != "3D";
                _useEyeCrop = rawMono && expCrop;
                if (rawMono)
                {
                    _previewWidth = SbsWidth;
                    _previewHeight = EyeHeight;
                    if (!_useEyeCrop)
                    {
                        _width = SbsWidth;
                        _height = EyeHeight;
                    }
                }
                else
                {
                    _previewWidth = _width;
                    _previewHeight = _height;
                }
            }
            else
            {
                _width = ProbeWidth;
                _height = ProbeHeight;
                _previewWidth = ProbeWidth;
                _previewHeight = ProbeHeight;
                _eyeMode = "LEFT";
                _useEyeCrop = false;
                _expCrop = false;
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
            string root = Path.Combine(download, CollectionSessionWriter.RootFolderName);
            if (Directory.Exists(Path.Combine(root, "session_" + _sessionId)))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            _trackingSnapshot = TrackingConfigSnapshot.CaptureFromPanel();
            if (!_trackingEnabled)
            {
                _trackingSnapshot.Head = false;
                _trackingSnapshot.Controller = false;
                _trackingSnapshot.Hand = false;
                _trackingSnapshot.FullBody = false;
                _trackingSnapshot.MotionTracker = false;
            }

            _sessionWriter = new CollectionSessionWriter(true, download);
            _sessionWriter.Start(
                _trackingSnapshot,
                EnterpriseCollectionRecorder.SharedEnterpriseSampleHz,
                90,
                true,
                SystemInfo.deviceModel,
                Utils.IsPico4U(),
                "session_" + _sessionId);
            _sessionDir = _sessionWriter.SessionDir;
            _videoPath = Path.Combine(_sessionDir, CollectionSessionWriter.VideoFile);
            _frameMapPath = Path.Combine(_sessionDir, CollectionSessionWriter.FrameMapFile);
            _trackingPath = _sessionDir;
            _headSeq = 0;
            _controllerSeq = 0;
            _handSeq = 0;
            _bodySeq = 0;
            _motionSeq = 0;

            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["sessionDir"] = _sessionDir;
            _meta["session_id"] = _sessionWriter.SessionId;
            _meta["exp_Image"] = mode == ModeCameraB1 ? 1 : -1;
            _meta["saveType"] = mode == ModePatternB0 ? "surface-encode-probe" : "for4u-surface-mp4";
            _meta["probePhase"] = mode == ModePatternB0 ? "B0-pattern" : "for4u-surface";
            _meta["status"] = "starting";
            _meta["requestUtc"] = DateTime.UtcNow.ToString("o");
            _meta["videoPath"] = _videoPath;
            _meta["frameMapPath"] = _frameMapPath;
            _meta["tracking_config_at_start"] = _trackingSnapshot.ToJson();
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
            _meta["exp_crop"] = _expCrop ? 1 : 0;
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : (_outputRaw && _eyeMode != "3D" ?
                "raw-sbs-black" : "direct");
            _meta["cropHalf"] = !_useEyeCrop ? "none" :
                (_eyeMode == "RIGHT" ? "right" : "left");
            if (_eyeMode == "3D")
            {
                _meta["layout"] = "3D SBS " + _width + "x" + _height +
                                  " (8:3, each eye 4:3); direct Surface";
            }
            else if (_useEyeCrop)
            {
                _meta["layout"] = _eyeMode + " raw SBS half " + _previewWidth + "x" + _previewHeight +
                                  " cropped to " + _width + "x" + _height + " (4:3)";
            }
            else if (_outputRaw)
            {
                _meta["layout"] = _eyeMode + " raw full SBS " + _width + "x" + _height +
                                  " (half content + black; exp_crop=0)";
            }
            else
            {
                _meta["layout"] = _eyeMode + " full-eye " + _width + "x" + _height +
                                  " (4:3); direct StartPreview";
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
            _sessionWriter.SetDiskBudget(diskOk, free, required);
            _sessionWriter.SetVideoInfo(false, _videoPath, 0, mode == ModePatternB0 ? "B0-pattern" : "for4u-surface");
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

            if (_trackingEnabled && _sessionWriter != null && _phase == RecordingSessionPhase.Recording)
            {
                WriteTrackingSample();
            }
        }

        public void WriteTrackingSample()
        {
            if (!_trackingEnabled || _sessionWriter == null || _phase != RecordingSessionPhase.Recording)
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
                if (_trackingSamples == 0)
                {
                    _trackingFirstElapsedRealtimeNs = elapsed;
                }

                _trackingLastElapsedRealtimeNs = elapsed;
                _trackingSamples++;

                if (_trackingSnapshot.Head)
                {
                    WriteSourceSample("head", "head_pose", ref _headSeq,
                        _trackingJson.ContainsKey("Head") ? _trackingJson["Head"] : null);
                }

                if (_trackingSnapshot.Controller)
                {
                    WriteSourceSample("controller", "controller_pose", ref _controllerSeq,
                        _trackingJson.ContainsKey("Controller") ? _trackingJson["Controller"] : null);
                }

                if (_trackingSnapshot.Hand)
                {
                    WriteSourceSample("hand", "hand_pose", ref _handSeq,
                        _trackingJson.ContainsKey("Hand") ? _trackingJson["Hand"] : null);
                }

                if (_trackingSnapshot.FullBody)
                {
                    WriteSourceSample("full_body", "body_pose", ref _bodySeq,
                        _trackingJson.ContainsKey("Body") ? _trackingJson["Body"] : null);
                }

                if (_trackingSnapshot.MotionTracker)
                {
                    WriteSourceSample("motion_tracker", "motion_tracker", ref _motionSeq,
                        _trackingJson.ContainsKey("Motion") ? _trackingJson["Motion"] : null);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] tracking write failed: " + exception.Message);
            }
        }

        private void WriteSourceSample(string sourceKey, string kind, ref long seq, JsonData data)
        {
            bool success = data != null;
            JsonData envelope = new JsonData();
            envelope["source"] = "for4u";
            envelope["kind"] = kind;
            envelope["seq"] = ++seq;
            envelope["captureUnixTimeMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            envelope["elapsedRealtimeNs"] = Utils.GetElapsedRealtimeNs();
            envelope["success"] = success;
            if (success)
            {
                envelope["data"] = data;
            }
            else
            {
                envelope["error"] = kind + " missing";
            }

            _sessionWriter.Enqueue(sourceKey, envelope.ToJson(), success);
        }

        private void SetStartPhase(string phase)
        {
            if (_meta == null)
            {
                return;
            }

            _meta["startPhase"] = phase ?? "";
            WriteMeta();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator Run()
        {
            SetStatus("Starting…");
            string failReason = null;
            // Cooperative start budget (Pump also ForceResets at StartTimeoutSeconds).
            float startDeadline = _startedRealtime + StartTimeoutSeconds;

            SetStartPhase("encoder_async");
            int startResult = 1; // 1=pending sentinel until poll completes
            Exception startEx = null;
            int accepted = -99;
            // Iterators cannot yield inside try/catch — keep Call and yield separated.
            try
            {
                _encoder = new AndroidJavaObject("com.xrobotoolkit.enterprise.SurfaceVideoEncoder");
                accepted = _encoder.Call<int>("startAsync", _videoPath, _frameMapPath, _width,
                    _height, _fps, _bitrateBps);
            }
            catch (Exception exception)
            {
                startEx = exception;
            }

            if (startEx == null && accepted != 0)
            {
                startResult = accepted;
            }
            else if (startEx == null)
            {
                while (!_finalized && !_stopRequested && Time.realtimeSinceStartup < startDeadline)
                {
                    int poll = 1;
                    Exception pollEx = null;
                    try
                    {
                        poll = _encoder.Call<int>("pollStartResult");
                    }
                    catch (Exception exception)
                    {
                        pollEx = exception;
                    }

                    if (pollEx != null)
                    {
                        startEx = pollEx;
                        break;
                    }

                    if (poll == 1)
                    {
                        yield return null;
                        continue;
                    }

                    startResult = poll;
                    break;
                }
            }

            if (failReason == null && startEx != null)
            {
                failReason = "encoder start exception: " + startEx.Message;
            }
            else if (failReason == null && startResult == 1)
            {
                failReason = "encoder startAsync timeout";
                if (_meta != null)
                {
                    _meta["startTimeoutSec"] = StartTimeoutSeconds;
                }
            }
            else if (failReason == null && startResult != 0)
            {
                failReason = "encoder start returned " + startResult +
                             " (rebuild APK if class SurfaceVideoEncoder missing)";
            }

            if (failReason == null)
            {
                SetStartPhase("encoder_ready");
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
                    SetStartPhase("pattern");
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
                    yield return OpenCameraAndPreview(startDeadline);
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
                SetStartPhase("recording");
                SetVisual("recording");
                SetStatus(_mode == ModePatternB0 ? "B0 recording" : "Recording");
                LogWindow.Info("[SurfaceEncodeProbe] recording " +
                               (_mode == ModePatternB0 ? "B0" : "for4U Surface") +
                               " " + _eyeMode + " " + _width + "x" + _height + "@" + _fps +
                               " session=" + _sessionId);
                Toast.Show("Recording started");
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

        private IEnumerator OpenCameraAndPreview(float startDeadline)
        {
            // Note: iterators cannot yield inside try/catch. Keep yields outside catch blocks.
            SetStartPhase("configure_open");
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
            while (!openDone && !_stopRequested && !_finalized
                   && Time.realtimeSinceStartup < startDeadline)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            _meta["openCameraOk"] = openOk;
            _meta["openCameraWaitSec"] = waited.ToString("R", CultureInfo.InvariantCulture);
            if (!openDone || !openOk)
            {
                _meta["previewFailReason"] = openDone
                    ? "OpenCameraAsyncfor4U returned false"
                    : "OpenCameraAsyncfor4U timed out";
                yield break;
            }

            _cameraOpen = true;
            AttachCalibrationAndTransmit();

            // Raw mono always previews on SBS canvas; encode may be half (crop) or full SBS.
            int previewW = (_outputRaw && _eyeMode != "3D") ? _previewWidth : _width;
            int previewH = (_outputRaw && _eyeMode != "3D") ? _previewHeight : _height;
            if (_useEyeCrop)
            {
                SetStartPhase("camera_surface");
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

                int surfacePoll = 1;
                string surfacePollError = null;
                while (!_finalized && !_stopRequested && Time.realtimeSinceStartup < startDeadline
                       && surfacePollError == null)
                {
                    Exception pollEx = null;
                    try
                    {
                        surfacePoll = _encoder.Call<int>("pollCameraSurface");
                    }
                    catch (Exception exception)
                    {
                        pollEx = exception;
                    }

                    if (pollEx != null)
                    {
                        surfacePollError = pollEx.Message;
                        break;
                    }

                    if (surfacePoll == 1)
                    {
                        yield return null;
                        continue;
                    }

                    break;
                }

                if (surfacePollError != null)
                {
                    _meta["previewFailReason"] = "pollCameraSurface: " + surfacePollError;
                    yield break;
                }

                _meta["pollCameraSurfaceResult"] = surfacePoll;
                if (surfacePoll == 1)
                {
                    _meta["previewFailReason"] = "createCameraSurface poll timeout";
                    yield break;
                }

                if (surfacePoll != 0)
                {
                    _meta["previewFailReason"] = "createCameraSurface poll returned " + surfacePoll;
                    yield break;
                }

                SetStartPhase("eye_crop");
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
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : (_outputRaw && _eyeMode != "3D" ?
                "raw-sbs-black" : "direct");
            _meta["exp_crop"] = _expCrop ? 1 : 0;
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

            SetStartPhase("start_preview");
            bool previewOk = false;
            Exception previewEx = null;
            PXRCaptureRenderMode renderMode = ParseRenderMode(_eyeMode);
            try
            {
                // Keep StartPreviewfor4U on Unity main thread (PICO API affinity).
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
            if (!previewOk)
            {
                _meta["previewFailReason"] = "StartPreviewfor4U returned false";
            }
            else
            {
                SetStartPhase("preview_ok");
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
                _meta["trackingPath"] = _sessionDir;
                _meta["trackingHeaderElapsedRealtimeNs"] = headerElapsed;
                _meta["alignFormula"] = "t_video_ns = video_pts_us * 1000";
                _meta["clockNote"] =
                    "elapsedRealtimeNs is CLOCK_BOOTTIME. Align with video_pts_us * 1000; " +
                    "PTS is encoder time in the same family, not proven exposure.";
                WriteMeta();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[SurfaceEncodeProbe] tracking open failed: " + exception.Message);
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
            // Stop crop/pattern consumers before CloseCamerafor4U so Java updateTexImage
            // is not racing a torn-down SurfaceTexture. Then close camera, then EOS/muxer.
            if (_encoder != null)
            {
                try
                {
                    if (_useEyeCrop)
                    {
                        _encoder.Call("stopEyeCropProducer");
                        _meta["stopEyeCropFirst"] = true;
                    }

                    if (_mode == ModePatternB0)
                    {
                        _encoder.Call("stopPatternProducer");
                        _meta["stopPatternFirst"] = true;
                    }
                }
                catch (Exception exception)
                {
                    _meta["stopProducerEarlyError"] = exception.Message;
                }
            }

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
                    // Producers already stopped above; stop() still drains EOS / finalizes muxer.
                    int stopResult = _encoder.Call<int>("stop", false);
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

            CaptureReport.WriteMp4Result(_meta, _videoPath);
            _meta["eyeMode"] = _eyeMode;
            _meta["width"] = _width;
            _meta["height"] = _height;
            _meta["encodeSize"] = _width + "x" + _height;
            _meta["previewSize"] = _previewWidth + "x" + _previewHeight;
            _meta["exp_crop"] = _expCrop ? 1 : 0;
            _meta["surfacePath"] = _useEyeCrop ? "eye-crop" : (_outputRaw && _eyeMode != "3D" ?
                "raw-sbs-black" : "direct");
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
            FinalizeSessionWriter(phase, reason);
            _phase = RecordingSessionPhase.Idle;
            SetVisual("idle");
            string userStatus = phase == RecordingSessionPhase.Completed ? "Done" :
                phase == RecordingSessionPhase.FailedPartial ? "Partial" : "Failed";
            SetStatus(userStatus);
            long savedBytes = 0;
            try
            {
                if (!string.IsNullOrEmpty(_videoPath) && File.Exists(_videoPath))
                {
                    savedBytes = new FileInfo(_videoPath).Length;
                }
            }
            catch
            {
            }

            if (phase == RecordingSessionPhase.Completed)
            {
                Toast.Show("Saved " + FormatBytes(savedBytes));
            }
            else
            {
                Toast.Show(ShortUserReason(reason));
            }

            LogWindow.Info("[SurfaceEncodeProbe] " + _meta["status"] + " " + reason +
                           " session=" + (_sessionDir ?? _sessionId));
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024)
            {
                return bytes + " B";
            }

            if (bytes < 1024 * 1024)
            {
                return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            return (bytes / (1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
        }

        private static string ShortUserReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "Record failed";
            }

            if (reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Record timeout";
            }

            if (reason.IndexOf("OpenCamera", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Camera open failed";
            }

            if (reason.Length > 42)
            {
                return reason.Substring(0, 41) + "…";
            }

            return reason;
        }

        private void FinalizeSessionWriter(RecordingSessionPhase phase, string reason)
        {
            if (_sessionWriter == null)
            {
                return;
            }

            long videoBytes = 0;
            try
            {
                if (!string.IsNullOrEmpty(_videoPath) && File.Exists(_videoPath))
                {
                    videoBytes = new FileInfo(_videoPath).Length;
                }
            }
            catch
            {
            }

            bool frameMapOk = !string.IsNullOrEmpty(_frameMapPath) && File.Exists(_frameMapPath);
            _sessionWriter.SetSourceAvailable("frame_map", frameMapOk,
                frameMapOk ? "" : "frame_map missing");
            _sessionWriter.SetVideoInfo(videoBytes > 0, _videoPath, videoBytes,
                _mode == ModePatternB0 ? "B0-pattern" : "for4u-surface");
            _sessionWriter.SetCaptureExtra(_meta);
            string status = phase == RecordingSessionPhase.Completed ? "completed" :
                phase == RecordingSessionPhase.FailedPartial ? "failed" : "failed";
            string interrupt = phase == RecordingSessionPhase.Completed ? "none" : "exception";
            if (reason != null && reason.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status = "aborted_disk";
                interrupt = "disk";
            }

            _sessionWriter.Stop(status, reason ?? "", interrupt, reason ?? "", 2000);
            _sessionWriter = null;
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

        /// <summary>
        /// Query for4U fx/fy/cx/cy + extrinsics into session meta/files (temporary transmit for A/B).
        /// Per-eye size 2048×1536 is the calibration domain; no k1/k2 in SDK.
        /// </summary>
        private void AttachCalibrationAndTransmit()
        {
            int calibW = EyeWidth;
            int calibH = EyeHeight;
            try
            {
                JsonData calib = CaptureReport.CalibrationNote(calibW, calibH, true);
                calib["encodeWidth"] = _width;
                calib["encodeHeight"] = _height;
                calib["previewWidth"] = _previewWidth;
                calib["previewHeight"] = _previewHeight;
                calib["exp_crop"] = _expCrop ? 1 : 0;
                calib["raw"] = _outputRaw;
                calib["eyeMode"] = _eyeMode;
                calib["fovNote"] =
                    "GetCameraIntrinsicsfor4U also available with h_fov=76.35 v_fov=61.05 " +
                    "(Utils.GetCameraIntrinsicsStrE). RGBCameraParamsNew has no distortion coeffs.";
                _meta["calibration"] = calib;

                string intrinsicsE = Utils.GetCameraIntrinsicsStrE(calibW, calibH);
                string extrinsicsE = Utils.GetCameraExtrinsicsStrE();
                _meta["cameraIntrinsicsE"] = intrinsicsE ?? "";
                _meta["cameraExtrinsicsE"] = extrinsicsE ?? "";

                if (!string.IsNullOrEmpty(_sessionDir))
                {
                    string calibPath = Path.Combine(_sessionDir, "camera_calib.json");
                    File.WriteAllText(calibPath, calib.ToJson());
                    string bundle = "CameraExtrinsics:" + extrinsicsE + "\n" +
                                   "cameraIntrinsics:" + intrinsicsE + "\n" +
                                   "applicableWidth:" + calibW + "\n" +
                                   "applicableHeight:" + calibH + "\n" +
                                   "encodeSize:" + _width + "x" + _height + "\n" +
                                   "exp_crop:" + (_expCrop ? 1 : 0) + "\n" +
                                   "raw:" + (_outputRaw ? 1 : 0) + "\n" +
                                   "eyeMode:" + _eyeMode + "\n";
                    File.WriteAllText(Path.Combine(_sessionDir, "camera_params.txt"), bundle);
                    // Temporary transmit copy under persistentDataPath for adb / PC pull.
                    try
                    {
                        string persist = Path.Combine(Application.persistentDataPath,
                            "camera_params_last.txt");
                        File.WriteAllText(persist, bundle);
                        _meta["cameraParamsPersistPath"] = persist;
                    }
                    catch (Exception persistEx)
                    {
                        _meta["cameraParamsPersistError"] = persistEx.Message;
                    }

                    _meta["cameraCalibPath"] = calibPath;
                }

                WriteMeta();
                LogWindow.Info("[SurfaceEncodeProbe] calib fx/fy/cx/cy + extrinsics written " +
                               "encode=" + _width + "x" + _height +
                               " exp_crop=" + (_expCrop ? 1 : 0));
            }
            catch (Exception exception)
            {
                _meta["calibrationError"] = exception.Message;
                Debug.LogWarning("[SurfaceEncodeProbe] calib: " + exception.Message);
            }
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
                if (_sessionWriter == null)
                {
                    return;
                }

                _sessionWriter.SetCaptureExtra(_meta);
                _sessionWriter.FlushMeta();
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
