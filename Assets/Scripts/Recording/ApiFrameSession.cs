using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.Android;

namespace Robot
{
    /// <summary>
    /// Experimental for4U frame capture that encodes CameraRecord_&lt;session&gt;.mp4.
    /// Capture callbacks only keep the latest frame; RGBA→MP4 runs on a worker thread so
    /// the XR UI thread can still accept Record stop.
    /// </summary>
    public class ApiFrameSession
    {
        public const int BufferWidth = 2048;
        public const int BufferHeight = 1536;
        public const string CapturePathId = "professional-api-for4U";
        public const string LockedCameraMode = "3D";

        private readonly object _frameLock = new object();
        private readonly object _muxerLock = new object();

        private UICameraCtrl _host;
        private TrackingData _trackingData = new TrackingData();
        private JsonData _trackingJson = new JsonData();
        private JsonData _meta;
        private StreamWriter _trackingWriter;
        private Coroutine _routine;
        private AndroidJavaObject _muxer;
        private byte[] _pinned;
        private GCHandle _handle;
        private byte[] _pending;
        private byte[] _encodeBuffer;
        private int _pendingLength;
        private uint _pendingDataSize;
        private ulong _pendingTimestamp;
        private int _pendingWidth;
        private int _pendingHeight;
        private bool _pendingReady;
        private int _encodeLength;
        private long _encodePtsUs;
        private long _encodeSdkNs;
        private bool _encodeBusy;
        private bool _acceptFrames;
        private bool _finalized;
        private bool _stopRequested;
        private string _pendingStopReason;
        private bool _cameraOpen;
        private bool _muxerStarted;
        private bool _modeStarted;
        private bool _tracking;
        private int _requestedFps;
        private string _cameraMode = LockedCameraMode;
        private int _encodedFrames;
        private int _callbacks;
        private int _droppedFrames;
        private int _throttledDrops;
        private int _trackingSamples;
        private string _pendingFail;
        private string _sessionId = "";
        private string _videoPath = "";
        private string _metaPath = "";
        private string _trackingPath = "";
        private string _pixelFormat = "not-read";
        private int _actualWidth;
        private int _actualHeight;
        private uint _actualDataSize;
        private long _firstSdkNs;
        private long _firstElapsedNs;
        private long _lastQueuedSdkNs;
        private long _encodeIntervalNs;
        private bool _haveFrame;
        private float _callbackWindow;
        private int _callbacksAtWindow;
        private float _sourceFps;
        private float _encodedFpsWindow;
        private int _encodedAtWindow;
        private float _encodedFps;
        private Thread _encodeThread;
        private AutoResetEvent _encodeSignal;
        private volatile bool _encodeThreadRun;
        private bool _finishStarted;
        private Coroutine _finishRoutine;
        private RecordingSessionPhase _phase = RecordingSessionPhase.Idle;

        public bool IsActive
        {
            get
            {
                return _phase == RecordingSessionPhase.Starting ||
                       _phase == RecordingSessionPhase.CameraReady ||
                       _phase == RecordingSessionPhase.Recording ||
                       _phase == RecordingSessionPhase.Stopping;
            }
        }

        public bool TrackingWriteEnabled { get; private set; }

        public string SessionId
        {
            get { return _sessionId; }
        }

