using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using LitJson;
using Network;
using Robot.Conf;
using Robot.V2.Network;
using UnityEngine;
using XRoboToolkit.Network;

public partial class UICameraCtrl
{

    private RemotePcmAudioPlayer remoteAudioPlayer;
    private PicoMicrophoneStreamer microphoneStreamer;
    private RemoteVideoProjectionRenderer videoProjectionRenderer;
    private RemoteRecordStatusOverlay recordStatusOverlay;
    private string lastRecordStatusBreadcrumbState = string.Empty;
    private OperatorControlClient operatorControlClient;
    private Coroutine cameraRequestCoroutine;
    private Coroutine microphoneStartCoroutine;
    private string activeAudioHost;
    private int audioSessionId;
    private int negotiatedAudioStreamPort;
    private bool audioDownlinkExplicitlyDisabled;
    private int negotiatedMicrophoneUploadPort;
    private string negotiatedMicrophoneUploadToken;
    private string currentAudioRequestId;
    private bool audioPortAckReceived;
    private bool acceptAudioPortConfig;
    private bool duplexAudioStarted;
    private bool microphoneMuted;
    private bool microphonePermissionRequested;
    private bool resumeDuplexAudioAfterPause;
    private bool controlClientConnected;
    private bool controlReconnectSuppressed;
    private float nextControlWatchdogRealtime;
    private long controlPingCount;
    private readonly List<byte> clientProtocolBuffer = new List<byte>();

    private const float AudioPortAckTimeoutSeconds = 1.5f;
    private const float MicrophonePermissionTimeoutSeconds = 10f;
    private const float ControlConnectTimeoutSeconds = 3f;
    private const float ControlReconnectDelaySeconds = 1f;
    private const float ControlWatchdogIntervalSeconds = 1f;
    private const string AudioPortConfigSchema = "g1_wuji_audio_ports_v2";
    private const string MicrophoneUploadProtocol = "g1_wuji_audio_uplink_v1";
    private const int MaxControlCommandBytes = 1024;
    private const int MaxControlPayloadBytes = 1024 * 1024;

    private void OnControlClientConnected()
    {
        EventExecutor.ExecuteInUpdate(() =>
        {
            if (operatorControlClient == null || !operatorControlClient.IsConnected)
            {
                return;
            }
            controlClientConnected = true;
            controlReconnectSuppressed = false;
            CrashProbe.Breadcrumb("operator_control.connected");
        });
    }

    private void OnControlClientDisconnected()
    {
        EventExecutor.ExecuteInUpdate(() =>
        {
            // Events cross from a worker thread to Unity's update queue. Ignore a
            // delayed disconnect from an older run after a newer run connected.
            if (operatorControlClient != null && operatorControlClient.IsConnected)
            {
                return;
            }
            HandleControlConnectionLost("disconnected");
        });
    }

    private void OnControlClientError(string message)
    {
        EventExecutor.ExecuteInUpdate(() =>
        {
            CrashProbe.Breadcrumb("operator_control.error", message, LogType.Warning);
            LogWindow.Warn($"Operator control client error: {message}");
        });
    }

    private void HandleControlConnectionLost(string reason)
    {
        controlClientConnected = false;
        if (controlReconnectSuppressed ||
            !isActiveAndEnabled ||
            listenBtn == null ||
            !listenBtn.On ||
            string.IsNullOrEmpty(activeAudioHost))
        {
            return;
        }

        // If the floating decoder is already receiving, do not blast another
        // OPEN_CAMERA at the source (that was restarting VST and blanking the view).
        var window = RemoteCameraWindowObj != null
            ? RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>()
            : null;
        if (window != null && window.IsListening)
        {
            LogWindow.Warn(
                $"Operator control connection lost ({reason}); video still listening — " +
                "skipping OPEN_CAMERA renegotiate.");
            CrashProbe.Breadcrumb(
                "operator_control.lost_keep_video",
                reason,
                LogType.Warning);
            return;
        }

        LogWindow.Warn($"Operator control connection lost ({reason}); renegotiating session.");
        CrashProbe.Breadcrumb("operator_control.lost", reason, LogType.Warning);
        StopCameraRequestCoroutine();
        StopDuplexAudio(false);
        cameraRequestCoroutine = StartCoroutine(
            ReconnectCameraSessionAfterDelay(activeAudioHost));
    }

