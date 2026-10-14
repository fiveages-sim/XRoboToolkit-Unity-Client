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
    /// Encode-path probe: B0 pattern→SurfaceVideoEncoder, B1 StartPreviewfor4U→encoder Surface.
    /// Does not modify ApiFrameSession. Enable via PlayerPrefs SurfaceEncodeProbeMode=1|2
    /// or call BeginPattern / BeginCamera directly.
    /// </summary>
    public sealed class SurfaceEncodeProbe
    {
        public const string PrefsModeKey = "SurfaceEncodeProbeMode";
        public const int ModeOff = 0;
        public const int ModePatternB0 = 1;
        public const int ModeCameraB1 = 2;

        public const int ProbeWidth = 1024;
        public const int ProbeHeight = 1024;
        public const int DefaultBitrateBps = 8 * 1000 * 1000;
        public const string EncodePathId = "for4u-surface-mediacodec";

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
        private int _trackingSamples;
        private long _videoStartElapsedRealtimeNs;
        private double _videoStartPredictTimeUs;
        private string _videoStartUtc;
        private long _lastDiskCheckMs;
        private AndroidJavaObject _encoder;
        private AndroidJavaObject _inputSurfaceJava;
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
        /// Probe mode is PlayerPrefs-only so leftover Download marker files cannot hijack
        /// Vision / for4U (exp_Image) recording. Enable explicitly:
        /// SurfaceEncodeProbe.PrefsMode = 1 (B0) or 2 (B1); set 0 to disable.
        /// </summary>
        public static int ResolveMode()
        {
            int prefs = PrefsMode;
            if (prefs == ModePatternB0 || prefs == ModeCameraB1)
            {
                return prefs;
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
            Begin(host, ModePatternB0, fps, DefaultBitrateBps, tracking);
        }

        public void BeginCamera(UICameraCtrl host, int fps, bool tracking)
        {
            Begin(host, ModeCameraB1, fps, DefaultBitrateBps, tracking);
        }

        public void Begin(UICameraCtrl host, int mode, int fps, int bitrateBps, bool tracking)
        {
            if (IsActive || host == null)
            {
                return;
            }

            if (mode != ModePatternB0 && mode != ModeCameraB1)
            {
                Debug.LogError("[SurfaceEncodeProbe] mode must be 1 (B0) or 2 (B1)");
                return;
            }

            _host = host;
            _mode = mode;
            _fps = Mathf.Clamp(fps, 1, 60);
            _bitrateBps = bitrateBps > 0 ? bitrateBps : DefaultBitrateBps;
            _trackingEnabled = tracking;
            ReleaseJavaResources();
            _finalized = false;
            _stopRequested = false;
            _cameraOpen = false;
            _trackingSamples = 0;
            _phase = RecordingSessionPhase.Starting;
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string download = VisionTrackingSession.DownloadDir;
            if (File.Exists(Path.Combine(download, "recording_meta_" + _sessionId + ".json")))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            string prefix = mode == ModePatternB0 ? "surf_b0_" : "surf_b1_";
            _videoPath = Path.Combine(download, prefix + "CameraRecord_" + _sessionId + ".mp4");
            _frameMapPath = Path.Combine(download, prefix + "frame_map_" + _sessionId + ".jsonl");
            _metaPath = Path.Combine(download, "recording_meta_" + _sessionId + ".json");
            _trackingPath = Path.Combine(download, "trackingData_" + _sessionId + ".txt");

            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["exp_Image"] = -1;
            _meta["saveType"] = "surface-encode-probe";
            _meta["probePhase"] = mode == ModePatternB0 ? "B0-pattern" : "B1-camera";
            _meta["status"] = "starting";
            _meta["requestUtc"] = DateTime.UtcNow.ToString("o");
            _meta["videoPath"] = _videoPath;
            _meta["frameMapPath"] = _frameMapPath;
            _meta["frameMapSemantics"] =
                "video_pts_us=encoder PTS; host_elapsedRealtimeNs_at_output=dequeue time NOT capture; " +
                "sdk_timestamp_ns absent unless separately proven.";

            string strategy = mode == ModePatternB0
                ? "mp4_pts_plus_start_anchor"
                : "mp4_pts_plus_start_anchor";
            CaptureReport.WriteEncodeTimestampContract(_meta, EncodePathId, strategy, "LEFT",
                ProbeWidth, ProbeHeight, _fps, _bitrateBps, "avc",
                eis: false, mctf: false, mfnr: false, raw: false, capturePathId: EncodePathId);
            _meta["timestampStrategyNote"] =
                "Surface path has no Frame.timestamp by default. Do not treat host_at_output as capture time.";

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

            long now = (long)(Time.realtimeSinceStartup * 1000.0);
            if (now - _lastDiskCheckMs < 30000)
            {
                return;
            }

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

                _trackingData.Get(ref _trackingJson);
                _trackingJson["elapsedRealtimeNs"] = Utils.GetElapsedRealtimeNs();
                _trackingJson["sessionId"] = _sessionId;
                _trackingWriter.WriteLine(_trackingJson.ToJson());
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
            SetStatus("probe starting");
            string failReason = null;

            int startResult = -99;
            Exception startEx = null;
            try
            {
                _encoder = new AndroidJavaObject("com.xrobotoolkit.enterprise.SurfaceVideoEncoder");
                startResult = _encoder.Call<int>("start", _videoPath, _frameMapPath, ProbeWidth,
                    ProbeHeight, _fps, _bitrateBps);
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
                SetStatus(_mode == ModePatternB0 ? "B0 pattern recording" : "B1 camera surface recording");
                LogWindow.Info("[SurfaceEncodeProbe] recording " + (_mode == ModePatternB0 ? "B0" : "B1") +
                               " session=" + _sessionId);

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
                    { PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA, PXRCapture.VALUE_FALSE },
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
                { PXRCapture.KEY_EIS, PXRCapture.VALUE_FALSE },
                { PXRCapture.KEY_MCTF, PXRCapture.VALUE_FALSE },
                { PXRCapture.KEY_MFNR, PXRCapture.VALUE_FALSE },
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

            // Prefer Java Surface jobject (NewGlobalRef). Reflection getNativeObject can SIGSEGV.
            IntPtr surfacePtr = IntPtr.Zero;
            string surfaceMethod = "none";
            Exception surfaceEx = null;
            try
            {
                _inputSurfaceJava = _encoder.Call<AndroidJavaObject>("getInputSurface");
                if (_inputSurfaceJava != null)
                {
                    IntPtr local = _inputSurfaceJava.GetRawObject();
                    if (local != IntPtr.Zero)
                    {
                        _surfaceGlobalRef = AndroidJNI.NewGlobalRef(local);
                        surfacePtr = _surfaceGlobalRef;
                        surfaceMethod = "jobject_NewGlobalRef";
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
                    long nativeHandle = _encoder.Call<long>("getInputSurfaceNativeHandle");
                    _meta["inputSurfaceNativeHandle"] =
                        nativeHandle.ToString(CultureInfo.InvariantCulture);
                    if (nativeHandle != 0)
                    {
                        surfacePtr = new IntPtr(nativeHandle);
                        surfaceMethod = "reflection_getNativeObject_fallback";
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
            _meta["startPreviewPluginSize"] =
                "1024x1024 hardcoded in PXR_EnterprisePlugin.StartPreview";
            if (surfacePtr == IntPtr.Zero)
            {
                _meta["previewFailReason"] = "no Surface ptr for StartPreviewfor4U";
                yield break;
            }

            bool previewOk = false;
            Exception previewEx = null;
            try
            {
                previewOk = PXR_Enterprise.StartPreviewfor4U(surfacePtr,
                    PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT);
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
                "StartPreviewfor4U Surface path typically has no Frame.timestamp callbacks.";
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
                _trackingWriter = new StreamWriter(_trackingPath, false);
                _trackingWriter.WriteLine("{\"type\":\"header\",\"sessionId\":\"" + _sessionId +
                                          "\",\"elapsedRealtimeNs\":" + Utils.GetElapsedRealtimeNs() +
                                          ",\"encodePath\":\"" + EncodePathId + "\"}");
                _meta["trackingPath"] = _trackingPath;
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
            SetStatus("probe stopping");

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
                    bool stopPattern = _mode == ModePatternB0;
                    int stopResult = _encoder.Call<int>("stop", stopPattern);
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

                    CaptureReport.WriteDroppedFrames(_meta, 0, encoderDrops, 0);
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
            _meta["trackingSamples"] = _trackingSamples;
            _meta["completedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["reason"] = reason ?? "";
            _meta["status"] = phase == RecordingSessionPhase.Completed ? "completed" :
                phase == RecordingSessionPhase.FailedPartial ? "failed_partial" : "failed";
            _meta["g1Hints"] =
                "Check mp4 present, outputSampleCount vs duration*fps, muxerStopOk, non-black visual review.";
            _meta["g2Hints"] =
                "Compare frame_map video_pts_us + videoStartElapsedRealtimeNs to tracking elapsedRealtimeNs. " +
                "host_elapsedRealtimeNs_at_output is NOT capture time. Without sdk_timestamp_ns this is indirect sync only.";
            WriteMeta();
            _phase = RecordingSessionPhase.Idle;
            SetVisual("idle");
            SetStatus(_meta["status"].ToString());
            LogWindow.Info("[SurfaceEncodeProbe] " + _meta["status"] + " " + reason + " session=" + _sessionId);
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