        public void Begin(UICameraCtrl host, int requestedFps, bool tracking)
        {
            if (IsActive || host == null)
            {
                return;
            }

            _host = host;
            _tracking = tracking;
            _requestedFps = Mathf.Clamp(requestedFps, 1, 60);
            _encodeIntervalNs = (long)(1e9 / Math.Max(1, _requestedFps));
            _cameraMode = LockedCameraMode;
            _finalized = false;
            _finishStarted = false;
            _finishRoutine = null;
            _stopRequested = false;
            _pendingStopReason = null;
            _cameraOpen = false;
            _muxerStarted = false;
            _modeStarted = false;
            _haveFrame = false;
            _encodeBusy = false;
            _pendingReady = false;
            _encodedFrames = 0;
            _callbacks = 0;
            _droppedFrames = 0;
            _throttledDrops = 0;
            _trackingSamples = 0;
            _pendingFail = null;
            _acceptFrames = false;
            _lastQueuedSdkNs = 0;
            _sourceFps = 0f;
            _encodedFps = 0f;
            _encodedFpsWindow = 0f;
            _encodedAtWindow = 0;
            TrackingWriteEnabled = false;
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string download = VisionTrackingSession.DownloadDir;
            if (File.Exists(Path.Combine(download, "recording_meta_" + _sessionId + ".json")) ||
                File.Exists(Path.Combine(download, "trackingData_" + _sessionId + ".txt")) ||
                File.Exists(Path.Combine(download, "CameraRecord_" + _sessionId + ".mp4")))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            _videoPath = Path.Combine(download, "CameraRecord_" + _sessionId + ".mp4");
            _metaPath = Path.Combine(download, "recording_meta_" + _sessionId + ".json");
            _phase = RecordingSessionPhase.Starting;
            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["mode"] = tracking ? "vision+tracking" : "vision";
            _meta["imageCapturePath"] = CapturePathId;
            _meta["exp_Image"] = 1;
            _meta["saveType"] = "api-mp4";
            _meta["experimental"] = true;
            _meta["captureApi"] = "for4U-SetCameraFrameBuffer";
            _meta["status"] = "starting";
            _meta["requestUtc"] = DateTime.UtcNow.ToString("o");
            _meta["bufferWidth"] = BufferWidth;
            _meta["bufferHeight"] = BufferHeight;
            _meta["dialogWidthIgnored"] = true;
            _meta["dialogHeightIgnored"] = true;
            _meta["requested_fps"] = _requestedFps;
            _meta["configuredFps"] = _requestedFps;
            _meta["cameraMode"] = _cameraMode;
            _meta["cameraModeLocked"] = true;
            _meta["videoPath"] = _videoPath;
            _meta["encodeThread"] = true;
            _meta["fovNote"] =
                "Professional API mode uses the for4U buffer " + BufferWidth + "x" + BufferHeight +
                " and PXRCapture_RenderMode_3D side-by-side. RecordDialog Width/Height are not applied as crop or scale.";
            _meta["sdkLimits"] =
                "This SDK exposes Configurefor4U(bool,int) and OpenCameraAsyncfor4U(Action). " +
                "It has no dictionary overload for KEY_EIS or KEY_OUTPUT_CAMERA_RAW_DATA.";
            CaptureReport.WriteApiMp4Plan(_meta, BufferWidth, BufferHeight, _requestedFps, _cameraMode);
            WriteMeta();
            SetVisual("starting");
            SetStatus("API starting");
            Log("ApiCaptureRequested",
                "buffer=" + BufferWidth + "x" + BufferHeight + " mode=" + _cameraMode +
                " requested_fps=" + _requestedFps + " tracking=" + tracking);
#if UNITY_ANDROID && !UNITY_EDITOR
            StartEncodeThread();
            _routine = host.StartCoroutine(Run());
#else
            BeginFinish("for4U frame capture requires the PICO 4 Ultra Enterprise device");
#endif
        }

        public void RequestStop(string reason)
        {
            if (!IsActive || _finalized || _finishStarted)
            {
                return;
            }

            // Latch immediately so OnFrame/Pump stop heavy work and the Record button can respond.
            _stopRequested = true;
            _acceptFrames = false;
            TrackingWriteEnabled = false;
            if (_meta != null)
            {
                _meta["stopReason"] = reason;
            }

            Log("ApiStopRequested", reason);
            _pendingStopReason = _phase != RecordingSessionPhase.Recording
                ? "stopped during startup: " + reason
                : reason;
            SetVisual("stopping");
            SetStatus("API stopping");
        }

