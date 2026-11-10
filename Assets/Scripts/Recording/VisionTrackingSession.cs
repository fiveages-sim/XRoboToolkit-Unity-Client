using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.Android;

namespace Robot
{
    public enum RecordingSessionPhase
    {
        Idle,
        Starting,
        CameraReady,
        Recording,
        Stopping,
        Completed,
        Failed,
        FailedPartial
    }

    /// <summary>
    /// One Vision / Tracking / Vision+Tracking capture session.
    /// Recording is confirmed only after the requested streams start.
    /// The session is successful only after Stop validates the files.
    /// </summary>
    public class VisionTrackingSession
    {
        public const string DownloadDir = "/sdcard/Download";
        public const float StartTimeoutSeconds = 12f;
        public const float RecoveryDelaySeconds = 2f;
        public const float StopTimeoutSeconds = 8f;

        public RecordingSessionPhase Phase { get; private set; } = RecordingSessionPhase.Idle;
        public bool TrackingWriteEnabled { get; set; }
        public string SessionId { get; private set; } = "-";

        private UICameraCtrl _host;
        private Coroutine _routine;
        private Coroutine _stopRoutine;
        private bool _vision;
        private bool _tracking;
        private bool _acceptStart;
        private bool _finalized;
        private float _stoppingSince = -1f;
        private bool _videoStarted;
        private bool _trackingStarted;
        private bool _recoveryUsed;
        private bool _seenProgress;
        private int _width;
        private int _height;
        private int _fps;
        private int _bitrate;
        private string _videoPath;
        private string _trackingPath;
        private string _sessionDir;
        private string _pendingFail;
        private CollectionSessionWriter _sessionWriter;
        private TrackingConfigSnapshot _trackingSnapshot;
        private int _trackingLines;
        private int _trackingSamples;
        private int _anomalyCount;
        private long _visionHeadSeq;
        private long _visionControllerSeq;
        private long _visionHandSeq;
        private long _visionBodySeq;
        private long _visionMotionSeq;
        private JsonData _trackingJson = new JsonData();
        private TrackingData _trackingData = new TrackingData();
        private JsonData _meta = new JsonData();

        private string _requestUtc = "";
        private string _cameraReadyUtc = "";
        private string _videoStartUtc = "";
        private string _trackingStartUtc = "";
        private string _stopUtc = "";
        private string _completedUtc = "";
        private long _videoStartElapsedRealtimeNs;
        private double _videoStartPredictTimeUs;
        private long _videoStartUtcNs;

        public bool IsActive
        {
            get
            {
                return Phase == RecordingSessionPhase.Starting ||
                       Phase == RecordingSessionPhase.CameraReady ||
                       Phase == RecordingSessionPhase.Recording ||
                       Phase == RecordingSessionPhase.Stopping;
            }
        }

        /// <summary>Stopping longer than StopTimeoutSeconds (native hang / lost stop coroutine).</summary>
        public bool NeedsForceStop()
        {
            if (Phase != RecordingSessionPhase.Stopping || _stoppingSince < 0f)
            {
                return false;
            }

            return Time.realtimeSinceStartup - _stoppingSince >= StopTimeoutSeconds;
        }

        public void ForceReset(string reason)
        {
            Debug.LogWarning("[VisionTrackingSession] ForceReset phase=" + Phase + " reason=" + reason);
            try
            {
                if (_stopRoutine != null && _host != null)
                {
                    _host.StopCoroutine(_stopRoutine);
                    _stopRoutine = null;
                }

                if (_routine != null && _host != null)
                {
                    _host.StopCoroutine(_routine);
                    _routine = null;
                }
            }
            catch
            {
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                CameraHandle.ResetFinalizeCapture();
            }
            catch
            {
            }
#endif
            TrackingWriteEnabled = false;
            CloseTrackingWriter();
            FinalizeSessionWriter(RecordingSessionPhase.Failed, reason ?? "force_reset");
            _acceptStart = false;
            _finalized = true;
            _stoppingSince = -1f;
            Phase = RecordingSessionPhase.Idle;
            if (_meta != null)
            {
                _meta["forceResetReason"] = reason ?? "";
                _meta["status"] = "failed";
                try
                {
                    WriteMeta();
                }
                catch
                {
                }
            }

            SetVisual("idle");
            SetStatus("Failed");
            _finalized = false;
        }

