using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.Android;

namespace Robot
{
    /// <summary>
    /// for4U capture that stores JPEG frames at the dialog FPS plus tracking/meta.
    /// PC encodes CameraRecord_*.mp4 offline. No headset MediaCodec / FrameMuxer.
    /// </summary>
    public class ApiFrameSession
    {
        public const int BufferWidth = 2048;
        public const int BufferHeight = 1536;
        public const string CapturePathId = "professional-api-for4U";
        public const string LockedCameraMode = "3D";
        public const int JpegQuality = 80;
        private const float WriterJoinSeconds = 20f;

        private readonly object _frameLock = new object();

        private UICameraCtrl _host;
        private TrackingData _trackingData = new TrackingData();
        private JsonData _trackingJson = new JsonData();
        private JsonData _meta;
        private StreamWriter _trackingWriter;
        private StreamWriter _framesJsonlWriter;
        private Coroutine _routine;
        private Coroutine _finishRoutine;
        private byte[] _pinned;
        private GCHandle _handle;
        private byte[] _pending;
        private sbyte[] _writeBuffer;
        private int _pendingLength;
        private uint _pendingDataSize;
        private ulong _pendingTimestamp;
        private int _pendingWidth;
        private int _pendingHeight;
        private bool _pendingReady;
        private int _writeLength;
        private long _writeSdkNs;
        private long _writeElapsedNs;
        private int _writeWidth;
        private int _writeHeight;
        private bool _writeBusy;
        private bool _acceptFrames;
        private bool _finalized;
        private bool _stopRequested;
        private bool _finishStarted;
        private string _pendingStopReason;
        private string _pendingFail;
        private bool _cameraOpen;
        private bool _modeStarted;
        private bool _tracking;
        private int _requestedFps;
        private string _cameraMode = LockedCameraMode;
        private int _savedFrames;
        private int _callbacks;
        private int _droppedFrames;
        private int _throttledDrops;
        private int _dropsSinceLastSave;
        private int _trackingSamples;
        private string _sessionId = "";
        private string _metaPath = "";
        private string _trackingPath = "";
        private string _framesDir = "";
        private string _framesJsonlPath = "";
        private string _pixelFormat = "not-read";
        private int _actualWidth;
        private int _actualHeight;
        private uint _actualDataSize;
        private long _firstSdkNs;
        private long _firstElapsedNs;
        private long _lastAcceptedSdkNs;
        private long _sampleIntervalNs;
        private bool _haveFrame;
        private float _callbackWindow;
        private int _callbacksAtWindow;
        private float _sourceFps;
        private float _savedFpsWindow;
        private int _savedAtWindow;
        private float _savedFps;
        private Thread _writerThread;
        private AutoResetEvent _writerSignal;
        private volatile bool _writerThreadRun;
        private bool _outputCameraRawData;
        private bool _enableEis;
        private bool _enableMctf;
        private bool _enableMfnr;
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