        public void Pump()
        {
            if (_pendingStopReason != null && !_finalized && !_finishStarted)
            {
                string reason = _pendingStopReason;
                _pendingStopReason = null;
                BeginFinish(reason);
                return;
            }

            if (_pendingFail != null && !_finalized && !_finishStarted)
            {
                string reason = _pendingFail;
                _pendingFail = null;
                BeginFinish(reason);
                return;
            }

            if (_acceptFrames && !_finalized && !_stopRequested)
            {
                _callbackWindow += Time.unscaledDeltaTime;
                if (_callbackWindow >= 1f)
                {
                    _sourceFps = (_callbacks - _callbacksAtWindow) / _callbackWindow;
                    _callbacksAtWindow = _callbacks;
                    _callbackWindow = 0f;
                }

                _encodedFpsWindow += Time.unscaledDeltaTime;
                if (_encodedFpsWindow >= 1f)
                {
                    _encodedFps = (_encodedFrames - _encodedAtWindow) / _encodedFpsWindow;
                    _encodedAtWindow = _encodedFrames;
                    _encodedFpsWindow = 0f;
                }
            }

            if (!_acceptFrames || _finalized || _stopRequested)
            {
                return;
            }

            int length;
            uint dataSize;
            ulong timestamp;
            int width;
            int height;
            lock (_frameLock)
            {
                if (!_pendingReady || _encodeBusy)
                {
                    return;
                }

                length = _pendingLength;
                dataSize = _pendingDataSize;
                timestamp = _pendingTimestamp;
                width = _pendingWidth;
                height = _pendingHeight;
                if (_encodeBuffer == null || _encodeBuffer.Length < length)
                {
                    _encodeBuffer = new byte[Math.Max(length, BufferWidth * BufferHeight * 4)];
                }

                Buffer.BlockCopy(_pending, 0, _encodeBuffer, 0, length);
                _pendingReady = false;
            }

            if (length <= 0)
            {
                return;
            }

            NoteFrame(width, height, dataSize, timestamp);
            if (_pixelFormat != "RGBA8888")
            {
                _pendingFail = "pixel format is not RGBA8888 datasize=" + dataSize +
                               " expected=" + (width * height * 4);
                return;
            }

            long elapsed = Utils.GetElapsedRealtimeNs();
            if (_phase == RecordingSessionPhase.Starting)
            {
                if (!StartMuxer(width, height))
                {
                    return;
                }

                ConfirmRecording(elapsed);
            }

            if (_phase != RecordingSessionPhase.Recording || _stopRequested)
            {
                return;
            }

            long sdkNs = unchecked((long)timestamp);
            long ptsUs = 0;
            if (_haveFrame)
            {
                long deltaNs = sdkNs - _firstSdkNs;
                if (deltaNs < 0)
                {
                    deltaNs = 0;
                }

                ptsUs = deltaNs / 1000L;
            }

            lock (_frameLock)
            {
                _encodeLength = length;
                _encodePtsUs = ptsUs;
                _encodeSdkNs = sdkNs;
                _lastQueuedSdkNs = sdkNs;
                _encodeBusy = true;
            }

            if (_encodeSignal != null)
            {
                _encodeSignal.Set();
            }
        }