        public void Begin(UICameraCtrl host, int width, int height, int fps, int bitrate, bool vision, bool tracking)
        {
            if (IsActive)
            {
                LogEvent("RecordRequested", "ignored=already-active phase=" + Phase, true);
                return;
            }

            _host = host;
            _vision = vision;
            _tracking = tracking;
            _width = width;
            _height = height;
            _fps = fps;
            _bitrate = bitrate;
            _finalized = false;
            _stoppingSince = -1f;
            _stopRoutine = null;
            _acceptStart = false;
            _videoStarted = false;
            _trackingStarted = false;
            _recoveryUsed = false;
            _seenProgress = false;
            _pendingFail = null;
            _trackingLines = 0;
            _trackingSamples = 0;
            _anomalyCount = 0;
            _videoPath = "";
            _trackingPath = "";
            _sessionDir = "";
            _sessionWriter = null;
            _visionHeadSeq = 0;
            _visionControllerSeq = 0;
            _visionHandSeq = 0;
            _visionBodySeq = 0;
            _visionMotionSeq = 0;
            TrackingWriteEnabled = false;
            SessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string collisionSuffix = DateTime.Now.ToString("fff");
            string root = Path.Combine(DownloadDir, CollectionSessionWriter.RootFolderName);
            string candidateSession = Path.Combine(root, "session_" + SessionId);
            if (Directory.Exists(candidateSession))
            {
                SessionId = SessionId + "_" + collisionSuffix;
            }

            _trackingSnapshot = TrackingConfigSnapshot.CaptureFromPanel();
            CameraHandle.SetLogContext(SessionId);
            string pipelineError = CapturePipeline.SelfCheck();
            if (pipelineError != null)
            {
                Debug.LogError("[RecordSession] pipeline self-check failed: " + pipelineError);
            }
            _requestUtc = DateTime.UtcNow.ToString("o");
            Phase = RecordingSessionPhase.Starting;
            _meta = new JsonData();
            _meta["sessionId"] = SessionId;
            _meta["mode"] = ModeName();
            _meta["imageCapturePath"] = vision ? "vision-capturelib" : "none";
            _meta["exp_Image"] = 0;
            _meta["saveType"] = vision ? "vision-mp4" : "tracking-only";
            _meta["requestUtc"] = _requestUtc;
            _meta["width"] = width;
            _meta["height"] = height;
            _meta["fps"] = fps;
            _meta["requested_fps"] = fps;
            _meta["bitrate"] = bitrate;
            _meta["status"] = "starting";
            _meta["syncHint"] = SyncHint();
            _meta["sessionDir"] = _sessionDir;
            _meta["collectionSchemaVersion"] = CollectionSessionWriter.SchemaVersion;
            _meta["tracking_config_at_start"] = _trackingSnapshot.ToJson();
            // AAR encoder is documented as ~5 Mbps; dialog bitrate may be ignored by capturelib.
            int bitrateBps = bitrate > 0 ? bitrate : 5 * 1000 * 1000;
            CaptureReport.WriteEncodeTimestampContract(_meta, "vision-startrecord",
                "mp4_pts_plus_start_anchor", "3D", width, height, fps, bitrateBps, "avc",
                eis: false, mctf: false, mfnr: false, raw: false,
                capturePathId: vision ? "vision-capturelib" : "none");
            CaptureReport.WriteMp4Plan(_meta, width, height, fps);
            long estimated = CaptureReport.EstimateSurfaceMp4Bytes(bitrateBps, 20 * 60);
            long free;
            long required;
            string diskError;
            bool diskOk = DiskSafety.EnsureSpace(DownloadDir, estimated, out free, out required, out diskError);
            CaptureReport.WriteDiskBudget(_meta, estimated, free, required, diskOk, DownloadDir);
            if (vision && !diskOk)
            {
                Finish(RecordingSessionPhase.Failed, diskError ?? "disk budget failed", false);
                return;
            }

            if (_tracking || _vision)
            {
                _sessionWriter = new CollectionSessionWriter(true, DownloadDir);
                _sessionWriter.Start(
                    _trackingSnapshot,
                    EnterpriseCollectionRecorder.SharedEnterpriseSampleHz,
                    90,
                    _vision,
                    SystemInfo.deviceModel,
                    Utils.IsPico4U(),
                    "session_" + SessionId);
                _sessionWriter.SetDiskBudget(diskOk, free, required);
                _sessionDir = _sessionWriter.SessionDir;
                _meta["sessionDir"] = _sessionDir;
                _meta["session_id"] = _sessionWriter.SessionId;
            }

            WriteMeta();

            LogEvent("RecordRequested",
                "mode=" + ModeName() + " " + width + "x" + height + "@" + fps + " bitrate=" + bitrate, false);
            SetVisual("starting");
            SetStatus("Starting");

            if (_vision && !Utils.IsPico4U())
            {
                Finish(RecordingSessionPhase.Failed,
                    "Vision recording requires a PICO 4 Ultra Enterprise device with camera permission. package=com.xrobotoolkit.client",
                    false);
                return;
            }

            UnityMainThreadDispatcher.Instance();
            CameraHandle.AddErrorListener(OnCaptureError);
            _routine = _host.StartCoroutine(Run());
        }

        public void RequestStop(string reason)
        {
            if (Phase == RecordingSessionPhase.Idle)
            {
                return;
            }

            if (Phase == RecordingSessionPhase.Stopping)
            {
                ForceReset("stop_reentry:" + (reason ?? "user"));
                return;
            }

            if (_finalized || _stopRoutine != null)
            {
                return;
            }

            _stopUtc = DateTime.UtcNow.ToString("o");
            _meta["stopRequestUtc"] = _stopUtc;
            _meta["stopReason"] = reason;
            LogEvent("StopRequested", "reason=" + reason + " phase=" + Phase, false);
            bool streamsStarted = Phase == RecordingSessionPhase.Recording
                                  || Phase == RecordingSessionPhase.CameraReady
                                  || _videoStarted
                                  || _trackingStarted
                                  || CameraHandle.PreviewEverStarted;
            RecordingSessionPhase requested = streamsStarted
                ? RecordingSessionPhase.Completed
                : RecordingSessionPhase.Failed;
            string finishReason = streamsStarted
                ? reason
                : "stopped during startup: " + reason;
            if (_host != null)
            {
                _stopRoutine = _host.StartCoroutine(StopAndFinish(requested, finishReason, true));
            }
            else
            {
                Finish(requested, finishReason, true);
            }
        }

