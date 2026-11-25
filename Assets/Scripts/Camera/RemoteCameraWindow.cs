using System.Collections;
using UnityEngine;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LitJson;
using Network;
using Robot;
using UnityEngine.UI;


/// <summary>
/// Display window of PC camera
/// Responsible for receiving, decoding, and displaying data
/// </summary>
public class RemoteCameraWindow : MonoBehaviour
{
    public RawImage RemoteCameraImage;
    private TcpListener _tcpListener;
    private TcpClient _client;
    private NetworkStream _stream;
    private Texture2D _texture;
    public Texture2D Texture => _texture;
    private byte[] _imageBuffer;
    private CancellationTokenSource _receiveImageTs = null;
    private Task _imageReceiveTask;
    private bool _listening;
    private float _nextDecoderPollRealtime;
    private float _nextHealthRealtime;
    private int _decoderPolls;
    private int _textureUpdates;
    private int _hitchFrames;
    private double _decoderPollTotalMs;
    private double _decoderPollMaxMs;
    private float _frameDeltaMaxMs;

    private const float HealthIntervalSeconds = 5f;
    private const float HitchThresholdMs = 25f;

    public bool IsListening => _listening;

    private int _resolutionWidth = 2160;
    private int _resolutionHeight = 2160 / 2 * 4 / 3;
    private int _videoFps = 60;
    private int _bitrate = 40 * 1024 * 1024;

    public CustomButton listenBtn;

    // Floating ZED/External plane: forward of the head but offset up so it does
    // not sit on top of the main control panel (LE/RE full-face cover is off for ZED).
    private const float HeadForwardMeters = 0.9f;
    private const float HeadUpMeters = 0.22f;

    private void Awake()
    {
        FollowHead(Camera.main);
    }

    public void StartListen(int width, int height, int fps, int bitrate, int port)
    {
        if (_listening)
        {
            return;
        }

        _listening = true;
        _resolutionWidth = width;
        _resolutionHeight = height;
        _videoFps = fps;
        _bitrate = bitrate;
        ResetPerformanceWindow();

        StartCoroutine(OnStartListen(port));
    }

    private void OnDisable()
    {
        StopListening();
        Debug.Log("RemoteCameraWindow OnDisable");
    }

    public void OnCloseBtn()
    {
        // Listen-off owns teardown (control socket, audio, projection).
        if (listenBtn != null)
        {
            listenBtn.SetOnAndNotify(false);
            return;
        }

        // Prefab/scene wiring missing listenBtn: still tear the local decoder down.
        Debug.LogWarning("RemoteCameraWindow.closeBtn: listenBtn not assigned; stopping decoder only.");
        StopListening();
        gameObject.SetActive(false);
    }

    public void StopListening()
    {
        if (_listening)
        {
            MediaDecoder.release();
            _listening = false;
        }
        if (_texture != null)
        {
            if (RemoteCameraImage != null)
            {
                RemoteCameraImage.texture = null;
            }
            Destroy(_texture);
            _texture = null;
        }
        TcpHandler.SendFunctionValue("StopReceivePcCamera", "");
    }

    public IEnumerator OnStartListen(int port)
    {
        Debug.Log("StartListen port:" + port);

        _texture = new Texture2D(_resolutionWidth, _resolutionHeight, TextureFormat.RGB24, false, false);
        RemoteCameraImage.texture = _texture;
        yield return null;

        MediaDecoder.initialize((int)_texture.GetNativeTexturePtr(), _resolutionWidth, _resolutionHeight);
        MediaDecoder.startServer(port, false);
        yield return null;

        JsonData cameraParam = new JsonData();
        cameraParam["ip"] = Utils.GetLocalIPv4();
        cameraParam["port"] = port;
        cameraParam["width"] = _resolutionWidth;
        cameraParam["height"] = _resolutionHeight;
        cameraParam["fps"] = _videoFps;
        cameraParam["bitrate"] = _bitrate;
        TcpHandler.SendFunctionValue("StartReceivePcCamera", cameraParam.ToJson());
    }

    private void LateUpdate()
    {
        FollowHead(Camera.main);
    }

    private void FollowHead(Camera cam)
    {
        if (cam == null)
        {
            return;
        }

        Transform head = cam.transform;
        transform.position = head.position
                             + head.forward * HeadForwardMeters
                             + head.up * HeadUpMeters;
        transform.rotation = head.rotation;
    }

    private void Update()
    {
        if (_texture == null || Application.platform != RuntimePlatform.Android)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        float frameDeltaMs = Time.unscaledDeltaTime * 1000f;
        _frameDeltaMaxMs = Mathf.Max(_frameDeltaMaxMs, frameDeltaMs);
        if (frameDeltaMs >= HitchThresholdMs)
        {
            _hitchFrames++;
        }

        // JNI polling at the headset display refresh rate adds main-thread work
        // without producing more video frames. Poll at twice the negotiated video
        // rate (bounded to 30-60 Hz) and upload only when the decoder has a new frame.
        float pollRate = Mathf.Clamp(Mathf.Max(1, _videoFps) * 2f, 30f, 60f);
        if (now >= _nextDecoderPollRealtime)
        {
            _nextDecoderPollRealtime = now + 1f / pollRate;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            bool hasFrame = MediaDecoder.isUpdateFrame();
            if (hasFrame)
            {
                MediaDecoder.updateTexture();
                GL.InvalidateState();
                _textureUpdates++;
            }
            double elapsedMs =
                (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 /
                System.Diagnostics.Stopwatch.Frequency;
            _decoderPolls++;
            _decoderPollTotalMs += elapsedMs;
            _decoderPollMaxMs = System.Math.Max(_decoderPollMaxMs, elapsedMs);
        }

        if (now >= _nextHealthRealtime)
        {
            float windowSeconds = HealthIntervalSeconds;
            double averagePollMs = _decoderPolls > 0
                ? _decoderPollTotalMs / _decoderPolls
                : 0.0;
            CrashProbe.Breadcrumb(
                "remote_video.health",
                $"configured={_resolutionWidth}x{_resolutionHeight}@{_videoFps} " +
                $"bitrate={_bitrate} texture_fps={_textureUpdates / windowSeconds:F1} " +
                $"polls={_decoderPolls} poll_avg_ms={averagePollMs:F3} " +
                $"poll_max_ms={_decoderPollMaxMs:F3} hitch_frames={_hitchFrames} " +
                $"frame_max_ms={_frameDeltaMaxMs:F1}");
            ResetPerformanceWindow();
        }
    }

    private void ResetPerformanceWindow()
    {
        float now = Time.realtimeSinceStartup;
        _nextDecoderPollRealtime = now;
        _nextHealthRealtime = now + HealthIntervalSeconds;
        _decoderPolls = 0;
        _textureUpdates = 0;
        _hitchFrames = 0;
        _decoderPollTotalMs = 0.0;
        _decoderPollMaxMs = 0.0;
        _frameDeltaMaxMs = 0f;
    }
}
