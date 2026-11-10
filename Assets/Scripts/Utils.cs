using System;
using System.Net;
using System.Net.Sockets;
using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;

namespace Robot
{
    public class Utils
    {
        /// <summary>
        /// Legacy wall-clock value used by older paths: ms since Unix epoch scaled by 1e6
        /// (not true nanosecond resolution — duplicates under ~1kHz). Prefer
        /// <see cref="GetElapsedRealtimeNs"/> for Tracking timeStampNs and
        /// <see cref="GetUnixTimeNs"/> for wall clock.
        /// </summary>
        public static long GetCurrentTimestamp()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long currentTimestamp = (long)((DateTime.UtcNow - epoch).TotalMilliseconds * 1000000);
            return currentTimestamp;
        }

        /// <summary>
        /// Unix epoch wall-clock nanoseconds (DateTime ticks; typically better than ms*1e6).
        /// May still jump with NTP; not the same family as PICO frame timestamps.
        /// </summary>
        public static long GetUnixTimeNs()
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            return (DateTime.UtcNow - epoch).Ticks * 100L;
        }

        /// <summary>
        /// CLOCK_BOOTTIME nanoseconds from android.os.SystemClock.elapsedRealtimeNanos.
        /// Same clock family as PICO Frame.timestamp (exposure start, ns, BOOTTIME).
        /// Monotonic for Tracking timeStampNs / fa-py frame gate. Not Unix time / MediaCodec PTS.
        /// </summary>
        private static AndroidJavaClass _elapsedClock;
        // Once JNI fails, stick to Stopwatch so we do not oscillate BOOTTIME↔Stopwatch.
        private static bool _elapsedUseStopwatchFallback;

        public static long GetElapsedRealtimeNs()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_elapsedUseStopwatchFallback)
            {
                return GetStopwatchTimeNs();
            }

            try
            {
                if (_elapsedClock == null)
                {
                    _elapsedClock = new AndroidJavaClass("android.os.SystemClock");
                }

                return _elapsedClock.CallStatic<long>("elapsedRealtimeNanos");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Utils] elapsedRealtimeNanos failed: " + exception.Message);
                // Stay in a monotonic family — never jump to Unix wall clock (fa-py deltas explode).
                _elapsedUseStopwatchFallback = true;
                return GetStopwatchTimeNs();
            }
#else
            return GetStopwatchTimeNs();
#endif
        }

        /// <summary>
        /// Process-monotonic nanoseconds from Stopwatch (not BOOTTIME, but never jumps to Unix).
        /// </summary>
        public static long GetStopwatchTimeNs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() *
                          (1e9 / System.Diagnostics.Stopwatch.Frequency));
        }

        //Obtain the local IPv4 address
        public static string GetLocalIPv4()
        {
            string localIP = "Not found";
            foreach (IPAddress ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    localIP = ip.ToString();
                    break;
                }
            }

            return localIP;
        }

        /// <summary>
        /// Check if the local port is occupied
        /// </summary>
        /// <param name="port"></param>
        /// <returns></returns>
        public static bool IsPortAvailable(int port)
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Any, port);

                listener.Start();

                listener.Stop();

                return true;
            }
            catch (SocketException e)
            {
                return false;
            }
        }

        public static int GetAvailablePort()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0); // 传入 0 让系统自动分配可用端口
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public static string GetCameraIntrinsicsStrE(int width, int height)
        {
            string cameraIntrinsics = "";
            double[] intrinsics = PXR_Enterprise.GetCameraIntrinsicsfor4U(width, height, 76.35f, 61.05f);
            for (int i = 0; i < intrinsics.Length; i++)
            {
                if (i > 0)
                {
                    cameraIntrinsics += ",";
                }

                cameraIntrinsics += intrinsics[i].ToString("E16");
            }

            return cameraIntrinsics;
        }

        public static string GetCameraExtrinsicsStrE()
        {
            PXR_EnterprisePlugin.GetCameraExtrinsics(out var leftExtrinsics, out var rightExtrinsics);
            string cameraExtrinsics = "";

            //left
            for (int i = 0; i < leftExtrinsics.Length; i++)
            {
                if (i > 0)
                {
                    cameraExtrinsics += ",";
                }

                cameraExtrinsics += leftExtrinsics[i].ToString("E16");
            }


            cameraExtrinsics += "|";
            //right
            for (int i = 0; i < rightExtrinsics.Length; i++)
            {
                if (i > 0)
                {
                    cameraExtrinsics += ",";
                }

                cameraExtrinsics += rightExtrinsics[i].ToString("E16");
            }

            return cameraExtrinsics;
        }

        public static bool IsPico4U()
        {
#if UNITY_EDITOR
            return true;
#endif
            return PXR_Input.GetControllerDeviceType() == PXR_Input.ControllerDevice.PICO_4U;
        }
        
        public static void WriteLog(string tag, string msg)
        {
#if UNITY_EDITOR
            Debug.Log($"[XRoboToolkit]<color=red>{tag}</color>\t{msg}");
#else
            // Output to logcat
            Debug.Log($"[XRoboToolkit]\t{tag}\t{msg}");
#endif
        }
    }
}