        public void WriteTrackingSample()
        {
            if (!TrackingWriteEnabled || _trackingWriter == null || _finalized || _stopRequested ||
                _phase != RecordingSessionPhase.Recording)
            {
                return;
            }

            try
            {
                _trackingData.Get(ref _trackingJson);
                _trackingJson["elapsedRealtimeNs"] = Utils.GetElapsedRealtimeNs();
                _trackingJson["sessionId"] = _sessionId;
                _trackingWriter.WriteLine(_trackingJson.ToJson());
                _trackingSamples++;
            }
            catch (Exception exception)
            {
                _pendingFail = "tracking write failed: " + exception.Message;
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private IEnumerator Run()
        {
            bool granted = false;
            yield return EnsurePermission(value => granted = value);
            if (_finalized || _stopRequested)
            {
                yield break;
            }

            if (!granted)
            {
                BeginFinish("camera permission denied");
                yield break;
            }

            PXR_Enterprise.Configurefor4U(false, _requestedFps);
            bool openDone = false;
            bool openOk = false;
            PXR_Enterprise.OpenCameraAsyncfor4U(ok =>
            {
                openOk = ok;
                openDone = true;
            });
            float waited = 0f;
            while (!openDone && !_finalized && !_stopRequested && waited < 10f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_finalized || _stopRequested)
            {
                yield break;
            }

            if (!openDone || !openOk)
            {
                BeginFinish(openDone ? "OpenCameraAsyncfor4U returned false" : "OpenCameraAsyncfor4U timeout");
                yield break;
            }

            _cameraOpen = true;
            int bytes = BufferWidth * BufferHeight * 4;
            _pinned = new byte[bytes];
            _pending = new byte[bytes];
            _encodeBuffer = new byte[bytes];
            _handle = GCHandle.Alloc(_pinned, GCHandleType.Pinned);
            IntPtr data = _handle.AddrOfPinnedObject();
            PXR_Enterprise.SetCameraFrameBufferfor4U(BufferWidth, BufferHeight, ref data, OnFrame);
            PXRCaptureRenderMode mode = ParseMode(_cameraMode);
            bool started = PXR_Enterprise.StartGetImageDatafor4U(mode, BufferWidth, BufferHeight);
            _modeStarted = started;
            _meta["modeStartResult"] = started;
            WriteMeta();
            if (!started)
            {
                BeginFinish("StartGetImageDatafor4U returned false for " + _cameraMode + " " +
                       BufferWidth + "x" + BufferHeight);
                yield break;
            }

            _acceptFrames = true;
            float idle = 0f;
            while (!_finalized && !_stopRequested && _phase == RecordingSessionPhase.Starting && idle < 8f)
            {
                idle += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!_finalized && !_stopRequested && _phase == RecordingSessionPhase.Starting)
            {
                BeginFinish("no for4U frame arrived within 8s");
            }
        }

        private void OnFrame(Frame frame)
        {
            if (!_acceptFrames || _finalized || _stopRequested)
            {
                return;
            }

            Interlocked.Increment(ref _callbacks);
            if (frame.datasize == 0)
            {
                return;
            }

            long sdkNs = unchecked((long)frame.timestamp);
            if (_lastQueuedSdkNs > 0 && _encodeIntervalNs > 0 &&
                sdkNs - _lastQueuedSdkNs < _encodeIntervalNs)
            {
                Interlocked.Increment(ref _throttledDrops);
                return;
            }

            int expected = BufferWidth * BufferHeight * 4;
            int copyLength = expected;
            if (frame.datasize < (uint)expected)
            {
                copyLength = (int)frame.datasize;
            }

            lock (_frameLock)
            {
                if (_stopRequested || _pinned == null || copyLength > _pinned.Length)
                {
                    return;
                }

                // Keep only the latest unread frame. Never pile copies onto the main/callback path.
                if (_pendingReady || _encodeBusy)
                {
                    Interlocked.Increment(ref _throttledDrops);
                    return;
                }

                Buffer.BlockCopy(_pinned, 0, _pending, 0, copyLength);
                _pendingLength = copyLength;
                _pendingDataSize = frame.datasize;
                _pendingTimestamp = frame.timestamp;
                _pendingWidth = frame.width > 0 ? (int)frame.width : BufferWidth;
                _pendingHeight = frame.height > 0 ? (int)frame.height : BufferHeight;
                _pendingReady = true;
            }
        }

        private IEnumerator EnsurePermission(Action<bool> done)
        {
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
            Permission.RequestUserPermission(Permission.Camera, callbacks);
            float waited = 0f;
            while (!finished && waited < 20f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            done(granted);
        }
#else
        private IEnumerator Run()
        {
            yield break;
        }
#endif

        private void StartEncodeThread()
        {
            RequestEncodeThreadStop();
            if (_encodeThread != null && _encodeThread.IsAlive)
            {
                try
                {
                    _encodeThread.Join(500);
                }
                catch
                {
                }
            }

            DisposeEncodeThreadObjects();
            _encodeThreadRun = true;
            _encodeSignal = new AutoResetEvent(false);
            _encodeThread = new Thread(EncodeLoop)
            {
                IsBackground = true,
                Name = "ApiFrameEncode"
            };
            _encodeThread.Start();
        }

        private void RequestEncodeThreadStop()
        {
            _encodeThreadRun = false;
            if (_encodeSignal != null)
            {
                try
                {
                    _encodeSignal.Set();
                }
                catch
                {
                }
            }
        }

        private void DisposeEncodeThreadObjects()
        {
            _encodeThread = null;
            if (_encodeSignal != null)
            {
                _encodeSignal.Dispose();
                _encodeSignal = null;
            }
        }

        private void EncodeLoop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
#endif
            try
            {
                while (_encodeThreadRun)
                {
                    if (_encodeSignal != null)
                    {
                        _encodeSignal.WaitOne(50);
                    }

                    int length;
                    long ptsUs;
                    byte[] buffer;
                    lock (_frameLock)
                    {
                        if (!_encodeBusy || _encodeBuffer == null || _encodeLength <= 0)
                        {
                            continue;
                        }

                        length = _encodeLength;
                        ptsUs = _encodePtsUs;
                        buffer = _encodeBuffer;
                    }

                    // Drop any in-flight frame once stop is latched so the worker can exit
                    // before muxer.finish() runs on the finalize thread.
                    if (_stopRequested || !_encodeThreadRun)
                    {
                        lock (_frameLock)
                        {
                            _encodeBusy = false;
                        }

                        continue;
                    }

                    int result = -1;
                    try
                    {
                        lock (_muxerLock)
                        {
                            if (_muxer != null && _muxerStarted && !_finishStarted && !_finalized)
                            {
                                result = _muxer.Call<int>("encodeRgba", buffer, length, ptsUs);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        _pendingFail = "FrameMuxer.encodeRgba exception: " + exception.Message;
                        result = -3;
                    }

                    lock (_frameLock)
                    {
                        if (result == 0)
                        {
                            _encodedFrames++;
                        }
                        else if (result != -1)
                        {
                            _droppedFrames++;
                            if (result <= -3 && _pendingFail == null)
                            {
                                _pendingFail = "FrameMuxer.encodeRgba failed result=" + result;
                            }
                        }

                        _encodeBusy = false;
                    }
                }
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.DetachCurrentThread();
#endif
            }
        }

        private bool StartMuxer(int width, int height)
        {
            try
            {
                Directory.CreateDirectory(VisionTrackingSession.DownloadDir);
                if (File.Exists(_videoPath))
                {
                    File.Delete(_videoPath);
                }

                lock (_muxerLock)
                {
                    _muxer = new AndroidJavaObject("com.xrobotoolkit.enterprise.FrameMuxer");
                    int bitrate = Math.Max(8 * 1024 * 1024, width * height * 2);
                    _meta["apiFrameWidth"] = width;
                    _meta["apiFrameHeight"] = height;
                    _meta["encodeWidth"] = width;
                    _meta["encodeHeight"] = height;
                    int result = _muxer.Call<int>("start", _videoPath, width, height, bitrate, _requestedFps);
                    _meta["muxerStartResult"] = result;
                    if (result != 0)
                    {
                        _pendingFail = "FrameMuxer.start failed result=" + result;
                        return false;
                    }

                    _muxerStarted = true;
                }

                return true;
            }
            catch (Exception exception)
            {
                _pendingFail = "FrameMuxer.start exception: " + exception.Message;
                return false;
            }
        }

        private void NoteFrame(int width, int height, uint dataSize, ulong timestamp)
        {
            _actualWidth = width;
            _actualHeight = height;
            _actualDataSize = dataSize;
            int rgbaBytes = width * height * 4;
            _pixelFormat = dataSize == (uint)rgbaBytes ? "RGBA8888" : "raw_unknown";
            if (!_haveFrame)
            {
                _haveFrame = true;
                _firstSdkNs = unchecked((long)timestamp);
                _firstElapsedNs = Utils.GetElapsedRealtimeNs();
                _meta["firstFrameSdkTimestampNs"] = _firstSdkNs.ToString(CultureInfo.InvariantCulture);
                _meta["firstFrameElapsedRealtimeNs"] = _firstElapsedNs.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void ConfirmRecording(long elapsed)
        {
            if (_tracking)
            {
                OpenTracking(elapsed);
            }

            _phase = RecordingSessionPhase.Recording;
            _meta["status"] = "recording";
            _meta["recordingConfirmedUtc"] = DateTime.UtcNow.ToString("o");
            WriteMeta();
            SetVisual("recording");
            SetStatus("API recording");
            Toast.Show("API capture started");
            Log("ApiRecordingConfirmed",
                _pixelFormat + " " + _actualWidth + "x" + _actualHeight + " mode=" + _cameraMode +
                " requested_fps=" + _requestedFps);
        }

        private void OpenTracking(long elapsed)
        {
            _trackingPath = Path.Combine(VisionTrackingSession.DownloadDir, "trackingData_" + _sessionId + ".txt");
            _trackingWriter = new StreamWriter(_trackingPath, false);
            _trackingWriter.AutoFlush = true;
            double predictUs = PXR_Enterprise.GetPredictedDisplayTime() * 1000.0;
            long utcNs = Utils.GetCurrentTimestamp();
            JsonData header = new JsonData();
            header["notice"] =
                "This is the timestamp and head pose information when obtaining the image for the first frame.";
            header["timeStampNs"] = utcNs;
            header["sessionId"] = _sessionId;
            header["elapsedRealtimeNs"] = elapsed;
            header["predictTime"] = predictUs;
            header["predictTimeUs"] = predictUs;
            header["videoPath"] = _videoPath;
            header["clockNote"] =
                "API frames use Frame.timestamp BOOTTIME ns. elapsedRealtimeNs is the same clock family. timeStampNs stays UTC.";
            _trackingWriter.WriteLine(header.ToJson());
            TrackingWriteEnabled = true;
            _meta["trackingPath"] = _trackingPath;
        }

        private void BeginFinish(string reason)
        {
            if (_finalized || _finishStarted)
            {
                return;
            }

            _finishStarted = true;
            _stopRequested = true;
            _acceptFrames = false;
            TrackingWriteEnabled = false;
            _pendingStopReason = null;
            _phase = RecordingSessionPhase.Stopping;
            SetVisual("stopping");
            SetStatus("API stopping");
            if (_routine != null && _host != null)
            {
                _host.StopCoroutine(_routine);
                _routine = null;
            }

            RequestEncodeThreadStop();
            if (_host != null)
            {
                _finishRoutine = _host.StartCoroutine(FinishAsync(reason));
            }
            else
            {
                // No host to pump a coroutine; best-effort blocking path for editor-only failures.
                CompleteFinishAfterMuxer(reason, int.MinValue, "no-host");
            }
        }

        private IEnumerator FinishAsync(string reason)
        {
            // Let the UI thread keep rendering while the encode worker exits.
            float waited = 0f;
            while (_encodeThread != null && _encodeThread.IsAlive && waited < 1.5f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (_encodeThread != null && _encodeThread.IsAlive)
            {
                Log("ApiEncodeThread", "exit wait timed out; continuing finalize");
            }

            DisposeEncodeThreadObjects();

            int muxerFinish = int.MinValue;
            string muxerError = null;
            bool muxerDone = false;
            Thread finalizeThread = new Thread(() =>
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                AndroidJNI.AttachCurrentThread();
#endif
                try
                {
                    lock (_muxerLock)
                    {
                        if (_muxer != null)
                        {
                            try
                            {
                                if (_muxerStarted)
                                {
                                    muxerFinish = _muxer.Call<int>("finish");
                                }
                                else
                                {
                                    _muxer.Call("release");
                                    muxerFinish = 0;
                                }
                            }
                            catch (Exception exception)
                            {
                                muxerError = exception.Message;
                            }

                            try
                            {
                                _muxer.Dispose();
                            }
                            catch
                            {
                            }

                            _muxer = null;
                            _muxerStarted = false;
                        }
                        else
                        {
                            muxerFinish = 0;
                        }
                    }
                }
                finally
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    AndroidJNI.DetachCurrentThread();
#endif
                    muxerDone = true;
                }
            })
            {
                IsBackground = true,
                Name = "ApiFrameFinalize"
            };
            finalizeThread.Start();

            waited = 0f;
            while (!muxerDone && waited < 8f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            if (!muxerDone)
            {
                muxerError = string.IsNullOrEmpty(muxerError) ? "muxer finish timeout" : muxerError;
                Log("ApiMuxerFinish", "timeout waiting for background finalize");
            }

            CompleteFinishAfterMuxer(reason, muxerFinish, muxerError);
            _finishRoutine = null;
        }

        private void CompleteFinishAfterMuxer(string reason, int muxerFinish, string muxerError)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
            if (!string.IsNullOrEmpty(muxerError))
            {
                _meta["muxerFinishError"] = muxerError;
            }

            _meta["muxerFinishResult"] = muxerFinish;

#if UNITY_ANDROID && !UNITY_EDITOR
            if (_cameraOpen)
            {
                try
                {
                    PXR_Enterprise.CloseCamerafor4U();
                }
                catch (Exception exception)
                {
                    _meta["closeError"] = exception.Message;
                }

                _cameraOpen = false;
            }
#endif
            if (_handle.IsAllocated)
            {
                _handle.Free();
            }

            if (_trackingWriter != null)
            {
                _trackingWriter.Flush();
                _trackingWriter.Close();
                _trackingWriter = null;
            }

            CaptureReport.WriteApiFrameFacts(_meta, _actualWidth, _actualHeight, _pixelFormat,
                (int)_actualDataSize, _requestedFps, _sourceFps, _cameraMode, _modeStarted);
            CaptureReport.WriteApiPreEncode(_meta, _actualWidth, _actualHeight, _cameraMode);
            JsonData mp4 = new JsonData();
            mp4["edges"] =
                "Full for4U buffer encoded without crop, rotate, or scale. RecordDialog Width/Height were not applied. " +
                "EIS dictionary keys are unavailable in this SDK.";
            mp4["encodedFrames"] = _encodedFrames;
            mp4["requested_fps"] = _requestedFps;
            mp4["source_fps"] = _sourceFps.ToString("R", CultureInfo.InvariantCulture);
            mp4["encoded_fps"] = _encodedFps.ToString("R", CultureInfo.InvariantCulture);
            mp4["throttlePolicy"] =
                "Callbacks above requested_fps or while encode is busy are dropped before copying. " +
                "Encoding and muxer finish run off the Unity main thread. Frames are never duplicated.";
            mp4["throttledDrops"] = _throttledDrops;
            _meta["mp4"] = mp4;
            CaptureReport.WriteMp4Result(_meta, _videoPath);

            _meta["calibration"] = CaptureReport.CalibrationNote(
                _actualWidth > 0 ? _actualWidth : BufferWidth,
                _actualHeight > 0 ? _actualHeight : BufferHeight,
                true);
            _meta["timeSync"] = CaptureReport.ApiTimeSync(_firstSdkNs, _firstElapsedNs, _haveFrame);

            string validation = Validate();
            bool ok = string.IsNullOrEmpty(validation);
            _meta["status"] = ok ? "completed" : "failed";
            _meta["reason"] = reason ?? "";
            _meta["validationError"] = validation ?? "";
            _meta["completedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["encodedFrames"] = _encodedFrames;
            _meta["callbacks"] = _callbacks;
            _meta["framesDropped"] = _droppedFrames;
            _meta["throttledDrops"] = _throttledDrops;
            _meta["trackingSamples"] = _trackingSamples;
            _meta["requested_fps"] = _requestedFps;
            _meta["source_fps"] = _sourceFps.ToString("R", CultureInfo.InvariantCulture);
            _meta["encoded_fps"] = _encodedFps.ToString("R", CultureInfo.InvariantCulture);
            _meta["measuredFps"] = _sourceFps.ToString("R", CultureInfo.InvariantCulture);
            _meta["videoBytes"] = File.Exists(_videoPath) ? new FileInfo(_videoPath).Length : 0;
            WriteMeta();
            _phase = ok ? RecordingSessionPhase.Completed : RecordingSessionPhase.Failed;
            SetVisual("idle");
            SetStatus(ok ? "API completed" : "API failed");
            Toast.Show(ok ? "API capture completed" : Trim(string.IsNullOrEmpty(validation) ? reason : validation));
            Log(ok ? "ApiSessionCompleted" : "ApiSessionFailed",
                reason + " frames=" + _encodedFrames + " validation=" + (validation ?? ""));
        }

        private string Validate()
        {
            if (_encodedFrames <= 0 || !File.Exists(_videoPath))
            {
                return "video was requested but no MP4 samples were written";
            }

            FileInfo info = new FileInfo(_videoPath);
            if (info.Length <= 0)
            {
                return "mp4 exists but length is 0";
            }

            try
            {
                byte[] header = new byte[Math.Min(64, (int)info.Length)];
                using (FileStream stream = File.OpenRead(_videoPath))
                {
                    stream.Read(header, 0, header.Length);
                }

                string ascii = System.Text.Encoding.ASCII.GetString(header);
                if (ascii.IndexOf("ftyp", StringComparison.Ordinal) < 0)
                {
                    return "mp4 missing ftyp";
                }
            }
            catch (Exception exception)
            {
                return "mp4 header read failed: " + exception.Message;
            }

            if (!FileContains(_videoPath, "moov"))
            {
                return "mp4 missing moov";
            }

            if (_tracking && _trackingSamples <= 0)
            {
                return "tracking was requested but no samples were written";
            }

            return null;
        }

        private static bool FileContains(string path, string token)
        {
            try
            {
                const int budget = 1024 * 1024;
                using (FileStream stream = File.OpenRead(path))
                {
                    int size = (int)Math.Min(stream.Length, budget);
                    byte[] buffer = new byte[size];
                    if (stream.Length > budget)
                    {
                        stream.Seek(-size, SeekOrigin.End);
                    }

                    stream.Read(buffer, 0, size);
                    return System.Text.Encoding.ASCII.GetString(buffer).IndexOf(token, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void WriteMeta()
        {
            try
            {
                Directory.CreateDirectory(VisionTrackingSession.DownloadDir);
                File.WriteAllText(_metaPath, _meta.ToJson());
            }
            catch (Exception exception)
            {
                Debug.LogError("[ApiFrame] meta write failed: " + exception.Message);
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

        private void Log(string eventName, string detail)
        {
            string message = "session=" + _sessionId + " event=" + eventName + " " + detail;
            Debug.Log("[ApiFrame] " + message);
            LogWindow.Info(message);
        }

        private static PXRCaptureRenderMode ParseMode(string mode)
        {
            if (mode == "RIGHT")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_RIGHT;
            }

            if (mode == "3D")
            {
                return PXRCaptureRenderMode.PXRCapture_RenderMode_3D;
            }

            return PXRCaptureRenderMode.PXRCapture_RenderMode_LEFT;
        }

        private static string Trim(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                return "API capture failed";
            }

            return reason.Length > 180 ? reason.Substring(0, 180) : reason;
        }
    }
}