    private IEnumerator ReconnectCameraSessionAfterDelay(string host)
    {
        yield return new WaitForSecondsRealtime(ControlReconnectDelaySeconds);
        cameraRequestCoroutine = null;
        if (listenBtn != null && listenBtn.On && !string.IsNullOrEmpty(host))
        {
            RequestCameraStream(host);
        }
    }

    private void OnClientDataReceived(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        // PING is a transport heartbeat, not a UI event.  Reply on the receive
        // thread so a busy Unity frame cannot revoke video and both audio
        // directions merely because the main-thread event queue was delayed.
        if (NetworkDataProtocolSerializer.TryDeserialize(
                data,
                out NetworkDataProtocol protocol) &&
            string.Equals(protocol.command, NetworkCommand.PING, StringComparison.Ordinal))
        {
            bool sent = operatorControlClient != null &&
                        operatorControlClient.SendCommand(NetworkCommand.PONG, protocol.data);
            long count = Interlocked.Increment(ref controlPingCount);
            if (count == 1 || count % 10 == 0 || !sent)
            {
                CrashProbe.Breadcrumb(
                    sent ? "operator_control.pong" : "operator_control.pong_failed",
                    $"count={count}",
                    sent ? LogType.Log : LogType.Warning);
            }
            return;
        }

        EventExecutor.ExecuteInUpdate(() => HandleClientDataChunk(data));
    }

    private void HandleClientDataChunk(byte[] data)
    {
        if (clientProtocolBuffer.Count == 0 && LooksLikeJson(data))
        {
            ApplyAudioPortConfig(data);
            return;
        }

        clientProtocolBuffer.AddRange(data);
        while (TryTakeClientProtocolFrame(out byte[] frame))
        {
            HandleClientProtocolFrame(frame);
        }
    }

    private void HandleClientProtocolFrame(byte[] frame)
    {
        if (!NetworkDataProtocolSerializer.TryDeserialize(
                frame,
                out NetworkDataProtocol protocol))
        {
            CrashProbe.Breadcrumb(
                "operator_control.invalid_frame",
                $"bytes={frame?.Length ?? 0}",
                LogType.Warning);
            return;
        }

        if (string.Equals(protocol.command, NetworkCommand.PING, StringComparison.Ordinal))
        {
            operatorControlClient?.SendCommand(NetworkCommand.PONG, protocol.data);
            return;
        }
        if (string.Equals(
                protocol.command,
                NetworkCommand.RECORD_STATUS,
                StringComparison.Ordinal))
        {
            ApplyRecordStatus(protocol.data);
            return;
        }
        if (string.Equals(
                protocol.command,
                NetworkCommand.AUDIO_CONFIG,
                StringComparison.Ordinal))
        {
            ApplyAudioPortConfig(protocol.data);
        }
    }

    private bool TryTakeClientProtocolFrame(out byte[] frame)
    {
        frame = null;
        if (clientProtocolBuffer.Count < sizeof(int))
        {
            return false;
        }

        int commandLength = ReadLittleEndianInt32(clientProtocolBuffer, 0);
        if (commandLength < 0 || commandLength > MaxControlCommandBytes)
        {
            LogWindow.Warn("Discarding malformed control response: invalid command length.");
            clientProtocolBuffer.Clear();
            return false;
        }

        int dataLengthOffset = sizeof(int) + commandLength;
        if (clientProtocolBuffer.Count < dataLengthOffset + sizeof(int))
        {
            return false;
        }

        int dataLength = ReadLittleEndianInt32(clientProtocolBuffer, dataLengthOffset);
        if (dataLength < 0 || dataLength > MaxControlPayloadBytes)
        {
            LogWindow.Warn("Discarding malformed control response: invalid payload length.");
            clientProtocolBuffer.Clear();
            return false;
        }

        int totalLength = dataLengthOffset + sizeof(int) + dataLength;
        if (clientProtocolBuffer.Count < totalLength)
        {
            return false;
        }

        frame = clientProtocolBuffer.GetRange(0, totalLength).ToArray();
        clientProtocolBuffer.RemoveRange(0, totalLength);
        return true;
    }