        public void Begin(UICameraCtrl host, int requestedFps, bool tracking,
            bool outputCameraRawData = false, bool enableEis = false, bool enableMctf = false,
            bool enableMfnr = true)
        {
            if (IsActive || host == null)
            {
                return;
            }

            _host = host;
            _tracking = tracking;
            _outputCameraRawData = outputCameraRawData;
            _enableEis = enableEis;
            _enableMctf = enableMctf;
            _enableMfnr = enableMfnr;
            _requestedFps = Mathf.Clamp(requestedFps, 1, 60);
            _sampleIntervalNs = (long)(1e9 / Math.Max(1, _requestedFps));
            _cameraMode = LockedCameraMode;
            _finalized = false;
            _finishStarted = false;
            _finishRoutine = null;
            _stopRequested = false;
            _pendingStopReason = null;
            _pendingFail = null;
            _cameraOpen = false;
            _modeStarted = false;
            _haveFrame = false;
            _writeBusy = false;
            _pendingReady = false;
            _savedFrames = 0;
            _callbacks = 0;
            _droppedFrames = 0;
            _throttledDrops = 0;
            _dropsSinceLastSave = 0;
            _trackingSamples = 0;
            _acceptFrames = false;
            _lastAcceptedSdkNs = 0;
            _sourceFps = 0f;
            _savedFps = 0f;
            _savedFpsWindow = 0f;
            _savedAtWindow = 0;
            TrackingWriteEnabled = false;
            _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string download = VisionTrackingSession.DownloadDir;
            if (File.Exists(Path.Combine(download, "recording_meta_" + _sessionId + ".json")) ||
                File.Exists(Path.Combine(download, "trackingData_" + _sessionId + ".txt")) ||
                Directory.Exists(Path.Combine(download, "frames_" + _sessionId)))
            {
                _sessionId = _sessionId + "_" + DateTime.Now.ToString("fff");
            }

            _metaPath = Path.Combine(download, "recording_meta_" + _sessionId + ".json");
            _framesDir = Path.Combine(download, "frames_" + _sessionId);
            _framesJsonlPath = Path.Combine(download, "frames_" + _sessionId + ".jsonl");
            _phase = RecordingSessionPhase.Starting;
            _meta = new JsonData();
            _meta["sessionId"] = _sessionId;
            _meta["mode"] = tracking ? "vision+tracking" : "vision";
            _meta["imageCapturePath"] = CapturePathId;
            _meta["exp_Image"] = 1;
            _meta["saveType"] = "api-frames";
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
            _meta["jpegQuality"] = JpegQuality;
            _meta["cameraMode"] = _cameraMode;
            _meta["cameraModeLocked"] = true;
            _meta["framesDir"] = _framesDir;
            _meta["framesJsonlPath"] = _framesJsonlPath;
            _meta["pcEncodeNote"] =
                "Headset stores JPEG frames at requested_fps. Run Docs/for4u_frames_to_mp4.sh to build CameraRecord_<sessionId>.mp4 on PC.";
            _meta["fovNote"] =
                "Professional API mode uses the for4U buffer " + BufferWidth + "x" + BufferHeight +
                " and PXRCapture_RenderMode_3D side-by-side. RecordDialog Width/Height are not applied as crop or scale.";
            _meta["sdkLimits"] =
                "SDK 3.4.0 for4U flags come from RecordDialog (Raw exclusive with EIS/MCTF/MFNR). " +
                "No headset MediaCodec; JPEG writer thread + PC encode.";
            _meta["outputCameraRawData"] = _outputCameraRawData;
            _meta["enableEis"] = _enableEis;
            _meta["enableMctf"] = _enableMctf;
            _meta["enableMfnr"] = _enableMfnr;
            _meta["configureKeyOutputCameraRawData"] = _outputCameraRawData
                ? PXRCapture.VALUE_TRUE
                : PXRCapture.VALUE_FALSE;
            _meta["openKeyEis"] = _enableEis ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE;
            _meta["openKeyMctf"] = _enableMctf ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE;
            _meta["openKeyMfnr"] = _enableMfnr ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE;
            CaptureReport.WriteApiFramesPlan(_meta, BufferWidth, BufferHeight, _requestedFps, _cameraMode,
                _framesDir, _framesJsonlPath, JpegQuality);
            WriteMeta();
            SetVisual("starting");
            SetStatus("API starting");
            string flags = "exp_Image=1 raw=" + (_outputCameraRawData ? 1 : 0) +
                           " eis=" + (_enableEis ? 1 : 0) +
                           " mctf=" + (_enableMctf ? 1 : 0) +
                           " mfnr=" + (_enableMfnr ? 1 : 0) +
                           " fps=" + _requestedFps;
            LogWindow.Info(flags + " buffer=" + BufferWidth + "x" + BufferHeight + " save=api-frames");
            Log("ApiCaptureRequested",
                flags + " mode=" + _cameraMode + " tracking=" + tracking + " saveType=api-frames");
#if UNITY_ANDROID && !UNITY_EDITOR
            StartWriterThread();
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

                _savedFpsWindow += Time.unscaledDeltaTime;
                if (_savedFpsWindow >= 1f)
                {
                    _savedFps = (_savedFrames - _savedAtWindow) / _savedFpsWindow;
                    _savedAtWindow = _savedFrames;
                    _savedFpsWindow = 0f;
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
            int dropsBefore = Interlocked.Exchange(ref _dropsSinceLastSave, 0);
            lock (_frameLock)
            {
                if (!_pendingReady || _writeBusy)
                {
                    // Put drops back so the next saved frame still accounts for them.
                    if (dropsBefore > 0)
                    {
                        Interlocked.Add(ref _dropsSinceLastSave, dropsBefore);
                    }

                    return;
                }

                length = _pendingLength;
                dataSize = _pendingDataSize;
                timestamp = _pendingTimestamp;
                width = _pendingWidth;
                height = _pendingHeight;
                if (_writeBuffer == null || _writeBuffer.Length < length)
                {
                    _writeBuffer = new sbyte[Math.Max(length, BufferWidth * BufferHeight * 4)];
                }

                Buffer.BlockCopy(_pending, 0, _writeBuffer, 0, length);
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
                if (!PrepareFrameSession())
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
            lock (_frameLock)
            {
                _writeLength = length;
                _writeSdkNs = sdkNs;
                _writeElapsedNs = elapsed;
                _writeWidth = width;
                _writeHeight = height;
                _pendingDropsBeforeWrite = dropsBefore;
                _writeBusy = true;
                _lastAcceptedSdkNs = sdkNs;
            }

            if (_writerSignal != null)
            {
                _writerSignal.Set();
            }
        }

        private int _pendingDropsBeforeWrite;

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
            var rawCfg = new Dictionary<string, string>
            {
                {
                    PXRCapture.KEY_OUTPUT_CAMERA_RAW_DATA,
                    _outputCameraRawData ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                }
            };
            PXR_Enterprise.Configurefor4U(rawCfg);

            var openParams = new Dictionary<string, string>
            {
                {
                    PXRCapture.KEY_MCTF,
                    _enableMctf ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                },
                {
                    PXRCapture.KEY_EIS,
                    _enableEis ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                },
                {
                    PXRCapture.KEY_MFNR,
                    _enableMfnr ? PXRCapture.VALUE_TRUE : PXRCapture.VALUE_FALSE
                }
            };

            bool openDone = false;
            bool openOk = false;
            PXR_Enterprise.OpenCameraAsyncfor4U(ok =>
            {
                openOk = ok;
                openDone = true;
            }, openParams);
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
            _writeBuffer = new sbyte[bytes];
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
            if (_lastAcceptedSdkNs > 0 && _sampleIntervalNs > 0 &&
                sdkNs - _lastAcceptedSdkNs < _sampleIntervalNs)
            {
                Interlocked.Increment(ref _throttledDrops);
                Interlocked.Increment(ref _dropsSinceLastSave);
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

                if (_pendingReady || _writeBusy)
                {
                    Interlocked.Increment(ref _throttledDrops);
                    Interlocked.Increment(ref _dropsSinceLastSave);
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

        private void StartWriterThread()
        {
            RequestWriterThreadStop();
            if (_writerThread != null && _writerThread.IsAlive)
            {
                try
                {
                    _writerThread.Join(500);
                }
                catch
                {
                }
            }

            DisposeWriterThreadObjects();
            _writerThreadRun = true;
            _writerSignal = new AutoResetEvent(false);
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "ApiFrameJpegWriter"
            };
            _writerThread.Start();
        }

        private void RequestWriterThreadStop()
        {
            _writerThreadRun = false;
            if (_writerSignal != null)
            {
                try
                {
                    _writerSignal.Set();
                }
                catch
                {
                }
            }
        }

        private void DisposeWriterThreadObjects()
        {
            _writerThread = null;
            if (_writerSignal != null)
            {
                _writerSignal.Dispose();
                _writerSignal = null;
            }
        }

        private void WriterLoop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
            AndroidJavaClass writerClass = null;
            try
            {
                writerClass = new AndroidJavaClass("com.xrobotoolkit.enterprise.FrameJpegWriter");
            }
            catch (Exception exception)
            {
                _pendingFail = "FrameJpegWriter class missing: " + exception.Message;
            }
#endif
            try
            {
                while (true)
                {
                    if (_writerSignal != null)
                    {
                        _writerSignal.WaitOne(50);
                    }

                    int length;
                    long sdkNs;
                    long elapsedNs;
                    int width;
                    int height;
                    int dropsBefore;
                    sbyte[] buffer;
                    bool haveWork;
                    lock (_frameLock)
                    {
                        haveWork = _writeBusy && _writeBuffer != null && _writeLength > 0;
                        if (!haveWork)
                        {
                            if (!_writerThreadRun)
                            {
                                break;
                            }

                            continue;
                        }

                        length = _writeLength;
                        sdkNs = _writeSdkNs;
                        elapsedNs = _writeElapsedNs;
                        width = _writeWidth;
                        height = _writeHeight;
                        dropsBefore = _pendingDropsBeforeWrite;
                        buffer = _writeBuffer;
                    }

                    int index = _savedFrames;
                    string fileName = index.ToString("D6", CultureInfo.InvariantCulture) + ".jpg";
                    string path = Path.Combine(_framesDir, fileName);
                    int result = -1;
                    try
                    {
#if UNITY_ANDROID && !UNITY_EDITOR
                        if (writerClass != null && Directory.Exists(_framesDir))
                        {
                            result = writerClass.CallStatic<int>("writeRgbaJpeg", path, buffer, length,
                                width, height, JpegQuality);
                        }
#else
                        result = -1;
#endif
                    }
                    catch (Exception exception)
                    {
                        _pendingFail = "JPEG write exception: " + exception.Message;
                        result = -3;
                    }

                    if (result == 0)
                    {
                        try
                        {
                            AppendFramesJsonl(index, fileName, sdkNs, elapsedNs, width, height, dropsBefore);
                            _savedFrames = index + 1;
                        }
                        catch (Exception exception)
                        {
                            _pendingFail = "frames.jsonl write failed: " + exception.Message;
                            _droppedFrames++;
                        }
                    }
                    else if (result != -1)
                    {
                        _droppedFrames++;
                        if (result <= -3 && _pendingFail == null)
                        {
                            _pendingFail = "JPEG write failed result=" + result;
                        }
                    }

                    lock (_frameLock)
                    {
                        _writeBusy = false;
                    }
                }
            }
            finally
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                if (writerClass != null)
                {
                    writerClass.Dispose();
                }

                AndroidJNI.DetachCurrentThread();
#endif
            }
        }

        private bool PrepareFrameSession()
        {
            try
            {
                Directory.CreateDirectory(VisionTrackingSession.DownloadDir);
                Directory.CreateDirectory(_framesDir);
                if (File.Exists(_framesJsonlPath))
                {
                    File.Delete(_framesJsonlPath);
                }

                _framesJsonlWriter = new StreamWriter(_framesJsonlPath, false, Encoding.UTF8);
                _framesJsonlWriter.AutoFlush = true;
                _meta["framesDir"] = _framesDir;
                _meta["framesJsonlPath"] = _framesJsonlPath;
                return true;
            }
            catch (Exception exception)
            {
                _pendingFail = "prepare frames session failed: " + exception.Message;
                return false;
            }
        }

        private void AppendFramesJsonl(int index, string fileName, long sdkNs, long elapsedNs,
            int width, int height, int dropsBefore)
        {
            if (_framesJsonlWriter == null)
            {
                return;
            }

            JsonData line = new JsonData();
            line["index"] = index;
            line["file"] = fileName;
            line["sdk_timestamp_ns"] = sdkNs.ToString(CultureInfo.InvariantCulture);
            line["elapsedRealtimeNs"] = elapsedNs.ToString(CultureInfo.InvariantCulture);
            line["width"] = width;
            line["height"] = height;
            line["dropped_before"] = dropsBefore;
            _framesJsonlWriter.WriteLine(line.ToJson());
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
            Toast.Show("API frames started");
            Log("ApiRecordingConfirmed",
                _pixelFormat + " " + _actualWidth + "x" + _actualHeight + " mode=" + _cameraMode +
                " requested_fps=" + _requestedFps + " saveType=api-frames");
            string startLine = "API frames session=" + _sessionId +
                               " raw=" + (_outputCameraRawData ? 1 : 0) +
                               " eis=" + (_enableEis ? 1 : 0) +
                               " mctf=" + (_enableMctf ? 1 : 0) +
                               " mfnr=" + (_enableMfnr ? 1 : 0) +
                               " framesDir=" + _framesDir;
            if (_tracking && !string.IsNullOrEmpty(_trackingPath))
            {
                startLine += " tracking=" + _trackingPath;
            }

            LogWindow.Info(startLine);
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
            header["framesDir"] = _framesDir;
            header["framesJsonlPath"] = _framesJsonlPath;
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

            RequestWriterThreadStop();
            if (_host != null)
            {
                _finishRoutine = _host.StartCoroutine(FinishAsync(reason));
            }
            else
            {
                CompleteFinish(reason, "no-host");
            }
        }

        private IEnumerator FinishAsync(string reason)
        {
            // Drain writer fully before closing camera (no parallel CloseCamera).
            float waited = 0f;
            while (_writerThread != null && _writerThread.IsAlive && waited < WriterJoinSeconds)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            string flushError = null;
            if (_writerThread != null && _writerThread.IsAlive)
            {
                flushError = "jpeg writer join timed out after " + WriterJoinSeconds + "s";
                Log("ApiWriterThread", flushError);
            }

            DisposeWriterThreadObjects();

            if (_framesJsonlWriter != null)
            {
                try
                {
                    _framesJsonlWriter.Flush();
                    _framesJsonlWriter.Close();
                }
                catch (Exception exception)
                {
                    flushError = string.IsNullOrEmpty(flushError)
                        ? exception.Message
                        : flushError + "; " + exception.Message;
                }

                _framesJsonlWriter = null;
            }

            if (_trackingWriter != null)
            {
                try
                {
                    _trackingWriter.Flush();
                    _trackingWriter.Close();
                }
                catch (Exception exception)
                {
                    flushError = string.IsNullOrEmpty(flushError)
                        ? exception.Message
                        : flushError + "; " + exception.Message;
                }

                _trackingWriter = null;
            }

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

            CompleteFinish(reason, flushError);
            _finishRoutine = null;
        }

        private void CompleteFinish(string reason, string flushError)
        {
            if (_finalized)
            {
                return;
            }

            _finalized = true;
            if (!string.IsNullOrEmpty(flushError))
            {
                _meta["flushError"] = flushError;
            }

            CaptureReport.WriteApiFrameFacts(_meta, _actualWidth, _actualHeight, _pixelFormat,
                (int)_actualDataSize, _requestedFps, _sourceFps, _cameraMode, _modeStarted);
            CaptureReport.WriteApiFramesResult(_meta, _framesDir, _framesJsonlPath, _savedFrames,
                _requestedFps, _savedFps, _throttledDrops, JpegQuality);

            // Never query calib while closing; camera already closed above.
            _meta["calibration"] = CaptureReport.CalibrationNote(
                _actualWidth > 0 ? _actualWidth : BufferWidth,
                _actualHeight > 0 ? _actualHeight : BufferHeight,
                false);
            _meta["timeSync"] = CaptureReport.ApiTimeSync(_firstSdkNs, _firstElapsedNs, _haveFrame);

            string validation = Validate(flushError);
            bool ok = string.IsNullOrEmpty(validation);
            bool partial = !ok && _savedFrames > 0;
            _meta["status"] = ok ? "completed" : (partial ? "failed_partial" : "failed");
            _meta["reason"] = reason ?? "";
            _meta["validationError"] = validation ?? "";
            _meta["completedUtc"] = DateTime.UtcNow.ToString("o");
            _meta["savedFrames"] = _savedFrames;
            _meta["encodedFrames"] = _savedFrames;
            _meta["callbacks"] = _callbacks;
            _meta["framesDropped"] = _droppedFrames;
            _meta["throttledDrops"] = _throttledDrops;
            _meta["trackingSamples"] = _trackingSamples;
            _meta["requested_fps"] = _requestedFps;
            _meta["source_fps"] = _sourceFps.ToString("R", CultureInfo.InvariantCulture);
            _meta["saved_fps"] = _savedFps.ToString("R", CultureInfo.InvariantCulture);
            _meta["measuredFps"] = _sourceFps.ToString("R", CultureInfo.InvariantCulture);
            long framesBytes = DirBytes(_framesDir);
            long trackingBytes = File.Exists(_trackingPath) ? new FileInfo(_trackingPath).Length : 0;
            _meta["framesBytes"] = framesBytes;
            WriteMeta();
            _phase = ok
                ? RecordingSessionPhase.Completed
                : (partial ? RecordingSessionPhase.FailedPartial : RecordingSessionPhase.Failed);
            SetVisual("idle");
            SetStatus(ok ? "API completed" : "API failed");
            if (ok)
            {
                string saved = "Saved session=" + _sessionId + " frames=" + _savedFrames +
                               " dir=" + _framesDir + " bytes=" + framesBytes;
                if (_tracking && !string.IsNullOrEmpty(_trackingPath))
                {
                    saved += " tracking=" + _trackingPath + " bytes=" + trackingBytes;
                }

                LogWindow.Info(saved);
                Toast.Show("Saved " + _savedFrames + " frames " + FormatBytes(framesBytes));
            }
            else
            {
                Toast.Show(Trim(string.IsNullOrEmpty(validation) ? reason : validation));
            }

            Log(ok ? "ApiSessionCompleted" : "ApiSessionFailed",
                reason + " frames=" + _savedFrames + " validation=" + (validation ?? "") +
                " framesBytes=" + framesBytes);
        }

        private string Validate(string flushError)
        {
            if (!string.IsNullOrEmpty(flushError))
            {
                return flushError;
            }

            if (_savedFrames <= 0 || !Directory.Exists(_framesDir))
            {
                return "frames were requested but none were written";
            }

            if (!File.Exists(_framesJsonlPath) || new FileInfo(_framesJsonlPath).Length <= 0)
            {
                return "frames.jsonl missing or empty";
            }

            string firstJpg = Path.Combine(_framesDir, "000000.jpg");
            if (!File.Exists(firstJpg) || new FileInfo(firstJpg).Length <= 0)
            {
                return "first JPEG missing: " + firstJpg;
            }

            if (_tracking && _trackingSamples <= 0)
            {
                return "tracking was requested but no samples were written";
            }

            return null;
        }

        private static long DirBytes(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                return 0;
            }

            long total = 0;
            try
            {
                foreach (string path in Directory.GetFiles(dir))
                {
                    total += new FileInfo(path).Length;
                }
            }
            catch
            {
            }

            return total;
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
            if (eventName == "ApiCaptureRequested" || eventName == "ApiStopRequested" ||
                eventName == "ApiSessionCompleted" || eventName == "ApiSessionFailed")
            {
                if (eventName == "ApiSessionFailed")
                {
                    LogWindow.Error(message);
                }
                else
                {
                    LogWindow.Info(message);
                }
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
