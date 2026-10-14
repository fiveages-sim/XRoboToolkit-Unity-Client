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
    /// for Enterprise direct main-path packing. Rate follows SharedEnterpriseSampleHz.
    /// Failures only skip that stream; never blocks Head/Controller sampling.
    /// </summary>
    public class EnterpriseAuxTrackingSampler : MonoBehaviour
    {
        private const string Tag = "EnterpriseAuxTrackingSampler";

        [SerializeField] private bool autoStart = true;

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
            Debug.Log($"{Tag} started (hz follows EnterpriseSampleHz)");
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
            long nextSampleTicks = Stopwatch.GetTimestamp();
            while (_running)
            {
                if (!IsSamplerActive())
                {
                    ClearCaches();
                    Thread.Sleep(20);
                    nextSampleTicks = Stopwatch.GetTimestamp();
                    continue;
                }

                bool wantHand = TrackingData.HandTrackingOn;
                bool wantBody = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Body;
                bool wantMotion = TrackingData.TrackingTypeValue == TrackingData.TrackingType.Motion;
                if (!wantHand && !wantBody && !wantMotion)
                {
                    ClearCaches();
                    Thread.Sleep(20);
                    nextSampleTicks = Stopwatch.GetTimestamp();
                    continue;
                }

                if (wantHand && _trackingData.TryBuildHandJson(out JsonData handJson))
                {
                    PublishHand(handJson.ToJson());
                }

                if (wantBody && _trackingData.TryBuildBodyJson(out JsonData bodyJson))
                {
                    PublishBody(bodyJson.ToJson());
                }
                else if (!wantBody)
                {
                    ClearBody();
                }

                if (wantMotion && _trackingData.TryBuildMotionJson(out JsonData motionJson))
                {
                    PublishMotion(motionJson.ToJson());
                }
                else if (!wantMotion)
                {
                    ClearMotion();
                }

                if (!wantHand)
                {
                    ClearHand();
                }

                int sampleHz = EnterpriseCollectionRecorder.SharedEnterpriseSampleHz;
                if (sampleHz <= 0)
                {
                    Thread.Sleep(50);
                    nextSampleTicks = Stopwatch.GetTimestamp();
                    continue;
                }

                long intervalTicks = Math.Max(1, (long)Math.Round(Stopwatch.Frequency / (double)sampleHz));
                nextSampleTicks += intervalTicks;
                long remainingTicks = nextSampleTicks - Stopwatch.GetTimestamp();
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
                    nextSampleTicks = Stopwatch.GetTimestamp();
                    Thread.Yield();
                }
            }
        }

        private static void PublishHand(string json)
        {
            lock (HandLock)
            {
                s_latestHandJson = json;
                s_handSampleSeq++;
            }
        }

        private static void PublishBody(string json)
        {
            lock (BodyLock)
            {
                s_latestBodyJson = json;
                s_bodySampleSeq++;
            }
        }

        private static void PublishMotion(string json)
        {
            lock (MotionLock)
            {
                s_latestMotionJson = json;
                s_motionSampleSeq++;
            }
        }

        private static void ClearHand()
        {
            lock (HandLock)
            {
                s_latestHandJson = null;
                s_handSampleSeq = 0;
            }
        }

        private static void ClearBody()
        {
            lock (BodyLock)
            {
                s_latestBodyJson = null;
                s_bodySampleSeq = 0;
            }
        }

        private static void ClearMotion()
        {
            lock (MotionLock)
            {
                s_latestMotionJson = null;
                s_motionSampleSeq = 0;
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