    private static int ReadLittleEndianInt32(List<byte> buffer, int offset)
    {
        return buffer[offset] |
               (buffer[offset + 1] << 8) |
               (buffer[offset + 2] << 16) |
               (buffer[offset + 3] << 24);
    }

    private static bool LooksLikeJson(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];
            if (value == (byte)' ' || value == (byte)'\t' || value == (byte)'\r' || value == (byte)'\n')
            {
                continue;
            }

            return value == (byte)'{';
        }

        return false;
    }

    private void ApplyAudioPortConfig(byte[] data)
    {
        if (!TryReadAudioPortAck(
                data,
                out int audioStreamPort,
                out int microphoneUploadPort,
                out string microphoneUploadToken,
                out string audioRequestId,
                out string videoProjection,
                out string videoStereoLayout))
        {
            CrashProbe.Breadcrumb("audio_config.invalid", $"bytes={data?.Length ?? 0}", LogType.Warning);
            return;
        }

        if (!string.Equals(audioRequestId, currentAudioRequestId, StringComparison.Ordinal))
        {
            CrashProbe.Breadcrumb("audio_config.stale", audioRequestId, LogType.Warning);
            LogWindow.Warn("Ignoring stale AUDIO_CONFIG from an older control request.");
            return;
        }

        if (!acceptAudioPortConfig ||
            string.IsNullOrEmpty(activeAudioHost) ||
            listenBtn == null ||
            !listenBtn.On)
        {
            return;
        }

        bool portsChanged = audioStreamPort != GetRemoteAudioPort() ||
                            (microphoneUploadPort > 0 &&
                             microphoneUploadPort != GetMicrophoneUploadPort()) ||
                            !string.Equals(
                                microphoneUploadToken,
                                negotiatedMicrophoneUploadToken,
                                StringComparison.Ordinal);
        if (audioStreamPort > 0)
        {
            negotiatedAudioStreamPort = audioStreamPort;
            audioDownlinkExplicitlyDisabled = false;
        }
        else
        {
            negotiatedAudioStreamPort = 0;
            audioDownlinkExplicitlyDisabled = true;
        }

        if (microphoneUploadPort > 0)
        {
            negotiatedMicrophoneUploadPort = microphoneUploadPort;
            negotiatedMicrophoneUploadToken = microphoneUploadToken;
        }
        else
        {
            negotiatedMicrophoneUploadPort = 0;
            negotiatedMicrophoneUploadToken = null;
        }

        audioPortAckReceived = true;
        acceptAudioPortConfig = false;
        ApplyVideoProjection(videoProjection, videoStereoLayout);
        CrashProbe.Breadcrumb(
            "audio_config.applied",
            $"downlink={GetRemoteAudioPort()} mic={GetMicrophoneUploadPort()} projection={videoProjection} layout={videoStereoLayout}");
        LogWindow.Info(
            $"Audio ports negotiated: downlink={GetRemoteAudioPort()}, " +
            $"microphone_upload={GetMicrophoneUploadPort()}.");

        if (duplexAudioStarted && portsChanged)
        {
            StartDuplexAudioForSession(activeAudioHost, audioSessionId);
        }
    }

    private static bool TryReadAudioPortAck(
        byte[] data,
        out int audioStreamPort,
        out int microphoneUploadPort,
        out string microphoneUploadToken,
        out string audioRequestId,
        out string videoProjection,
        out string videoStereoLayout)
    {
        audioStreamPort = 0;
        microphoneUploadPort = 0;
        microphoneUploadToken = null;
        audioRequestId = null;
        videoProjection = RemoteVideoProjectionRenderer.FlatProjection;
        videoStereoLayout = RemoteVideoProjectionRenderer.MonoLayout;
        byte[] jsonPayload = null;

        if (NetworkDataProtocolSerializer.TryDeserialize(data, out NetworkDataProtocol protocol))
        {
            bool isAudioConfig = string.Equals(
                protocol.command,
                NetworkCommand.AUDIO_CONFIG,
                StringComparison.Ordinal);
            bool isLegacyAck = string.Equals(
                protocol.command,
                NetworkCommand.OPEN_CAMERA_ACK,
                StringComparison.Ordinal);
            if (!isAudioConfig && !isLegacyAck)
            {
                return false;
            }

            jsonPayload = protocol.data;
        }
        else
        {
            // Compatibility fallback for an operator bridge that sends the ACK JSON directly.
            jsonPayload = data;
        }

        if (jsonPayload == null || jsonPayload.Length == 0)
        {
            return false;
        }

        try
        {
            JsonData json = JsonMapper.ToObject(Encoding.UTF8.GetString(jsonPayload));
            if (json == null || !json.IsObject)
            {
                return false;
            }

            if (!json.ContainsKey("schema") ||
                !string.Equals(
                    json["schema"].ToString(),
                    AudioPortConfigSchema,
                    StringComparison.Ordinal))
            {
                return false;
            }

            if (!MatchesOptionalInt(json, "sample_rate", PicoMicrophoneStreamer.OutputSampleRate) ||
                !MatchesOptionalInt(json, "channels", PicoMicrophoneStreamer.OutputChannels) ||
                !MatchesOptionalString(json, "sample_format", "s16le"))
            {
                return false;
            }

            if (json.ContainsKey("video_projection"))
            {
                videoProjection = RemoteVideoProjectionRenderer.NormalizeProjection(
                    json["video_projection"].ToString());
            }
            if (json.ContainsKey("video_stereo_layout"))
            {
                videoStereoLayout = RemoteVideoProjectionRenderer.NormalizeStereoLayout(
                    json["video_stereo_layout"].ToString());
            }

            if (!json.ContainsKey("audio_request_id"))
            {
                return false;
            }
            audioRequestId = json["audio_request_id"].ToString();
            if (string.IsNullOrWhiteSpace(audioRequestId) ||
                audioRequestId.Length < 16 ||
                audioRequestId.Length > 128)
            {
                return false;
            }

            if (!json.ContainsKey("audio_stream_port") ||
                !TryParsePortAllowDisabled(json["audio_stream_port"], out audioStreamPort))
            {
                return false;
            }

            if (!json.ContainsKey("microphone_upload_port") ||
                !TryParsePortAllowDisabled(
                    json["microphone_upload_port"],
                    out microphoneUploadPort))
            {
                return false;
            }

            if (microphoneUploadPort > 0)
            {
                bool protocolMatches = json.ContainsKey("microphone_upload_protocol") &&
                                       string.Equals(
                                           json["microphone_upload_protocol"].ToString(),
                                           MicrophoneUploadProtocol,
                                           StringComparison.Ordinal);
                string token = json.ContainsKey("microphone_upload_token")
                    ? json["microphone_upload_token"].ToString()
                    : null;
                bool tokenValid = !string.IsNullOrWhiteSpace(token) &&
                                  Encoding.ASCII.GetByteCount(token) >= 16 &&
                                  Encoding.ASCII.GetByteCount(token) <= 256;
                if (!protocolMatches || !tokenValid)
                {
                    microphoneUploadPort = 0;
                }
                else
                {
                    microphoneUploadToken = token;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryParsePortAllowDisabled(JsonData value, out int port)
    {
        port = 0;
        return value != null &&
               int.TryParse(value.ToString(), out port) &&
               port >= 0 &&
               port <= 65535;
    }

    private static bool MatchesOptionalInt(JsonData json, string key, int expected)
    {
        if (!json.ContainsKey(key))
        {
            return true;
        }

        return int.TryParse(json[key].ToString(), out int value) && value == expected;
    }

    private static bool MatchesOptionalString(JsonData json, string key, string expected)
    {
        return !json.ContainsKey(key) ||
               string.Equals(json[key].ToString(), expected, StringComparison.Ordinal);
    }

    private void EnsureVideoProjectionRenderer()
    {
        if (RemoteCameraWindowObj == null)
        {
            return;
        }
        if (videoProjectionRenderer == null)
        {
            videoProjectionRenderer =
                RemoteCameraWindowObj.GetComponent<RemoteVideoProjectionRenderer>();
            if (videoProjectionRenderer == null)
            {
                videoProjectionRenderer =
                    RemoteCameraWindowObj.AddComponent<RemoteVideoProjectionRenderer>();
            }
        }
        videoProjectionRenderer.SetLegacyStereoRenderer(setLere);
    }

    private void EnsureRecordStatusOverlay()
    {
        if (RemoteCameraWindowObj == null)
        {
            return;
        }
        if (recordStatusOverlay == null)
        {
            recordStatusOverlay =
                RemoteCameraWindowObj.GetComponent<RemoteRecordStatusOverlay>();
            if (recordStatusOverlay == null)
            {
                recordStatusOverlay =
                    RemoteCameraWindowObj.AddComponent<RemoteRecordStatusOverlay>();
            }
        }
        recordStatusOverlay.Configure();
    }

    private string CurrentSourceCamera()
    {
        var source = VideoSourceConfigManager.Instance != null
            ? VideoSourceConfigManager.Instance.CurrentVideoSource
            : null;
        return source != null ? source.camera : null;
    }

    private bool IsCurrentSourcePanorama()
    {
        return string.Equals(CurrentSourceCamera(), "PANORAMA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Full Remote Vision teardown used by Listen-off.
    /// Order matters: projection/LERE first, then decoder window.
    /// </summary>
    private void StopRemoteVisionSession()
    {
        controlReconnectSuppressed = true;
        CrashProbe.Breadcrumb("remote_vision.stop");
        // Send CLOSE_CAMERA and flush before tearing the socket down so the
        // source headset actually leaves preview (high-bitrate 2160 sessions
        // otherwise look like the official "camera stuck open" failure).
        if (controlClientConnected && operatorControlClient != null)
        {
            operatorControlClient.SendCommand(NetworkCommand.CLOSE_CAMERA, new byte[0]);
            operatorControlClient.FlushSend();
        }
        if (operatorControlClient != null && operatorControlClient.IsRunning)
        {
            operatorControlClient.DisconnectSilently();
        }
        controlClientConnected = false;
        StopCameraRequestCoroutine();
        StopDuplexAudio();
        recordStatusOverlay?.Clear();

        EnsureVideoProjectionRenderer();
        videoProjectionRenderer?.ShutdownSession();
        if (setLere != null)
        {
            setLere.ForceSessionOff();
        }

        if (RemoteCameraWindowObj != null)
        {
            var window = RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>();
            window?.StopListening();
            RemoteCameraWindowObj.SetActive(false);
        }

        SetRemoteVisionStatus("Idle");
        if (listenBtn != null)
        {
            listenBtn.SetOn(false);
        }
    }

    private void ApplyRecordStatus(byte[] payload)
    {
        EnsureRecordStatusOverlay();
        if (recordStatusOverlay == null ||
            !RemoteRecordStatusSnapshot.TryParse(payload, out RemoteRecordStatusSnapshot status))
        {
            CrashProbe.Breadcrumb(
                "record_status.invalid",
                $"bytes={payload?.Length ?? 0}",
                LogType.Warning);
            return;
        }
        if (!string.Equals(
                lastRecordStatusBreadcrumbState,
                status.State,
                StringComparison.Ordinal))
        {
            lastRecordStatusBreadcrumbState = status.State;
            CrashProbe.Breadcrumb(
                "record_status.received",
                $"state={status.State} ep={status.Episode} frames={status.FrameCount}");
        }
        if (!recordStatusOverlay.Apply(status))
        {
            CrashProbe.Breadcrumb(
                "record_status.overlay_unavailable",
                $"state={status.State}",
                LogType.Warning);
        }
    }

    private void ApplyVideoProjection(string projection, string stereoLayout)
    {
        EnsureVideoProjectionRenderer();
        if (videoProjectionRenderer == null)
        {
            return;
        }

        // Dropdown Panorama wins over a missing/flat AUDIO_CONFIG default so we
        // never drop back to floating RawImage mid-session (mesh flicker).
        if (IsCurrentSourcePanorama())
        {
            projection = RemoteVideoProjectionRenderer.EquirectangularProjection;
            if (string.IsNullOrEmpty(stereoLayout) ||
                string.Equals(
                    stereoLayout,
                    RemoteVideoProjectionRenderer.MonoLayout,
                    StringComparison.Ordinal))
            {
                stereoLayout = RemoteVideoProjectionRenderer.SideBySideLayout;
            }
        }

        videoProjectionRenderer.Configure(projection, stereoLayout);
        CrashProbe.Breadcrumb(
            "video_projection.apply",
            $"projection={projection} layout={stereoLayout}");
        LogWindow.Info(
            $"Remote video projection={projection}, stereo_layout={stereoLayout}.");
    }


    private int BeginAudioSession(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return -1;
        }

        StopDuplexAudio(false);
        activeAudioHost = ip.Trim();
        negotiatedAudioStreamPort = 0;
        audioDownlinkExplicitlyDisabled = false;
        negotiatedMicrophoneUploadPort = 0;
        negotiatedMicrophoneUploadToken = null;
        audioPortAckReceived = false;
        acceptAudioPortConfig = false;
        duplexAudioStarted = false;
        microphonePermissionRequested = false;
        clientProtocolBuffer.Clear();
        recordStatusOverlay?.Clear();
        currentAudioRequestId = Guid.NewGuid().ToString("N");
        CrashProbe.Breadcrumb(
            "audio_session.begin",
            $"host={activeAudioHost} request_id={currentAudioRequestId}");
        EnsureVideoProjectionRenderer();
        if (IsCurrentSourcePanorama())
        {
            // Stable skybox from the first frame; no LE/RE or floating flash.
            videoProjectionRenderer?.PreparePendingPanorama();
        }
        else
        {
            // PICO4U / ZEDMINI: flat SBS negotiation; LE/RE armed in Listen coroutine.
            ApplyVideoProjection(
                RemoteVideoProjectionRenderer.FlatProjection,
                RemoteVideoProjectionRenderer.SideBySideLayout);
        }
        return ++audioSessionId;
    }

    private void StartDuplexAudioForSession(string ip, int sessionId)
    {
        if (sessionId != audioSessionId ||
            string.IsNullOrWhiteSpace(ip) ||
            listenBtn == null ||
            !listenBtn.On)
        {
            return;
        }

        activeAudioHost = ip.Trim();
        EnsureAudioComponents();
        int remoteAudioPort = GetRemoteAudioPort();
        if (remoteAudioPort > 0)
        {
            CrashProbe.Breadcrumb("audio_downlink.start", $"{activeAudioHost}:{remoteAudioPort}");
            remoteAudioPlayer.StartAudio(activeAudioHost, remoteAudioPort);
        }
        else
        {
            CrashProbe.Breadcrumb("audio_downlink.disabled");
            remoteAudioPlayer.StopAudio();
        }

        if (GetMicrophoneUploadPort() <= 0 ||
            string.IsNullOrEmpty(negotiatedMicrophoneUploadToken))
        {
            if (microphoneStartCoroutine != null)
            {
                StopCoroutine(microphoneStartCoroutine);
                microphoneStartCoroutine = null;
            }
            if (microphoneStreamer != null)
            {
                microphoneStreamer.StopStreaming();
            }
            LogWindow.Warn(
                "Secure Pico microphone session was not negotiated; uplink remains disabled.");
            CrashProbe.Breadcrumb("microphone_uplink.not_negotiated", "", LogType.Warning);
            duplexAudioStarted = true;
            return;
        }

        if (microphoneStartCoroutine != null)
        {
            StopCoroutine(microphoneStartCoroutine);
        }

        microphoneStartCoroutine = StartCoroutine(
            StartMicrophoneWhenAuthorized(
                activeAudioHost,
                GetMicrophoneUploadPort(),
                negotiatedMicrophoneUploadToken,
                sessionId));
        CrashProbe.Breadcrumb("microphone_uplink.authorizing", $"{activeAudioHost}:{GetMicrophoneUploadPort()}");
        duplexAudioStarted = true;
    }

    private IEnumerator StartMicrophoneWhenAuthorized(
        string host,
        int port,
        string sessionToken,
        int sessionId)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!PicoMicrophoneStreamer.HasRecordPermission() && !microphonePermissionRequested)
        {
            microphonePermissionRequested = true;
            PicoMicrophoneStreamer.RequestRecordPermission();
            float permissionDeadline = Time.realtimeSinceStartup + MicrophonePermissionTimeoutSeconds;
            while (sessionId == audioSessionId &&
                   listenBtn != null &&
                   listenBtn.On &&
                   RemoteCameraWindowObj != null &&
                   RemoteCameraWindowObj.activeInHierarchy &&
                   !PicoMicrophoneStreamer.HasRecordPermission() &&
                   Time.realtimeSinceStartup < permissionDeadline)
            {
                yield return null;
            }
        }
#endif

        if (sessionId != audioSessionId ||
            listenBtn == null ||
            !listenBtn.On ||
            RemoteCameraWindowObj == null ||
            !RemoteCameraWindowObj.activeInHierarchy)
        {
            microphoneStartCoroutine = null;
            yield break;
        }

        if (!PicoMicrophoneStreamer.HasRecordPermission())
        {
            LogWindow.Warn("Pico microphone upload not started: RECORD_AUDIO permission was denied or timed out.");
            CrashProbe.Breadcrumb("microphone_uplink.permission_denied", "", LogType.Warning);
            Toast.Show("Microphone permission denied; voice upload is disabled.");
            microphoneStartCoroutine = null;
            yield break;
        }

        microphoneStreamer.SetMuted(microphoneMuted);
        microphoneStreamer.StartStreaming(host, port, sessionToken);
        CrashProbe.Breadcrumb("microphone_uplink.start", $"{host}:{port}");
        microphoneStartCoroutine = null;
    }

    private void StopDuplexAudio(bool clearHost = true)
    {
        CrashProbe.Breadcrumb(
            "duplex_audio.stop",
            $"clear_host={clearHost} had_host={!string.IsNullOrEmpty(activeAudioHost)}");
        audioSessionId++;
        duplexAudioStarted = false;

        if (microphoneStartCoroutine != null)
        {
            StopCoroutine(microphoneStartCoroutine);
            microphoneStartCoroutine = null;
        }

        if (remoteAudioPlayer != null)
        {
            remoteAudioPlayer.StopAudio();
        }

        if (microphoneStreamer != null)
        {
            microphoneStreamer.StopStreaming();
        }

        negotiatedAudioStreamPort = 0;
        audioDownlinkExplicitlyDisabled = false;
        negotiatedMicrophoneUploadPort = 0;
        negotiatedMicrophoneUploadToken = null;
        audioPortAckReceived = false;
        acceptAudioPortConfig = false;
        microphonePermissionRequested = false;
        currentAudioRequestId = null;
        clientProtocolBuffer.Clear();

        if (clearHost)
        {
            activeAudioHost = null;
        }
    }

    private void EnsureAudioComponents()
    {
        if (remoteAudioPlayer == null)
        {
            remoteAudioPlayer = RemoteCameraWindowObj.GetComponent<RemotePcmAudioPlayer>();
            if (remoteAudioPlayer == null)
            {
                remoteAudioPlayer = RemoteCameraWindowObj.AddComponent<RemotePcmAudioPlayer>();
            }
        }

        if (microphoneStreamer == null)
        {
            microphoneStreamer = RemoteCameraWindowObj.GetComponent<PicoMicrophoneStreamer>();
            if (microphoneStreamer == null)
            {
                microphoneStreamer = RemoteCameraWindowObj.AddComponent<PicoMicrophoneStreamer>();
            }
        }
    }

    private int GetRemoteAudioPort()
    {
        if (audioDownlinkExplicitlyDisabled)
        {
            return 0;
        }

        if (negotiatedAudioStreamPort > 0)
        {
            return negotiatedAudioStreamPort;
        }

        if (VideoSourceConfigManager.Instance == null)
        {
            return RemotePcmAudioPlayer.DefaultPort;
        }

        int configuredPort = VideoSourceConfigManager.Instance.AudioStreamPort;
        return configuredPort > 0 ? configuredPort : RemotePcmAudioPlayer.DefaultPort;
    }

    private int GetMicrophoneUploadPort()
    {
        return negotiatedMicrophoneUploadPort;
    }

    public void SetMicrophoneMuted(bool muted)
    {
        microphoneMuted = muted;
        if (microphoneStreamer != null)
        {
            microphoneStreamer.SetMuted(muted);
        }
    }

    private void StopCameraRequestCoroutine()
    {
        if (cameraRequestCoroutine == null)
        {
            return;
        }

        StopCoroutine(cameraRequestCoroutine);
        cameraRequestCoroutine = null;
    }


    private void RunControlConnectionWatchdog()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextControlWatchdogRealtime)
        {
            return;
        }
        nextControlWatchdogRealtime = now + ControlWatchdogIntervalSeconds;

        if (controlReconnectSuppressed ||
            cameraRequestCoroutine != null ||
            listenBtn == null ||
            !listenBtn.On ||
            string.IsNullOrEmpty(activeAudioHost) ||
            operatorControlClient == null ||
            operatorControlClient.IsConnected)
        {
            return;
        }

        var window = RemoteCameraWindowObj != null
            ? RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>()
            : null;
        if (window != null && window.IsListening)
        {
            // Control socket idle/drop is OK while MediaDecoder is already up.
            return;
        }

        CrashProbe.Breadcrumb(
            "operator_control.watchdog_reconnect",
            activeAudioHost,
            LogType.Warning);
        LogWindow.Warn("Operator control watchdog detected a lost session; renegotiating.");
        RequestCameraStream(activeAudioHost);
    }


    private void OnDisable()
    {
        CrashProbe.Breadcrumb("camera_ctrl.disable");
        resumeDuplexAudioAfterPause = resumeDuplexAudioAfterPause ||
                                      (listenBtn != null &&
                                       listenBtn.On &&
                                       !string.IsNullOrEmpty(activeAudioHost));
        StopCameraRequestCoroutine();
        StopDuplexAudio(false);
        controlReconnectSuppressed = true;
        if (operatorControlClient != null && operatorControlClient.IsRunning)
        {
            operatorControlClient.DisconnectSilently();
        }
        controlClientConnected = false;
    }

    private void OnEnable()
    {
        CrashProbe.Breadcrumb("camera_ctrl.enable");
        if (resumeDuplexAudioAfterPause &&
            listenBtn != null &&
            listenBtn.On &&
            !string.IsNullOrEmpty(activeAudioHost))
        {
            controlReconnectSuppressed = false;
            RequestCameraStream(activeAudioHost);
            resumeDuplexAudioAfterPause = false;
        }
    }

}
