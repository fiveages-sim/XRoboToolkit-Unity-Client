#if (UNITY_ANDROID && !UNITY_EDITOR)
#define PICO_PLATFORM
#endif
using System;
using System.Diagnostics;
using System.Threading;
using LitJson;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Robot
{
    /// <summary>
    /// Samples Hand / Body / Motion on a dedicated thread into latest-only caches
    /// for Enterprise direct main-path packing. Body/Motion follow SharedEnterpriseSampleHz;
    /// Hand can use a lower handSampleHz to avoid 5KB JSON work at panel rate.
    /// Failures clear that stream's cache (no stale Hand left attached to Head packets).
    /// Wire protocol unchanged — missing Hand key means no fresh sample (same as before first hit).
    /// </summary>
    public class EnterpriseAuxTrackingSampler : MonoBehaviour
    {
        private const string Tag = "EnterpriseAuxTrackingSampler";

        [SerializeField] private bool autoStart = true;
        /// <summary>
        /// Cap for Hand sampling. 0 = follow SharedEnterpriseSampleHz (recommended);
        /// TcpHandler.autoThrottle then lowers via SharedAuxThrottleHz under bandwidth pressure.
        /// </summary>
        // Cap Hand JSON build rate. 0 = follow SharedEnterpriseSampleHz (can be 300–400 and
        // burns CPU on ToString/ToJson). 120 matches typical HF hand useful rate under B0.
        [SerializeField] private int handSampleHz = 120;

        private Thread _auxThread;
        private volatile bool _running;
        private readonly TrackingData _trackingData = new TrackingData();

        private static readonly object HandLock = new object();
        private static readonly object BodyLock = new object();
        private static readonly object MotionLock = new object();
        private static string s_latestHandJson;
        private static long s_handSampleSeq;
        private static string s_latestBodyJson;
        private static long s_bodySampleSeq;
        private static string s_latestMotionJson;
        private static long s_motionSampleSeq;
        private static string s_lastPublishedHandJson;
        private static string s_lastPublishedBodyJson;
        private static string s_lastPublishedMotionJson;
        private static ulong s_lastHandFingerprint;
        private static long s_lastHandPublishTicks;
        private static long s_lastBodyPublishTicks;
        private static long s_lastMotionPublishTicks;
        // When a source is packet primary (no higher-priority source ready), identical JSON
        // must still advance seq ~20Hz so TcpHandler does not starve.
        private const double PrimaryKeepaliveHz = 20.0;
        private const double AuxFailClearSeconds = 0.05;

        public static void EnsureCreated()
        {
            if (FindObjectOfType<EnterpriseAuxTrackingSampler>() != null)
            {
                return;
            }

            GameObject go = new GameObject(nameof(EnterpriseAuxTrackingSampler));
            DontDestroyOnLoad(go);
            go.AddComponent<EnterpriseAuxTrackingSampler>();
        }

        private void Start()
        {
            if (autoStart)
            {
                StartAuxLoop();
            }
        }

        private void OnDestroy()
        {
            StopAuxLoop();
        }

        private void OnApplicationQuit()
        {
            StopAuxLoop();
        }

        public void StartAuxLoop()
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _auxThread = new Thread(AuxLoop)
            {
                IsBackground = true,
                Name = nameof(EnterpriseAuxTrackingSampler)
            };
            _auxThread.Start();
            Debug.Log($"{Tag} started (handHz={(handSampleHz > 0 ? handSampleHz.ToString() : "enterprise")}, " +
                      $"throttleCap={TcpHandler.SharedAuxThrottleHz})");
        }

        public void StopAuxLoop()
        {
            if (!_running)
            {
                return;
            }

            _running = false;
            _auxThread?.Join(1000);
            _auxThread = null;
            ClearCaches();
            Debug.Log($"{Tag} stopped");
        }

        public static bool TryGetLatestHandJson(out string handJson, out long sampleSeq)
        {
            lock (HandLock)
            {
                handJson = s_latestHandJson;
                sampleSeq = s_handSampleSeq;
                return !string.IsNullOrEmpty(handJson);
            }
        }

        public static bool TryGetLatestBodyJson(out string bodyJson, out long sampleSeq)
        {
            lock (BodyLock)
            {
                bodyJson = s_latestBodyJson;
                sampleSeq = s_bodySampleSeq;
                return !string.IsNullOrEmpty(bodyJson);
            }
        }

        public static bool TryGetLatestMotionJson(out string motionJson, out long sampleSeq)
        {
            lock (MotionLock)
            {
                motionJson = s_latestMotionJson;
                sampleSeq = s_motionSampleSeq;
                return !string.IsNullOrEmpty(motionJson);
            }
        }

        public static void ClearCaches()
        {
            lock (HandLock)
            {
                s_latestHandJson = null;
                s_lastPublishedHandJson = null;
                s_lastHandFingerprint = 0;
                s_handSampleSeq = 0;
                s_lastHandPublishTicks = 0;
            }

            lock (BodyLock)
            {
                s_latestBodyJson = null;
                s_lastPublishedBodyJson = null;
                s_bodySampleSeq = 0;
                s_lastBodyPublishTicks = 0;
            }

            lock (MotionLock)
            {
                s_latestMotionJson = null;
                s_lastPublishedMotionJson = null;
                s_motionSampleSeq = 0;
                s_lastMotionPublishTicks = 0;
            }
        }

        private void AuxLoop()
        {
#if PICO_PLATFORM
            AndroidJNI.AttachCurrentThread();
#endif
            long nextLoopTicks = Stopwatch.GetTimestamp();
            long nextHandTicks = Stopwatch.GetTimestamp();
            long nextBodyTicks = Stopwatch.GetTimestamp();
            long nextMotionTicks = Stopwatch.GetTimestamp();
            long handFailSinceTicks = 0;
            long bodyFailSinceTicks = 0;
            long motionFailSinceTicks = 0;
            long handPublishCount = 0;
            long handFailCount = 0;
            long lastDiagTicks = Stopwatch.GetTimestamp();
            // Keep last good samples briefly across SDK gaps so high-rate polling does not
            // clear on every miss (PICO joints often update ~72–90Hz).
            while (_running)
            {
                try
                {
                    AuxLoopOnce(
                        ref nextLoopTicks,
                        ref nextHandTicks,
                        ref nextBodyTicks,
                        ref nextMotionTicks,
                        ref handFailSinceTicks,
                        ref bodyFailSinceTicks,
                        ref motionFailSinceTicks,
                        ref handPublishCount,
                        ref handFailCount,
                        ref lastDiagTicks);
                }
                catch (Exception e)
                {
                    // Without this the aux thread dies silently; Hand cache freezes and B0
                    // never refreshes → PC keeps last hand pose forever.
                    Debug.LogError($"{Tag} AuxLoop: {e}");
                    Thread.Sleep(20);
                    nextLoopTicks = Stopwatch.GetTimestamp();
                }
            }
        }

        private void AuxLoopOnce(
            ref long nextLoopTicks,
            ref long nextHandTicks,
            ref long nextBodyTicks,
            ref long nextMotionTicks,
            ref long handFailSinceTicks,
            ref long bodyFailSinceTicks,
            ref long motionFailSinceTicks,
            ref long handPublishCount,
            ref long handFailCount,
            ref long lastDiagTicks)
        {
                if (!IsSamplerActive())
                {
                    ClearCaches();
                    Thread.Sleep(20);
                    nextLoopTicks = Stopwatch.GetTimestamp();
                    nextHandTicks = nextLoopTicks;
                    nextBodyTicks = nextLoopTicks;
                    nextMotionTicks = nextLoopTicks;
                    return;
                }

                bool wantHand = TrackingData.HandTrackingOn;
                bool wantBody = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Body;
                bool wantMotion = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Motion;
                if (!wantHand && !wantBody && !wantMotion)
                {
                    ClearCaches();
                    Thread.Sleep(20);
                    nextLoopTicks = Stopwatch.GetTimestamp();
                    nextHandTicks = nextLoopTicks;
                    nextBodyTicks = nextLoopTicks;
                    nextMotionTicks = nextLoopTicks;
                    return;
                }

                long nowTicks = Stopwatch.GetTimestamp();
                int enterpriseHz = EnterpriseCollectionRecorder.SharedEnterpriseSampleHz;
                int throttleHz = TcpHandler.SharedAuxThrottleHz;
                int configuredHandHz = handSampleHz > 0 ? handSampleHz : enterpriseHz;
                // Auto-throttle never raises above inspector cap; can lower toward min.
                int effectiveHandHz = configuredHandHz > 0
                    ? Math.Min(configuredHandHz, throttleHz)
                    : throttleHz;
                int effectiveBodyMotionHz = enterpriseHz > 0
                    ? Math.Min(enterpriseHz, throttleHz)
                    : throttleHz;

                // Ready (not merely toggled on) decides sidecar vs primary. HeadOn with no
                // Enterprise head yet makes Hand the tracking primary — still need keepalive.
                bool higherPriorityReady =
                    (TrackingData.HeadOn &&
                     EnterpriseCollectionRecorder.HasLatestEnterpriseHeadForTcp()) ||
                    (TrackingData.ControllerOn &&
                     EnterpriseCollectionRecorder.HasLatestEnterpriseControllerForTcp());
                bool handReadyCached;
                lock (HandLock)
                {
                    handReadyCached = !string.IsNullOrEmpty(s_latestHandJson);
                }

                // Hand on its own cadence. Only clear after sustained failures so we do not
                // thrash when polling faster than the SDK joint rate (~90Hz typical).
                if (wantHand && nowTicks >= nextHandTicks)
                {
                    bool handIsSidecar = higherPriorityReady;
                    bool keepaliveDue = !handIsSidecar && IsKeepaliveDue(
                        s_lastHandPublishTicks, nowTicks);
                    // Fingerprint raw joints first; skip ToString("R")/ToJson when unchanged.
                    TrackingData.HandSampleResult handResult = _trackingData.TrySampleHand(
                        s_lastHandFingerprint,
                        buildJsonEvenIfUnchanged: false,
                        out JsonData handJson,
                        out ulong handFingerprint);
                    if (handResult == TrackingData.HandSampleResult.Failed)
                    {
                        handFailCount++;
                        if (ShouldClearAfterFail(ref handFailSinceTicks, nowTicks))
                        {
                            ClearHand();
                        }
                    }
                    else if (handResult == TrackingData.HandSampleResult.Changed)
                    {
                        PublishHand(handJson.ToJson());
                        s_lastHandFingerprint = handFingerprint;
                        handPublishCount++;
                        handFailSinceTicks = 0;
                    }
                    else if (keepaliveDue && !string.IsNullOrEmpty(s_lastPublishedHandJson))
                    {
                        // Same pose: republish cached JSON to advance seq (no rebuild).
                        PublishHand(s_lastPublishedHandJson);
                        handPublishCount++;
                        handFailSinceTicks = 0;
                    }
                    else
                    {
                        handFailSinceTicks = 0;
                    }

                    AdvanceDue(ref nextHandTicks, nowTicks, effectiveHandHz);
                }
                else if (!wantHand)
                {
                    ClearHand();
                    handFailSinceTicks = 0;
                }

                if (wantBody && nowTicks >= nextBodyTicks)
                {
                    if (_trackingData.TryBuildBodyJson(out JsonData bodyJson))
                    {
                        string json = bodyJson.ToJson();
                        bool contentChanged = json != s_lastPublishedBodyJson;
                        // Body is sidecar if Head/Controller ready, or Hand already caching.
                        bool bodyIsSidecar = higherPriorityReady ||
                                             (TrackingData.HandTrackingOn && handReadyCached);
                        bool keepaliveDue = !bodyIsSidecar && IsKeepaliveDue(
                            s_lastBodyPublishTicks, nowTicks);
                        if (contentChanged || keepaliveDue)
                        {
                            PublishBody(json);
                        }

                        bodyFailSinceTicks = 0;
                    }
                    else if (ShouldClearAfterFail(ref bodyFailSinceTicks, nowTicks))
                    {
                        ClearBody();
                    }

                    AdvanceDue(ref nextBodyTicks, nowTicks, effectiveBodyMotionHz);
                }
                else if (!wantBody)
                {
                    ClearBody();
                    bodyFailSinceTicks = 0;
                }

                if (wantMotion && nowTicks >= nextMotionTicks)
                {
                    if (_trackingData.TryBuildMotionJson(out JsonData motionJson))
                    {
                        string json = motionJson.ToJson();
                        bool contentChanged = json != s_lastPublishedMotionJson;
                        bool bodyReadyCached;
                        lock (BodyLock)
                        {
                            bodyReadyCached = !string.IsNullOrEmpty(s_latestBodyJson);
                        }

                        bool motionIsSidecar = higherPriorityReady ||
                                               (TrackingData.HandTrackingOn && handReadyCached) ||
                                               bodyReadyCached;
                        bool keepaliveDue = !motionIsSidecar && IsKeepaliveDue(
                            s_lastMotionPublishTicks, nowTicks);
                        if (contentChanged || keepaliveDue)
                        {
                            PublishMotion(json);
                        }

                        motionFailSinceTicks = 0;
                    }
                    else if (ShouldClearAfterFail(ref motionFailSinceTicks, nowTicks))
                    {
                        ClearMotion();
                    }

                    AdvanceDue(ref nextMotionTicks, nowTicks, effectiveBodyMotionHz);
                }
                else if (!wantMotion)
                {
                    ClearMotion();
                    motionFailSinceTicks = 0;
                }

                // Loop wake: max of enabled aux cadences (Hand throttled separately).
                int loopHz = 0;
                if (wantHand)
                {
                    loopHz = Math.Max(loopHz, effectiveHandHz);
                }

                if (wantBody || wantMotion)
                {
                    loopHz = Math.Max(loopHz, effectiveBodyMotionHz);
                }

                if (loopHz <= 0)
                {
                    Thread.Sleep(50);
                    nextLoopTicks = Stopwatch.GetTimestamp();
                    return;
                }

                double diagElapsed = (nowTicks - lastDiagTicks) / (double)Stopwatch.Frequency;
                if (diagElapsed >= 1.0)
                {
                    double pubHz = handPublishCount / diagElapsed;
                    double failHz = handFailCount / diagElapsed;
                    Debug.Log($"{Tag}hand diag: effectiveHz={effectiveHandHz} " +
                              $"throttle={throttleHz} enterprise={enterpriseHz} " +
                              $"configured={(handSampleHz > 0 ? handSampleHz.ToString() : "0/follow")} " +
                              $"publishHz={pubHz:F1} failHz={failHz:F1}");
                    handPublishCount = 0;
                    handFailCount = 0;
                    lastDiagTicks = nowTicks;
                }

                long intervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)loopHz));
                nextLoopTicks += intervalTicks;
                long remainingTicks = nextLoopTicks - Stopwatch.GetTimestamp();
                if (remainingTicks > 0)
                {
                    int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 0)
                    {
                        Thread.Sleep(sleepMs);
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
                else
                {
                    nextLoopTicks = Stopwatch.GetTimestamp();
                    Thread.Yield();
                }
        }

        private static void AdvanceDue(ref long nextDueTicks, long nowTicks, int hz)
        {
            if (hz <= 0)
            {
                return;
            }

            long interval = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)hz));
            nextDueTicks += interval;
            if (nextDueTicks < nowTicks)
            {
                nextDueTicks = nowTicks;
            }
        }

        private static bool IsKeepaliveDue(long lastPublishTicks, long nowTicks)
        {
            long keepInterval = Math.Max(1,
                (long)Math.Round(Stopwatch.Frequency / PrimaryKeepaliveHz));
            return lastPublishTicks == 0 || (nowTicks - lastPublishTicks) >= keepInterval;
        }

        private static bool ShouldClearAfterFail(ref long failSinceTicks, long nowTicks)
        {
            if (failSinceTicks == 0)
            {
                failSinceTicks = nowTicks;
                return false;
            }

            if ((nowTicks - failSinceTicks) > AuxFailClearSeconds * Stopwatch.Frequency)
            {
                failSinceTicks = nowTicks;
                return true;
            }

            return false;
        }

        private static void PublishHand(string json)
        {
            lock (HandLock)
            {
                s_latestHandJson = json;
                s_lastPublishedHandJson = json;
                s_handSampleSeq++;
                s_lastHandPublishTicks = Stopwatch.GetTimestamp();
            }

            TcpHandler.NotifyTrackingSampleAvailable();
        }

        private static void PublishBody(string json)
        {
            lock (BodyLock)
            {
                s_latestBodyJson = json;
                s_lastPublishedBodyJson = json;
                s_bodySampleSeq++;
                s_lastBodyPublishTicks = Stopwatch.GetTimestamp();
            }

            TcpHandler.NotifyTrackingSampleAvailable();
        }

        private static void PublishMotion(string json)
        {
            lock (MotionLock)
            {
                s_latestMotionJson = json;
                s_lastPublishedMotionJson = json;
                s_motionSampleSeq++;
                s_lastMotionPublishTicks = Stopwatch.GetTimestamp();
            }

            TcpHandler.NotifyTrackingSampleAvailable();
        }

        private static void ClearHand()
        {
            bool cleared = false;
            lock (HandLock)
            {
                if (s_latestHandJson != null || s_handSampleSeq != 0)
                {
                    s_latestHandJson = null;
                    s_lastPublishedHandJson = null;
                    s_lastHandFingerprint = 0;
                    s_handSampleSeq = 0;
                    s_lastHandPublishTicks = 0;
                    cleared = true;
                }
            }

            if (cleared)
            {
                TcpHandler.NotifyTrackingSampleAvailable();
            }
        }

        private static void ClearBody()
        {
            bool cleared = false;
            lock (BodyLock)
            {
                if (s_latestBodyJson != null || s_bodySampleSeq != 0)
                {
                    s_latestBodyJson = null;
                    s_lastPublishedBodyJson = null;
                    s_bodySampleSeq = 0;
                    s_lastBodyPublishTicks = 0;
                    cleared = true;
                }
            }

            if (cleared)
            {
                TcpHandler.NotifyTrackingSampleAvailable();
            }
        }

        private static void ClearMotion()
        {
            bool cleared = false;
            lock (MotionLock)
            {
                if (s_latestMotionJson != null || s_motionSampleSeq != 0)
                {
                    s_latestMotionJson = null;
                    s_lastPublishedMotionJson = null;
                    s_motionSampleSeq = 0;
                    s_lastMotionPublishTicks = 0;
                    cleared = true;
                }
            }

            if (cleared)
            {
                TcpHandler.NotifyTrackingSampleAvailable();
            }
        }

        private static bool IsSamplerActive()
        {
            return TcpHandler.SendTrackingData &&
                   TcpHandler.EnterpriseDirectTrackingEnabled &&
                   TrackingDataSourceCtrl.UseEnterpriseSDK &&
                   (TrackingData.HandTrackingOn ||
                    TrackingData.TrackingTypeValue > TrackingData.TrackingType.None);
        }
    }
}
