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
        [SerializeField] private int handSampleHz = 0;

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
                s_handSampleSeq = 0;
            }

            lock (BodyLock)
            {
                s_latestBodyJson = null;
                s_bodySampleSeq = 0;
            }

            lock (MotionLock)
            {
                s_latestMotionJson = null;
                s_motionSampleSeq = 0;
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
            long handPublishCount = 0;
            long handFailCount = 0;
            long lastDiagTicks = Stopwatch.GetTimestamp();
            // Keep last good Hand briefly across SDK gaps so high-rate polling does not
            // ClearHand on every miss (PICO joints often update ~72–90Hz).
            const double handFailClearSeconds = 0.05;
            while (_running)
            {
                if (!IsSamplerActive())
                {
                    ClearCaches();
                    Thread.Sleep(20);
                    nextLoopTicks = Stopwatch.GetTimestamp();
                    nextHandTicks = nextLoopTicks;
                    nextBodyTicks = nextLoopTicks;
                    nextMotionTicks = nextLoopTicks;
                    continue;
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
                    continue;
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

                // Hand on its own cadence. Only clear after sustained failures so we do not
                // thrash when polling faster than the SDK joint rate (~90Hz typical).
                if (wantHand && nowTicks >= nextHandTicks)
                {
                    if (_trackingData.TryBuildHandJson(out JsonData handJson))
                    {
                        PublishHand(handJson.ToJson());
                        handFailSinceTicks = 0;
                        handPublishCount++;
                    }
                    else
                    {
                        handFailCount++;
                        if (handFailSinceTicks == 0)
                        {
                            handFailSinceTicks = nowTicks;
                        }
                        else if ((nowTicks - handFailSinceTicks) >
                                 handFailClearSeconds * Stopwatch.Frequency)
                        {
                            ClearHand();
                            handFailSinceTicks = nowTicks;
                        }
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
                        PublishBody(bodyJson.ToJson());
                    }
                    else
                    {
                        ClearBody();
                    }

                    AdvanceDue(ref nextBodyTicks, nowTicks, effectiveBodyMotionHz);
                }
                else if (!wantBody)
                {
                    ClearBody();
                }

                if (wantMotion && nowTicks >= nextMotionTicks)
                {
                    if (_trackingData.TryBuildMotionJson(out JsonData motionJson))
                    {
                        PublishMotion(motionJson.ToJson());
                    }
                    else
                    {
                        ClearMotion();
                    }

                    AdvanceDue(ref nextMotionTicks, nowTicks, effectiveBodyMotionHz);
                }
                else if (!wantMotion)
                {
                    ClearMotion();
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
                    continue;
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

        private static void PublishHand(string json)
        {
            lock (HandLock)
            {
                s_latestHandJson = json;
                s_handSampleSeq++;
            }

            TcpHandler.NotifyTrackingSampleAvailable();
        }

        private static void PublishBody(string json)
        {
            lock (BodyLock)
            {
                s_latestBodyJson = json;
                s_bodySampleSeq++;
            }

            TcpHandler.NotifyTrackingSampleAvailable();
        }

        private static void PublishMotion(string json)
        {
            lock (MotionLock)
            {
                s_latestMotionJson = json;
                s_motionSampleSeq++;
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
                    s_handSampleSeq = 0;
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
                    s_bodySampleSeq = 0;
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
                    s_motionSampleSeq = 0;
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