        public void WriteTrackingSample()
        {
            if (!TrackingWriteEnabled || _sessionWriter == null || _finalized ||
                Phase != RecordingSessionPhase.Recording)
            {
                return;
            }

            try
            {
                _trackingData.Get(ref _trackingJson);
                _trackingJson["elapsedRealtimeNs"] = Utils.GetElapsedRealtimeNs();
                _trackingJson["sessionId"] = SessionId;
                _trackingLines++;
                _trackingSamples++;

                // Per-source files follow the start snapshot, not live panel toggles.
                if (_trackingSnapshot.Head)
                {
                    WriteVisionSourceSample("head", "head_pose", ref _visionHeadSeq,
                        _trackingJson.ContainsKey("Head") ? _trackingJson["Head"] : null);
                    if (!_trackingJson.ContainsKey("Head"))
                    {
                        _anomalyCount++;
                    }
                }

                if (_trackingSnapshot.Controller)
                {
                    WriteVisionSourceSample("controller", "controller_pose", ref _visionControllerSeq,
                        _trackingJson.ContainsKey("Controller") ? _trackingJson["Controller"] : null);
                    if (!_trackingJson.ContainsKey("Controller"))
                    {
                        _anomalyCount++;
                    }
                }

                if (_trackingSnapshot.Hand)
                {
                    WriteVisionSourceSample("hand", "hand_pose", ref _visionHandSeq,
                        _trackingJson.ContainsKey("Hand") ? _trackingJson["Hand"] : null);
                    if (!_trackingJson.ContainsKey("Hand"))
                    {
                        _anomalyCount++;
                    }
                }

                if (_trackingSnapshot.FullBody)
                {
                    WriteVisionSourceSample("full_body", "body_pose", ref _visionBodySeq,
                        _trackingJson.ContainsKey("Body") ? _trackingJson["Body"] : null);
                    if (!_trackingJson.ContainsKey("Body"))
                    {
                        _anomalyCount++;
                    }
                }

                if (_trackingSnapshot.MotionTracker)
                {
                    WriteVisionSourceSample("motion_tracker", "motion_tracker", ref _visionMotionSeq,
                        _trackingJson.ContainsKey("Motion") ? _trackingJson["Motion"] : null);
                    if (!_trackingJson.ContainsKey("Motion"))
                    {
                        _anomalyCount++;
                    }
                }
            }
            catch (Exception exception)
            {
                _pendingFail = "tracking write failed: " + exception.Message;
            }
        }

        private void WriteVisionSourceSample(string sourceKey, string kind, ref long seq, JsonData data)
        {
            if (_sessionWriter == null)
            {
                return;
            }

            bool success = data != null;
            JsonData envelope = new JsonData();
            envelope["source"] = "vision";
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
                envelope["error"] = kind + " missing in TrackingData.Get";
            }

            _sessionWriter.Enqueue(sourceKey, envelope.ToJson(), success);
        }

        private float _diskCheckTimer;

        public void Pump()
        {
            if (NeedsForceStop())
            {
                ForceReset("stopping_timeout");
                return;
            }

            if (_pendingFail != null && !_finalized &&
                (Phase == RecordingSessionPhase.Starting || Phase == RecordingSessionPhase.CameraReady ||
                 Phase == RecordingSessionPhase.Recording))
            {
                string reason = _pendingFail;
                _pendingFail = null;
                Finish(RecordingSessionPhase.Failed, reason, false);
                return;
            }

            if (Phase == RecordingSessionPhase.Recording && !_finalized && _vision)
            {
                _diskCheckTimer += Time.unscaledDeltaTime;
                if (_diskCheckTimer >= 30f)
                {
                    _diskCheckTimer = 0f;
                    long free = DiskSafety.GetFreeBytes(DownloadDir);
                    if (_meta != null)
                    {
                        _meta["lastFreeBytes"] = free;
                    }

                    if (free >= 0 && free < DiskSafety.ReserveBytes)
                    {
                        Finish(RecordingSessionPhase.FailedPartial, "disk free below 2GB reserve", false);
                    }
                }
            }
        }

