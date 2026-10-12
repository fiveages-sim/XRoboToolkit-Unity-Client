using System;
using UnityEngine;

namespace Robot
{
    enum PXRCaptureState : int
    {
        CAPTURE_STATE_IDLE = 0,
        CAPTURE_STATE_CAMERA_OPENING = 1,
        CAPTURE_STATE_CAMERA_OPENED = 2,
        CAPTURE_STATE_CONFIGURED = 3,
        CAPTURE_STATE_PHOTO_CAPTURING = 4,
        CAPTURE_STATE_VIDEO_CAPTURING = 5,
        CAPTURE_STATE_VIDEO_PREVIEWING = 6
    }

    public class CameraHandle : AndroidJavaProxy
    {
        private static int _width = 1280;
        private static int _height = 480;
        private static int _fps = 30;
        private static int _bitrate = 10 * 1024 * 1024;
        private static int _enableMvHevc = 0;
        private static int _captureRenderMode = 0;
        private static PXRCaptureState CaptureState;
        private static int _pipelineEpoch;
        private static CapturePipeline.Flags _flags;
        private static int _lastCallbackState = -1;
        private static string _logContext = "-";

        private Action OnCameraOpened;
        private event Action<int> OnStateChanged;
        private event Action<int> OnError;
        private Action OnReset;

        private static CameraHandle _callbackProxy = new CameraHandle();

        public static int LastCallbackState => _lastCallbackState;
        public static int LastOpenResult { get; private set; } = int.MinValue;
        public static int LastCallbackRegisterResult { get; private set; } = int.MinValue;
        public static int LastStartPreviewResult { get; private set; } = int.MinValue;
        public static bool OpenSucceeded { get; private set; }
        public static bool PreviewEverStarted => _flags.PreviewEverStarted;
        public static bool ConfigApplied => _flags.ConfigApplied;

        public CameraHandle() : base("com.pxr.capturelib.PXRCameraCallBack")
        {
        }

        public static void SetLogContext(string sessionId)
        {
            _logContext = string.IsNullOrEmpty(sessionId) ? "-" : sessionId;
        }

        public void captureOnStateChanged(int state)
        {
            int epoch = _pipelineEpoch;
            UnityMainThreadDispatcher.Enqueue(() => { HandleStateOnMain(state, epoch, "callback"); });
        }

