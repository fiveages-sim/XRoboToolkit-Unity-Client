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
    /// for Enterprise direct main-path packing.
    /// Default: Hand and Tracker (Body/Motion) follow panel CollectionRate
    /// (SharedEnterpriseSampleHz), same as Head/Controller. Override &gt;0 only when
    /// deliberately decoupling. TcpHandler.autoThrottle can still lower via SharedAuxThrottleHz.
    /// Failures clear that stream's cache (no stale Hand left attached to Head packets).
    /// Wire protocol unchanged — missing Hand key means no fresh sample (same as before first hit).
    /// </summary>
    public class EnterpriseAuxTrackingSampler : MonoBehaviour
    {
        private const string Tag = "EnterpriseAuxTrackingSampler";

        [SerializeField] private bool autoStart = true;
        /// <summary>
        /// Hand poll/publish attempt rate. 0 = follow panel SharedEnterpriseSampleHz.
        /// </summary>
        [SerializeField] private int handSampleHz = 0;
        /// <summary>
        /// Body/Motion (tracker) poll rate. 0 = follow panel SharedEnterpriseSampleHz.
        /// </summary>
        [SerializeField] private int trackerSampleHz = 0;

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
        private static ulong s_lastHandRawFingerprint;
        private static Vector3 s_lastHandPalm;
        private static bool s_lastHandPalmValid;
        private static long s_lastHandPublishTicks;
        // Palm |Δ| below this ⇒ treat as repeat poll of the same SDK frame (meters).
        private const float HandNearDupPalmM = 1e-5f;
        // Palm |Δ| at/above this ⇒ clear true motion / new recognition frame (meters).
        private const float HandTrueChangePalmM = 5e-4f;
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
            Debug.Log($"{Tag} started (" +
                      $"handHz={(handSampleHz > 0 ? handSampleHz.ToString() : "panel")}, " +
                      $"trackerHz={(trackerSampleHz > 0 ? trackerSampleHz.ToString() : "panel")}, " +
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
                s_lastHandRawFingerprint = 0;
                s_lastHandPalmValid = false;
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
            long handUnchangedCount = 0;
            long handSampleOkCount = 0;
            long leftOkCount = 0;
            long rightOkCount = 0;
            long leftActiveCount = 0;
            long rightActiveCount = 0;
            long activeMismatchCount = 0;
            long oneSideMissCount = 0;
            long handRawChangedCount = 0;
            long handQuantChangedCount = 0;
            long handNearDupCount = 0;
            long handTrueChangeCount = 0;
            long handMicroChangeCount = 0;
            double handNearDupDeltaSum = 0.0;
            double handTrueChangeDeltaSum = 0.0;
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
                        ref handUnchangedCount,
                        ref handSampleOkCount,
                        ref leftOkCount,
                        ref rightOkCount,
                        ref leftActiveCount,
                        ref rightActiveCount,
                        ref activeMismatchCount,
                        ref oneSideMissCount,
                        ref handRawChangedCount,
                        ref handQuantChangedCount,
                        ref handNearDupCount,
                        ref handTrueChangeCount,
                        ref handMicroChangeCount,
                        ref handNearDupDeltaSum,
                        ref handTrueChangeDeltaSum,
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
            ref long handUnchangedCount,
            ref long handSampleOkCount,
            ref long leftOkCount,
            ref long rightOkCount,
            ref long leftActiveCount,
            ref long rightActiveCount,
            ref long activeMismatchCount,
            ref long oneSideMissCount,
            ref long handRawChangedCount,
            ref long handQuantChangedCount,
            ref long handNearDupCount,
            ref long handTrueChangeCount,
            ref long handMicroChangeCount,
            ref double handNearDupDeltaSum,
            ref double handTrueChangeDeltaSum,
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
                int panelHz = EnterpriseCollectionRecorder.SharedEnterpriseSampleHz;
                int throttleHz = TcpHandler.SharedAuxThrottleHz;
                // Panel CollectionRate is the single source of truth unless inspector override >0.
                int configuredHandHz = handSampleHz > 0 ? handSampleHz : panelHz;
                int configuredTrackerHz = trackerSampleHz > 0 ? trackerSampleHz : panelHz;
                // Auto-throttle never raises above configured; can lower toward min under budget.
                int effectiveHandHz = configuredHandHz > 0
                    ? Math.Min(configuredHandHz, throttleHz)
                    : throttleHz;
                int effectiveTrackerHz = configuredTrackerHz > 0
                    ? Math.Min(configuredTrackerHz, throttleHz)
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
                    // Dual fingerprint: raw (any float bit) vs quantized (ignore micro noise).
                    TrackingData.HandSampleResult handResult = _trackingData.TrySampleHand(
                        s_lastHandFingerprint,
                        s_lastHandPalm,
                        s_lastHandPalmValid,
                        buildJsonEvenIfUnchanged: false,
                        out JsonData handJson,
                        out ulong handFingerprint,
                        out TrackingData.HandSampleInfo handInfo);
                    if (handResult != TrackingData.HandSampleResult.Failed)
                    {
                        handSampleOkCount++;
                        if (s_lastHandRawFingerprint != 0 &&
                            handInfo.RawFingerprint != s_lastHandRawFingerprint)
                        {
                            handRawChangedCount++;
                        }

                        if (handResult == TrackingData.HandSampleResult.Changed)
                        {
                            handQuantChangedCount++;
                        }

                        if (handInfo.PalmDeltaM >= 0f)
                        {
                            if (handInfo.PalmDeltaM < HandNearDupPalmM)
                            {
                                handNearDupCount++;
                                handNearDupDeltaSum += handInfo.PalmDeltaM;
                            }
                            else if (handInfo.PalmDeltaM >= HandTrueChangePalmM)
                            {
                                handTrueChangeCount++;
                                handTrueChangeDeltaSum += handInfo.PalmDeltaM;
                            }
                            else
                            {
                                // Between near-dup and true-change: float noise or very slow motion.
                                handMicroChangeCount++;
                            }
                        }

                        // Update continuity refs for next poll (even when Unchanged).
                        s_lastHandRawFingerprint = handInfo.RawFingerprint;
                        if (handInfo.LeftPalmValid && handInfo.RightPalmValid)
                        {
                            s_lastHandPalm = 0.5f * (handInfo.LeftPalm + handInfo.RightPalm);
                            s_lastHandPalmValid = true;
                        }
                        else if (handInfo.LeftPalmValid)
                        {
                            s_lastHandPalm = handInfo.LeftPalm;
                            s_lastHandPalmValid = true;
                        }
                        else if (handInfo.RightPalmValid)
                        {
                            s_lastHandPalm = handInfo.RightPalm;
                            s_lastHandPalmValid = true;
                        }

                        if (handInfo.LeftOk)
                        {
                            leftOkCount++;
                            if (handInfo.LeftIsActive != 0U)
                            {
                                leftActiveCount++;
                            }
                        }

                        if (handInfo.RightOk)
                        {
                            rightOkCount++;
                            if (handInfo.RightIsActive != 0U)
                            {
                                rightActiveCount++;
                            }
                        }

                        if (handInfo.LeftOk != handInfo.RightOk)
                        {
                            oneSideMissCount++;
                        }

                        if (handInfo.LeftOk && handInfo.RightOk &&
                            (handInfo.LeftIsActive != 0U) != (handInfo.RightIsActive != 0U))
                        {
                            activeMismatchCount++;
                        }
                    }

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
                        handUnchangedCount++;
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

                    AdvanceDue(ref nextBodyTicks, nowTicks, effectiveTrackerHz);
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

                    AdvanceDue(ref nextMotionTicks, nowTicks, effectiveTrackerHz);
                }
                else if (!wantMotion)
                {
                    ClearMotion();
                    motionFailSinceTicks = 0;
                }

                // Loop wake: max of enabled aux cadences (all follow panel unless overridden).
                int loopHz = 0;
                if (wantHand)
                {
                    loopHz = Math.Max(loopHz, effectiveHandHz);
                }

                if (wantBody || wantMotion)
                {
                    loopHz = Math.Max(loopHz, effectiveTrackerHz);
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
                    double unchangedHz = handUnchangedCount / diagElapsed;
                    double leftDuty = leftOkCount > 0
                        ? leftActiveCount / (double)leftOkCount
                        : 0.0;
                    double rightDuty = rightOkCount > 0
                        ? rightActiveCount / (double)rightOkCount
                        : 0.0;
                    double mismatchFrac = handSampleOkCount > 0
                        ? activeMismatchCount / (double)handSampleOkCount
                        : 0.0;
                    double oneSideFrac = handSampleOkCount > 0
                        ? oneSideMissCount / (double)handSampleOkCount
                        : 0.0;
                    double pollHz = handSampleOkCount / diagElapsed;
                    double rawHz = handRawChangedCount / diagElapsed;
                    double quantHz = handQuantChangedCount / diagElapsed;
                    double nearDupHz = handNearDupCount / diagElapsed;
                    double trueHz = handTrueChangeCount / diagElapsed;
                    double microHz = handMicroChangeCount / diagElapsed;
                    double nearDupMeanMm = handNearDupCount > 0
                        ? 1000.0 * handNearDupDeltaSum / handNearDupCount
                        : 0.0;
                    double trueMeanMm = handTrueChangeCount > 0
                        ? 1000.0 * handTrueChangeDeltaSum / handTrueChangeCount
                        : 0.0;
                    // Resource hint: quantHz ≈ useful content rate after ignoring float noise.
                    // trueHz ≈ clear motion frames; nearDupHz ≈ same SDK frame re-read.
                    Debug.Log($"{Tag}hand diag: effectiveHz={effectiveHandHz} " +
                              $"throttle={throttleHz} panel={panelHz} " +
                              $"handCfg={(handSampleHz > 0 ? handSampleHz.ToString() : "0/panel")} " +
                              $"trackerCfg={(trackerSampleHz > 0 ? trackerSampleHz.ToString() : "0/panel")} " +
                              $"trackerHz={effectiveTrackerHz} " +
                              $"publishHz={pubHz:F1} unchangedHz={unchangedHz:F1} failHz={failHz:F1} | " +
                              $"poll={pollHz:F1} rawUnique={rawHz:F1} quantUnique={quantHz:F1} " +
                              $"(~trueUsefulHz) nearDup={nearDupHz:F1} trueΔ={trueHz:F1} " +
                              $"microΔ={microHz:F1} nearDupMean={nearDupMeanMm:F3}mm " +
                              $"trueMean={trueMeanMm:F2}mm | " +
                              $"isActive L/R duty={leftDuty:F2}/{rightDuty:F2} " +
                              $"ok={leftOkCount}/{rightOkCount} " +
                              $"activeMismatch={mismatchFrac:P0} oneSideMiss={oneSideFrac:P0}");
                    handPublishCount = 0;
                    handFailCount = 0;
                    handUnchangedCount = 0;
                    handSampleOkCount = 0;
                    leftOkCount = 0;
                    rightOkCount = 0;
                    leftActiveCount = 0;
                    rightActiveCount = 0;
                    activeMismatchCount = 0;
                    oneSideMissCount = 0;
                    handRawChangedCount = 0;
                    handQuantChangedCount = 0;
                    handNearDupCount = 0;
                    handTrueChangeCount = 0;
                    handMicroChangeCount = 0;
                    handNearDupDeltaSum = 0.0;
                    handTrueChangeDeltaSum = 0.0;
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
                    s_lastHandRawFingerprint = 0;
                    s_lastHandPalmValid = false;
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