        private IEnumerator Run()
        {
            if (_vision)
            {
                bool permissionOk = false;
                yield return EnsureCameraPermission(granted => permissionOk = granted);
                if (_finalized)
                {
                    yield break;
                }

                if (!permissionOk)
                {
                    Finish(RecordingSessionPhase.Failed,
                        "camera permission denied. package=com.xrobotoolkit.client requires android.permission.CAMERA and PICO enterprise camera authorization",
                        false);
                    yield break;
                }

                _acceptStart = true;
                int openResult;
                try
                {
                    openResult = CameraHandle.StartCameraPreview(_width, _height, _fps, _bitrate, 0,
                        (int)PXRCaptureRenderMode.PXRCapture_RenderMode_3D, OnPreviewReady);
                }
                catch (Exception exception)
                {
                    Finish(RecordingSessionPhase.Failed, "OpenCamera exception: " + exception.Message, false);
                    yield break;
                }

                if (_finalized)
                {
                    yield break;
                }

                if (openResult != CapturePipeline.NativeCallSuccess)
                {
                    Finish(RecordingSessionPhase.Failed,
                        "OpenCameraReturn openResult=" + openResult +
                        " callbackResult=" + CameraHandle.LastCallbackRegisterResult +
                        " expectedOpenSuccess=1 (native PXRCamera--open--Success). package=com.xrobotoolkit.client",
                        false);
                    yield break;
                }

                yield return null;
                if (_finalized || Phase != RecordingSessionPhase.Starting)
                {
                    yield break;
                }

                // Do not synthesize OPENED/CONFIGURED here. The callback is still
                // CAPTURE_STATE_CAMERA_OPENING (mCaptureState=0); configure and
                // startPreview both fail and StartRecord then writes an empty MP4.
                // Real OPENED/CONFIGURED/PREVIEWING callbacks drive SetConfig and preview.
            }
            else
            {
                try
                {
                    OpenTrackingWriter();
                    ConfirmRecording("tracking-only");
                }
                catch (Exception exception)
                {
                    Finish(RecordingSessionPhase.Failed, "tracking file open failed: " + exception.Message, false);
                }

                yield break;
            }

            float elapsed = 0f;
            while (!_finalized && Phase == RecordingSessionPhase.Starting && elapsed < StartTimeoutSeconds)
            {
                yield return new WaitForSeconds(0.4f);
                elapsed += 0.4f;
                if (_finalized)
                {
                    yield break;
                }

                int javaState = SafeJavaCaptureState();
                LogEvent("CaptureStatePolled",
                    "source=polling elapsedMs=" + (int)(elapsed * 1000) +
                    " javaFieldState=" + javaState +
                    " javaFieldNote=CameraHandle.mCaptureState-is-not-live" +
                    " lastCallback=" + CameraHandle.LastCallbackState +
                    " lastCallbackName=" + CapturePipeline.StateName(CameraHandle.LastCallbackState) +
                    " configApplied=" + CameraHandle.ConfigApplied +
                    " previewEverStarted=" + CameraHandle.PreviewEverStarted, false);

                // No synthetic OPENED/CONFIGURED. configure fails while mCaptureState is still 0.
            }

            if (!_finalized && Phase == RecordingSessionPhase.Starting)
            {
                Finish(RecordingSessionPhase.Failed, TimeoutMessage(), false);
            }
        }