        public void captureOnError(int errorCode)
        {
            int epoch = _pipelineEpoch;
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (epoch != _pipelineEpoch)
                {
                    LogPipeline("CaptureStateCallback", "source=callback ignored=stale-epoch error=" + errorCode);
                    return;
                }

                Debug.LogError("[PXRCapture] session=" + _logContext + " event=CaptureError error=" + errorCode);
                LogWindow.Error("session=" + _logContext + " event=CaptureError error=" + errorCode);
                if (OnError != null)
                {
                    OnError.Invoke(errorCode);
                }
            });
        }

        public void captureOnEnviromentInfoChanged(int info1, int info2)
        {
            Debug.Log("[PXRCapture] Environment Info Changed: " + info1 + ", " + info2);
        }

        public void captureOnTakePictureComplete(float p1, float p2, float p3, float p4, float p5)
        {
            Debug.Log("[PXRCapture] Picture Taken: " + p1 + ", " + p2 + ", " + p3 + ", " + p4 + ", " + p5);
        }

        public void captureOnTakePictureTimeOut()
        {
            Debug.LogWarning("[PXRCapture] Picture Capture Timed Out");
        }

        private static void HandleStateOnMain(int state, int epoch, string source)
        {
            if (epoch != _pipelineEpoch)
            {
                LogPipeline("CaptureStateCallback",
                    "source=" + source + " ignored=stale-epoch state=" + state + " " + CapturePipeline.StateName(state));
                return;
            }

            _lastCallbackState = state;
            CaptureState = (PXRCaptureState)state;
            LogPipeline(source == "callback" ? "CaptureStateCallback" : "CaptureStatePolled",
                "source=" + source + " state=" + state + " name=" + CapturePipeline.StateName(state));

            try
            {
                if (CaptureState == PXRCaptureState.CAPTURE_STATE_IDLE && _callbackProxy.OnReset != null)
                {
                    _callbackProxy.OnReset();
                }

                CapturePipeline.Step step = CapturePipeline.Advance(state, ref _flags);
                if (step.DuplicateIgnored)
                {
                    LogPipeline("CaptureStateCallback",
                        "source=" + source + " duplicate-ignored state=" + CapturePipeline.StateName(state));
                }

                if (step.SetConfig)
                {
                    ApplyConfig(source);
                }

                if (step.StartPreview)
                {
                    ApplyPreview(source);
                }

                if (step.NotifyRecordingReady && _callbackProxy.OnCameraOpened != null)
                {
                    _callbackProxy.OnCameraOpened();
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[PXRCapture] session=" + _logContext + " state handler failed: " + exception);
                LogWindow.Error("session=" + _logContext + " event=CaptureStateHandlerFailed " + exception.Message);
            }

            if (_callbackProxy.OnStateChanged != null)
            {
                _callbackProxy.OnStateChanged.Invoke(state);
            }
        }

        private static void ApplyConfig(string source)
        {
            LogPipeline("SetConfigCall",
                "source=" + source + " enableMvHevc=" + _enableMvHevc + " fps=" + _fps +
                " note=java-SetConfig-discards-native-configure-int");
            GetJavaObject().Call("SetConfig", _enableMvHevc, _fps);
            LogPipeline("SetConfigResult", "source=" + source + " result=invoked");
        }

        private static void ApplyPreview(string source)
        {
            LogPipeline("StartPreviewCall",
                "source=" + source + " width=" + _width + " height=" + _height + " renderMode=" + _captureRenderMode +
                " requestedFps=" + _fps + " requestedBitrate=" + _bitrate +
                " note=MediaCodec format inside the AAR is hardcoded to 60fps and 5Mbps");
            LastStartPreviewResult = GetJavaObject().Call<int>("StartPreview", _width, _height, _captureRenderMode);
            LogPipeline("StartPreviewResult", "source=" + source + " result=" + LastStartPreviewResult +
                                             " note=java-StartPreview-returns-0-after-discarding-native-result");
        }

        /// <summary>
        /// Runs SetConfig, StartPreview, then the preview-ready handler when capture callbacks never arrive.
        /// Device logs showed openResult=1 and callbackResult=1 while lastCallback stayed -1, so waiting
        /// for CAMERA_OPENED/CONFIGURED never reached StartRecord. Each step stays idempotent.
        /// </summary>
        public static string DrivePipelineWithoutCallback(string source)
        {
            if (!_flags.Drive)
            {
                return "pipeline is not driving";
            }

            try
            {
                LogPipeline("PipelineDrivenWithoutCallback",
                    "source=" + source + " lastCallback=" + _lastCallbackState +
                    " configApplied=" + _flags.ConfigApplied + " previewApplied=" + _flags.PreviewApplied);
                if (!_flags.ConfigApplied)
                {
                    CapturePipeline.Step configStep =
                        CapturePipeline.Advance((int)PXRCaptureState.CAPTURE_STATE_CAMERA_OPENED, ref _flags);
                    if (configStep.SetConfig)
                    {
                        ApplyConfig(source);
                    }
                }

                if (!_flags.PreviewApplied)
                {
                    CapturePipeline.Step previewStep =
                        CapturePipeline.Advance((int)PXRCaptureState.CAPTURE_STATE_CONFIGURED, ref _flags);
                    if (previewStep.StartPreview)
                    {
                        ApplyPreview(source);
                    }
                }

                if (!_flags.PreviewNotified && _flags.PreviewApplied && _callbackProxy.OnCameraOpened != null)
                {
                    CapturePipeline.Step readyStep =
                        CapturePipeline.Advance((int)PXRCaptureState.CAPTURE_STATE_VIDEO_PREVIEWING, ref _flags);
                    if (readyStep.NotifyRecordingReady)
                    {
                        _callbackProxy.OnCameraOpened();
                    }
                }

                return null;
            }
            catch (Exception exception)
            {
                Debug.LogError("[PXRCapture] session=" + _logContext + " pipeline without callback failed: " + exception);
                LogWindow.Error("session=" + _logContext + " event=PipelineDrivenWithoutCallback exception=" +
                                exception.Message);
                return exception.Message;
            }
        }

        public static void StopDrivingPipeline()
        {
            _flags.Drive = false;
            _pipelineEpoch++;
            _callbackProxy.OnCameraOpened = null;
            LogPipeline("StopDrivingPipeline", "epoch=" + _pipelineEpoch);
        }

        private static AndroidJavaObject _javaObj = null;

        private static AndroidJavaObject GetJavaObject()
        {
            if (_javaObj == null)
            {
                _javaObj = new AndroidJavaObject("com.picovr.robotassistantlib.CameraHandle");
            }

            return _javaObj;
        }

        /// <summary>
        /// Java CameraHandle.mCaptureState. It is initialized to 0 and is not updated from native callbacks.
        /// Do not treat this as the live capture state.
        /// </summary>
        public static int GetCaptureState()
        {
            return GetJavaObject().Call<int>("GetCaptureState");
        }

        public static int StartCameraPreview(int width, int height, int fps, int bitrate, int enableMvHevc,
            int renderMode, Action onCameraOpened)
        {
            _callbackProxy.OnCameraOpened = onCameraOpened;
            _width = width;
            _height = height;
            _fps = fps;
            _bitrate = bitrate;
            _enableMvHevc = enableMvHevc;
            _captureRenderMode = renderMode;
            return OpenCamera();
        }

        public static int OpenCamera()
        {
            _flags = new CapturePipeline.Flags { Drive = true };
            _lastCallbackState = -1;
            OpenSucceeded = false;
            LastOpenResult = int.MinValue;
            LastCallbackRegisterResult = int.MinValue;
            int epoch = ++_pipelineEpoch;

            try
            {
                using (AndroidJavaClass opener = new AndroidJavaClass("com.xrobotoolkit.enterprise.CaptureOpener"))
                {
                    int[] results = opener.CallStatic<int[]>("openWithCallbackFirst", GetJavaObject(), _callbackProxy);
                    LastCallbackRegisterResult = results != null && results.Length > 0 ? results[0] : -1;
                    LastOpenResult = results != null && results.Length > 1 ? results[1] : int.MinValue;
                }

                LogPipeline("OpenCameraCall",
                    "epoch=" + epoch + " callbackResult=" + LastCallbackRegisterResult +
                    " note=callback-registered-before-open native-setCallback-success=1");
                LogPipeline("OpenCameraReturn",
                    "epoch=" + epoch + " openResult=" + LastOpenResult +
                    " note=native-1-logs-PXRCamera--open--Success native-0-logs-open--fail");
                OpenSucceeded = LastOpenResult == CapturePipeline.NativeCallSuccess &&
                                LastCallbackRegisterResult == CapturePipeline.NativeCallSuccess;
                if (!OpenSucceeded)
                {
                    _flags.Drive = false;
                }

                return OpenSucceeded ? CapturePipeline.NativeCallSuccess : LastOpenResult;
            }
            catch (Exception exception)
            {
                _flags.Drive = false;
                LastOpenResult = -1;
                Debug.LogError("[PXRCapture] session=" + _logContext + " OpenCamera exception: " + exception);
                LogWindow.Error("session=" + _logContext + " event=OpenCameraReturn exception=" + exception.Message);
                return -1;
            }
        }

        public static int StartRecord(string savePath)
        {
            LogPipeline("VideoRecordStartCall", "path=" + savePath);
            int result = GetJavaObject().Call<int>("StartRecord", savePath);
            LogPipeline("VideoRecordStartResult", "result=" + result + " path=" + savePath);
            return result;
        }

        public static int StartPreview(int width, int height, int renderMode, Action onPreviewing)
        {
            return GetJavaObject().Call<int>("StartPreview", width, height, renderMode);
        }

        public static void AddStateListener(Action<int> onStateChanged)
        {
            _callbackProxy.OnStateChanged += onStateChanged;
        }

        public static void RemoveStateListener(Action<int> onStateChanged)
        {
            _callbackProxy.OnStateChanged -= onStateChanged;
        }

        public static void AddErrorListener(Action<int> onError)
        {
            _callbackProxy.OnError += onError;
        }

        public static void RemoveErrorListener(Action<int> onError)
        {
            _callbackProxy.OnError -= onError;
        }

        public static int StartSendImage(string ip, int port)
        {
            return GetJavaObject().Call<int>("StartSendImage", ip, port);
        }

        public static int StopPreview()
        {
            _flags.Drive = false;
            _pipelineEpoch++;
            _callbackProxy.OnCameraOpened = null;
            int result = GetJavaObject().Call<int>("StopPreview");
            MarkUseStateIdle();
            LogPipeline("StopPreviewResult", "result=" + result);
            return result;
        }

        public static string GetCameraExtrinsics()
        {
            return GetJavaObject().Call<string>("getCameraExtrinsics");
        }

        public static string GetCameraIntrinsics(int width, int height)
        {
            return GetJavaObject().Call<string>("getCameraIntrinsics", width, height);
        }

        public static int CloseCamera()
        {
            _flags.Drive = false;
            _pipelineEpoch++;
            _callbackProxy.OnCameraOpened = null;
            int result = GetJavaObject().Call<int>("CloseCamera");
            OpenSucceeded = false;
            LogPipeline("CloseCameraResult", "result=" + result);
            return result;
        }

        private static void MarkUseStateIdle()
        {
            try
            {
                using (AndroidJavaClass opener = new AndroidJavaClass("com.xrobotoolkit.enterprise.CaptureOpener"))
                {
                    bool marked = opener.CallStatic<bool>("markUseStateIdle", GetJavaObject());
                    if (!marked)
                    {
                        Debug.LogWarning("[PXRCapture] session=" + _logContext +
                                         " CloseCamera may call StopPreview a second time");
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[PXRCapture] session=" + _logContext +
                                 " failed to set UseState.IDLE before CloseCamera: " + exception.Message);
            }
        }

        private static void LogPipeline(string eventName, string detail)
        {
            string message = "session=" + _logContext + " event=" + eventName + " " + detail;
            Debug.Log("[PXRCapture] " + message);
            LogWindow.Info(message);
        }
    }
}
