#if !PICO_OPENXR_SDK
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace Pico.CameraCapture
{
    /// <summary>
    /// Records HMD + controller poses into poses.jsonl while a session is active.
    /// Uses mono_ns aligned with CameraFrameCapture; wall_time_utc for coarse sync.
    /// </summary>
    public class PoseRecorder : MonoBehaviour
    {
        CaptureSettings _settings;
        StreamWriter _writer;
        float _interval;
        float _accum;
        int _count;
        bool _active;

        public int WrittenCount => _count;
        public bool IsActive => _active;

        public void Begin(string sessionDir, CaptureSettings settings)
        {
            End();
            _settings = settings ?? new CaptureSettings();
            int hz = Mathf.Clamp(_settings.poseFps > 0 ? _settings.poseFps : 50, 1, 120);
            _interval = 1f / hz;
            _accum = 0f;
            _count = 0;

            Directory.CreateDirectory(sessionDir);
            string path = Path.Combine(sessionDir, "poses.jsonl");
            _writer = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
            _active = true;
            Debug.Log($"[CameraCapture] PoseRecorder started @ {hz}Hz → {path}");
        }

        public void End()
        {
            _active = false;
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Dispose();
                _writer = null;
            }
        }

        void OnDestroy()
        {
            End();
        }

        void Update()
        {
            if (!_active || _writer == null) return;

            _accum += Time.deltaTime;
            if (_accum < _interval) return;
            _accum = 0f;

            long monoNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000.0);
            string wall = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            TryNode(XRNode.Head, out bool headOk, out Vector3 hp, out Quaternion hr);
            TryNode(XRNode.LeftHand, out bool leftOk, out Vector3 lp, out Quaternion lr);
            TryNode(XRNode.RightHand, out bool rightOk, out Vector3 rp, out Quaternion rr);

            var sb = new StringBuilder(384);
            sb.Append('{');
            sb.Append("\"mono_ns\":").Append(monoNs).Append(',');
            sb.Append("\"wall_time_utc\":\"").Append(wall).Append("\",");
            sb.Append("\"clock_domain\":\"realtimeSinceStartup_ns\",");
            AppendPose(sb, "head", headOk, hp, hr);
            sb.Append(',');
            AppendPose(sb, "left_controller", leftOk, lp, lr);
            sb.Append(',');
            AppendPose(sb, "right_controller", rightOk, rp, rr);
            sb.Append('}');

            _writer.WriteLine(sb.ToString());
            _count++;
        }

        static void TryNode(XRNode node, out bool ok, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var device = InputDevices.GetDeviceAtXRNode(node);
            ok = device.isValid
                 && device.TryGetFeatureValue(CommonUsages.devicePosition, out pos)
                 && device.TryGetFeatureValue(CommonUsages.deviceRotation, out rot);
            if (!ok)
            {
                pos = Vector3.zero;
                rot = Quaternion.identity;
            }
        }

        static void AppendPose(StringBuilder sb, string name, bool ok, Vector3 p, Quaternion r)
        {
            sb.Append('"').Append(name).Append("\":{");
            sb.Append("\"tracked\":").Append(ok ? "true" : "false").Append(',');
            sb.Append("\"px\":").Append(F(p.x)).Append(',');
            sb.Append("\"py\":").Append(F(p.y)).Append(',');
            sb.Append("\"pz\":").Append(F(p.z)).Append(',');
            sb.Append("\"qx\":").Append(F(r.x)).Append(',');
            sb.Append("\"qy\":").Append(F(r.y)).Append(',');
            sb.Append("\"qz\":").Append(F(r.z)).Append(',');
            sb.Append("\"qw\":").Append(F(r.w));
            sb.Append('}');
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
#endif