        private IEnumerator EnsureCameraPermission(Action<bool> done)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                done(true);
                yield break;
            }

            bool finished = false;
            bool granted = false;
            PermissionCallbacks callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ =>
            {
                granted = true;
                finished = true;
            };
            callbacks.PermissionDenied += _ => { finished = true; };
            callbacks.PermissionDeniedAndDontAskAgain += _ => { finished = true; };
            Permission.RequestUserPermissions(new[] { Permission.Camera }, callbacks);
            float waited = 0f;
            while (!finished && waited < 20f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            LogEvent(granted ? "CameraPermissionGranted" : "CameraPermissionDenied", "waitedMs=" + (int)(waited * 1000),
                !granted);
            done(granted);
#else
            done(true);
            yield break;
#endif
        }

        private void OnPreviewReady()
        {
            if (!_acceptStart || _finalized || Phase != RecordingSessionPhase.Starting)
            {
                LogEvent("VideoRecordStartCall", "ignored phase=" + Phase + " accept=" + _acceptStart, true);
                return;
            }

            Phase = RecordingSessionPhase.CameraReady;
            _cameraReadyUtc = DateTime.UtcNow.ToString("o");
            _meta["cameraReadyUtc"] = _cameraReadyUtc;
            _meta["eisDisableResult"] = CameraHandle.LastEisDisableResult;
            _meta["fovNote"] =
                "3D side-by-side keeps each eye at 4:3, the same aspect as the capture sensor. " +
                "SetConfig enables EIS, which crops the frame; EIS is turned off after SetConfig. " +
                "eisDisableResult 1 is native success.";
            SetStatus("Ready");
            try
            {
                EnsureDownloadDir();
                if (string.IsNullOrEmpty(_sessionDir))
                {
                    _sessionDir = Path.Combine(DownloadDir, CollectionSessionWriter.RootFolderName,
                        "session_" + SessionId);
                    Directory.CreateDirectory(_sessionDir);
                }

                _videoPath = Path.Combine(_sessionDir, CollectionSessionWriter.VideoFile);
                _videoStartUtc = DateTime.UtcNow.ToString("o");
                _videoStartElapsedRealtimeNs = Utils.GetElapsedRealtimeNs();
                _videoStartPredictTimeUs = PXR_Enterprise.GetPredictedDisplayTime() * 1000.0;
                _videoStartUtcNs = Utils.GetCurrentTimestamp();
                _meta["videoPath"] = _videoPath;
                _sessionWriter?.SetVideoInfo(true, _videoPath, 0, "vision-startrecord");
                _meta["videoStartUtc"] = _videoStartUtc;
                _meta["videoStartUtcNs"] = _videoStartUtcNs;
                _meta["videoStartElapsedRealtimeNs"] = _videoStartElapsedRealtimeNs;
                _meta["videoStartPredictTimeUs"] = _videoStartPredictTimeUs;
                int recordResult = CameraHandle.StartRecord(_videoPath);
                if (recordResult != 0)
                {
                    Finish(RecordingSessionPhase.Failed, "VideoRecordStartResult=" + recordResult, false);
                    return;
                }

                _videoStarted = true;
                if (_tracking)
                {
                    OpenTrackingWriter();
                }

                ConfirmRecording("preview-ready");
            }
            catch (Exception exception)
            {
                RecordingSessionPhase phase = _videoStarted || _trackingStarted
                    ? RecordingSessionPhase.FailedPartial
                    : RecordingSessionPhase.Failed;
                Finish(phase, "failed to start files: " + exception.Message, false);
            }
        }

        private void OpenTrackingWriter()
        {
            if (_sessionWriter == null)
            {
                throw new InvalidOperationException("CollectionSessionWriter missing for tracking");
            }

            _sessionDir = _sessionWriter.SessionDir;
            _trackingPath = _sessionDir;
            _trackingStartUtc = DateTime.UtcNow.ToString("o");
            long elapsed = Utils.GetElapsedRealtimeNs();
            double predictUs = PXR_Enterprise.GetPredictedDisplayTime() * 1000.0;
            long utcNs = Utils.GetCurrentTimestamp();
            try
            {
                _meta["cameraExtrinsics"] = CameraHandle.GetCameraExtrinsics();
                _meta["cameraIntrinsics"] = CameraHandle.GetCameraIntrinsics(_width, _height);
            }
            catch (Exception exception)
            {
                _meta["cameraExtrinsics"] = "";
                _meta["cameraIntrinsics"] = "";
                _meta["cameraParamError"] = exception.Message;
                LogEvent("TrackingWriterOpened", "camera-param-error=" + exception.Message, true);
            }

            _trackingLines = 0;
            _trackingStarted = true;
            TrackingWriteEnabled = true;
            _meta["trackingPath"] = _sessionDir;
            _meta["sessionDir"] = _sessionDir;
            _meta["trackingStartUtc"] = _trackingStartUtc;
            _meta["trackingStartUtcNs"] = utcNs;
            _meta["trackingStartElapsedRealtimeNs"] = elapsed;
            _meta["trackingStartPredictTimeUs"] = predictUs;
            _meta["clockNote"] = SyncHint();
            WriteMeta();
            LogEvent("TrackingWriterOpened", "session=" + _sessionDir, false);
        }

        private void ConfirmRecording(string source)
        {
            if (_vision && !_videoStarted)
            {
                return;
            }

            if (_tracking && !_trackingStarted)
            {
                return;
            }

            Phase = RecordingSessionPhase.Recording;
            _meta["recordingConfirmedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["status"] = "recording";
            WriteMeta();
            SetVisual("recording");
            SetStatus("Recording");
            LogEvent("RecordingConfirmed", "source=" + source + " mode=" + ModeName(), false);
            string startLine = "Recording started session=" + SessionId;
            if (_vision && !string.IsNullOrEmpty(_videoPath))
            {
                startLine += " video=" + _videoPath;
            }

            if (_tracking && !string.IsNullOrEmpty(_trackingPath))
            {
                startLine += " tracking=" + _trackingPath;
            }

            LogWindow.Info(startLine);
            Toast.Show("Recording started");
            if (_tracking)
            {
                WriteTrackingSample();
            }
        }

        private void OnCaptureError(int errorCode)
        {
            if (_finalized)
            {
                return;
            }

            _pendingFail = "captureOnError=" + errorCode + " lastCallback=" +
                           CapturePipeline.StateName(CameraHandle.LastCallbackState);
        }

        public void NoteCaptureState(int state)
        {
            if (_finalized || !_vision)
            {
                return;
            }

            if (state >= (int)PXRCaptureState.CAPTURE_STATE_CAMERA_OPENING)
            {
                _seenProgress = true;
            }

            if (state == (int)PXRCaptureState.CAPTURE_STATE_IDLE && _seenProgress &&
                (Phase == RecordingSessionPhase.Starting || Phase == RecordingSessionPhase.CameraReady ||
                 Phase == RecordingSessionPhase.Recording))
            {
                _pendingFail = "capture returned IDLE after progress. lastCallback=" +
                               CapturePipeline.StateName(state) + " javaField=" + SafeJavaCaptureState();
            }
        }

        private void Finish(RecordingSessionPhase requestedPhase, string reason, bool fromUserStop)
        {
            if (_finalized || _stopRoutine != null)
            {
                return;
            }

            if (_host != null)
            {
                _stopRoutine = _host.StartCoroutine(StopAndFinish(requestedPhase, reason, fromUserStop));
                return;
            }

            // No host: best-effort synchronous path (editor / teardown).
            _finalized = true;
            Phase = RecordingSessionPhase.Stopping;
            SetVisual("stopping");
            bool cleanupFailed = SyncNativeTeardown();
            CompleteFinish(requestedPhase, reason, fromUserStop, cleanupFailed);
        }

        private IEnumerator StopAndFinish(RecordingSessionPhase requestedPhase, string reason,
            bool fromUserStop)
        {
            if (_finalized)
            {
                _stopRoutine = null;
                yield break;
            }

            _finalized = true;
            _acceptStart = false;
            TrackingWriteEnabled = false;
            Phase = RecordingSessionPhase.Stopping;
            _stoppingSince = Time.realtimeSinceStartup;
            SetVisual("stopping");
            SetStatus("Stopping…");
            if (string.IsNullOrEmpty(_stopUtc))
            {
                _stopUtc = DateTime.UtcNow.ToString("o");
                _meta["stopRequestUtc"] = _stopUtc;
            }

            CameraHandle.RemoveErrorListener(OnCaptureError);
            CameraHandle.StopDrivingPipeline();
            CloseTrackingWriter();

            bool cleanupFailed = false;
            bool previewStarted = CameraHandle.PreviewEverStarted;
            bool openSucceeded = CameraHandle.OpenSucceeded;

            if (_vision && (previewStarted || openSucceeded))
            {
                LogEvent("VideoFinalizeStarted", "path=" + _videoPath, false);
                yield return FinalizeVisionNativeAsync();
                if (_meta != null && _meta.ContainsKey("finalizeTimedOut"))
                {
                    cleanupFailed = true;
                    requestedPhase = RecordingSessionPhase.FailedPartial;
                }

                if (_meta != null && _meta.ContainsKey("stopPreviewError"))
                {
                    cleanupFailed = true;
                }
            }

            CompleteFinish(requestedPhase, reason, fromUserStop, cleanupFailed);
            _stopRoutine = null;
            _stoppingSince = -1f;
        }

        private IEnumerator FinalizeVisionNativeAsync()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            int accepted = -99;
            Exception beginEx = null;
            try
            {
                accepted = CameraHandle.BeginFinalizeCaptureAsync();
            }
            catch (Exception exception)
            {
                beginEx = exception;
            }

            if (beginEx != null)
            {
                _meta["stopPreviewError"] = beginEx.Message;
                SyncNativeTeardown();
                yield break;
            }

            // -1 = already running; still poll. Other negatives → sync fallback.
            if (accepted != 0 && accepted != -1)
            {
                _meta["finalizeAsyncAccepted"] = accepted;
                SyncNativeTeardown();
                yield break;
            }

            _meta["finalizeAsyncAccepted"] = accepted;
            while (CameraHandle.PollFinalizeCapture() == 1)
            {
                if (Time.realtimeSinceStartup - _stoppingSince >= StopTimeoutSeconds)
                {
                    _meta["finalizeTimedOut"] = true;
                    _meta["startTimeoutSec"] = StopTimeoutSeconds;
                    CameraHandle.ResetFinalizeCapture();
                    LogEvent("VideoFinalizeCompleted", "timed out after " + StopTimeoutSeconds + "s", true);
                    yield break;
                }

                yield return null;
            }

            if (CameraHandle.PollFinalizeCapture() == 2)
            {
                int[] results = CameraHandle.TakeFinalizeCaptureResults();
                if (results != null && results.Length >= 2)
                {
                    _meta["stopPreviewResult"] = results[0];
                    _meta["closeCameraResult"] = results[1];
                    if (results.Length >= 3)
                    {
                        _meta["markUseStateIdleOk"] = results[2] == 1;
                    }

                    LogEvent("VideoFinalizeCompleted",
                        "stopPreviewResult=" + results[0] + " closeCameraResult=" + results[1], false);
                }
            }
#else
            SyncNativeTeardown();
            yield break;
#endif
        }

        private bool SyncNativeTeardown()
        {
            bool cleanupFailed = false;
            if (_vision && CameraHandle.PreviewEverStarted)
            {
                try
                {
                    int stopPreview = CameraHandle.StopPreview();
                    _meta["stopPreviewResult"] = stopPreview;
                }
                catch (Exception exception)
                {
                    cleanupFailed = true;
                    _meta["stopPreviewError"] = exception.Message;
                }
            }

            if (_vision && CameraHandle.OpenSucceeded)
            {
                try
                {
                    int closeResult = CameraHandle.CloseCamera();
                    _meta["closeCameraResult"] = closeResult;
                }
                catch (Exception exception)
                {
                    cleanupFailed = true;
                    _meta["closeCameraError"] = exception.Message;
                }
            }

            return cleanupFailed;
        }

        private void CompleteFinish(RecordingSessionPhase requestedPhase, string reason, bool fromUserStop,
            bool cleanupFailed)
        {
            string validationError = ValidateFiles();
            bool anyStreamStarted = _videoStarted || _trackingStarted || CameraHandle.PreviewEverStarted;
            bool cleanStop = fromUserStop && !cleanupFailed &&
                             requestedPhase != RecordingSessionPhase.FailedPartial;
            RecordingSessionPhase finalPhase;
            if (!anyStreamStarted)
            {
                finalPhase = RecordingSessionPhase.Failed;
            }
            else if (cleanStop && validationError == null)
            {
                finalPhase = RecordingSessionPhase.Completed;
            }
            else
            {
                finalPhase = RecordingSessionPhase.FailedPartial;
            }

            _completedUtc = DateTime.UtcNow.ToString("o");
            _meta["completedUtc"] = _completedUtc;
            _meta["reason"] = reason;
            _meta["validationError"] = validationError ?? "";
            _meta["status"] = StatusName(finalPhase);
            _meta["trackingLines"] = _trackingLines;
            _meta["trackingSamples"] = _trackingSamples;
            _meta["anomalyCount"] = _anomalyCount;
            _meta["videoBytes"] = FileLength(_videoPath);
            _meta["trackingSamples"] = _trackingSamples;
            FinalizeSessionWriter(finalPhase, reason);
            // Session writer already flushed final meta.json; keep capture status in memory only.
            Phase = RecordingSessionPhase.Idle;

            string summary = "status=" + _meta["status"] + " reason=" + reason +
                             (string.IsNullOrEmpty(validationError) ? "" : " validation=" + validationError);
            long videoBytes = FileLength(_videoPath);
            if (finalPhase == RecordingSessionPhase.Completed)
            {
                LogEvent("SessionCompleted", summary, false);
                string saved = "Saved session=" + (_sessionDir ?? SessionId);
                if (_vision)
                {
                    saved += " video=" + _videoPath + " bytes=" + videoBytes;
                }

                if (_tracking)
                {
                    saved += " trackingSamples=" + _trackingSamples;
                }

                LogWindow.Info(saved);
                Toast.Show(_vision
                    ? "Saved " + FormatBytes(videoBytes)
                    : "Saved tracking");
            }
            else
            {
                Debug.LogError("[RecordSession] " + summary + " " + DiagnosticSnapshot());
                LogWindow.Error("Record failed " + ShortReason(validationError ?? reason));
                Toast.Show(ShortReason(validationError ?? reason));
            }

            SetVisual("idle");
            SetStatus(finalPhase == RecordingSessionPhase.Completed ? "Done" :
                finalPhase == RecordingSessionPhase.FailedPartial ? "Partial" : "Failed");
        }

        private string ValidateFiles()
        {
            if (_vision)
            {
                if (!_videoStarted)
                {
                    return "video was requested but StartRecord did not succeed";
                }

                if (!File.Exists(_videoPath))
                {
                    return "mp4 missing: " + _videoPath;
                }

                long length = new FileInfo(_videoPath).Length;
                if (length <= 0)
                {
                    return "mp4 is empty: " + _videoPath;
                }

                if (!FileContainsAscii(_videoPath, "ftyp"))
                {
                    return "mp4 has no ftyp header: " + _videoPath;
                }

                if (!FileContainsAscii(_videoPath, "moov"))
                {
                    return "mp4 has no moov atom, finalize did not complete: " + _videoPath;
                }
            }
            else if (!string.IsNullOrEmpty(_videoPath) && File.Exists(_videoPath))
            {
                return "vision-only was off but an mp4 exists";
            }

            if (_tracking)
            {
                if (!_trackingStarted)
                {
                    return "tracking was requested but the writer did not open";
                }

                if (string.IsNullOrEmpty(_sessionDir) || !Directory.Exists(_sessionDir))
                {
                    return "session dir missing: " + _sessionDir;
                }

                if (_trackingSamples < 1)
                {
                    return "tracking has no samples in session: " + _sessionDir;
                }
            }

            return null;
        }

        private string TimeoutMessage()
        {
            return "camera start timeout after " + StartTimeoutSeconds + "s. " + DiagnosticSnapshot();
        }

        private string DiagnosticSnapshot()
        {
            return "openResult=" + CameraHandle.LastOpenResult +
                   " callbackResult=" + CameraHandle.LastCallbackRegisterResult +
                   " startPreviewResult=" + CameraHandle.LastStartPreviewResult +
                   " lastCallback=" + CameraHandle.LastCallbackState +
                   " " + CapturePipeline.StateName(CameraHandle.LastCallbackState) +
                   " javaFieldState=" + SafeJavaCaptureState() +
                   " configApplied=" + CameraHandle.ConfigApplied +
                   " previewEverStarted=" + CameraHandle.PreviewEverStarted +
                   " recoveryUsed=" + _recoveryUsed;
        }

        private static int SafeJavaCaptureState()
        {
            try
            {
                return CameraHandle.GetCaptureState();
            }
            catch (Exception)
            {
                return int.MinValue;
            }
        }

        private void CloseTrackingWriter()
        {
            if (!TrackingWriteEnabled && !_trackingStarted)
            {
                return;
            }

            TrackingWriteEnabled = false;
            LogEvent("TrackingWriterClosed",
                "session=" + _sessionDir + " lines=" + _trackingLines + " samples=" + _trackingSamples +
                " anomalies=" + _anomalyCount, false);
        }

        private void FinalizeSessionWriter(RecordingSessionPhase finalPhase, string reason)
        {
            if (_sessionWriter == null)
            {
                return;
            }

            long videoBytes = FileLength(_videoPath);
            _sessionWriter.SetVideoInfo(_videoStarted && videoBytes > 0, _videoPath, videoBytes,
                _vision ? "vision-startrecord" : "tracking-only");
            _sessionWriter.SetCaptureExtra(_meta);
            string status;
            string interrupt;
            if (finalPhase == RecordingSessionPhase.Completed)
            {
                status = "completed";
                interrupt = "none";
            }
            else if (reason != null && reason.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status = "aborted_disk";
                interrupt = "disk";
            }
            else if (finalPhase == RecordingSessionPhase.FailedPartial)
            {
                status = "failed";
                interrupt = "exception";
            }
            else
            {
                status = "failed";
                interrupt = "exception";
            }

            _sessionWriter.Stop(status, reason ?? "", interrupt, reason ?? "", 2000);
            _sessionWriter = null;
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
                Debug.LogError("[RecordSession] meta write failed: " + exception.Message);
            }
        }

        private static void EnsureDownloadDir()
        {
            if (!Directory.Exists(DownloadDir))
            {
                Directory.CreateDirectory(DownloadDir);
            }
        }

        private static long FileLength(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }

            return new FileInfo(path).Length;
        }

        private static bool FileContainsAscii(string path, string token)
        {
            byte[] needle = Encoding.ASCII.GetBytes(token);
            byte[] buffer = new byte[1024 * 256];
            int keep = needle.Length - 1;
            using (FileStream stream = File.OpenRead(path))
            {
                int filled = 0;
                while (true)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0)
                    {
                        break;
                    }

                    int count = filled + read;
                    if (IndexOf(buffer, count, needle) >= 0)
                    {
                        return true;
                    }

                    if (count > keep)
                    {
                        Buffer.BlockCopy(buffer, count - keep, buffer, 0, keep);
                        filled = keep;
                    }
                    else
                    {
                        filled = count;
                    }
                }
            }

            return false;
        }

        private static int IndexOf(byte[] buffer, int count, byte[] needle)
        {
            int last = count - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (buffer[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }

        private string ModeName()
        {
            if (_vision && _tracking)
            {
                return "vision+tracking";
            }

            return _vision ? "vision" : "tracking";
        }

        private static string StatusName(RecordingSessionPhase phase)
        {
            switch (phase)
            {
                case RecordingSessionPhase.Completed: return "completed";
                case RecordingSessionPhase.FailedPartial: return "failed_partial";
                case RecordingSessionPhase.Failed: return "failed";
                default: return phase.ToString();
            }
        }

        private static string SyncHint()
        {
            return "timeStampNs remains UTC wall-clock milliseconds*1000000 (millisecond precision, Unix epoch). " +
                   "predictTime / predictTimeUs is PXR_Enterprise.GetPredictedDisplayTime()*1000 microseconds (predicted VST display time). " +
                   "elapsedRealtimeNs is SystemClock.elapsedRealtimeNanos, CLOCK_BOOTTIME nanoseconds, the same clock family as PICO Frame.timestamp. " +
                   "MP4 PTS is MediaCodec presentationTimeUs written by CameraHandle's MediaMuxer and is not Unix time. " +
                   "Do not treat equal filenames as synchronization. " +
                   "Align by comparing ffprobe PTS with videoStartElapsedRealtimeNs / videoStartPredictTimeUs and per-sample elapsedRealtimeNs / predictTime. " +
                   "Tracking samples start when video StartRecord has returned, so there is no unmarked pre-video tracking segment.";
        }

        private void LogEvent(string eventName, string detail, bool error)
        {
            // Diagnostics stay in logcat. User toast/LogWindow lines are explicit and short.
            string message = "session=" + SessionId + " event=" + eventName + " t=" + DateTime.UtcNow.ToString("o") +
                             " " + detail;
            if (error)
            {
                Debug.LogError("[RecordSession] " + message);
            }
            else
            {
                Debug.Log("[RecordSession] " + message);
            }
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

        private void SetVisual(string phase)
        {
            if (_host == null)
            {
                return;
            }

            _host.SetRecordVisual(phase);
        }

        private void SetStatus(string text)
        {
            if (_host != null)
            {
                _host.SetRecordStatus(text);
            }
        }

        private static string ShortReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "Record failed";
            }

            string text = reason;
            int path = text.IndexOf("/sdcard", StringComparison.Ordinal);
            if (path >= 0)
            {
                text = text.Substring(0, path).Trim().TrimEnd(':');
            }

            if (text.IndexOf("moov", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "MP4 not finalized";
            }

            if (text.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Record timeout";
            }

            if (text.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Camera permission denied";
            }

            if (text.Length > 42)
            {
                text = text.Substring(0, 41) + "…";
            }

            return text;
        }
    }
}
