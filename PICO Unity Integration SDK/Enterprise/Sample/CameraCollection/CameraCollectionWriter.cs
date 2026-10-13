#if !PICO_OPENXR_SDK
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Pico.CameraCapture
{
    /// <summary>
    /// Writes camera frames under:
    /// Application.persistentDataPath/CameraCollection/session_&lt;yyyyMMdd_HHmmss&gt;/
    /// </summary>
    public sealed class CameraCollectionWriter : IDisposable
    {
        public sealed class FrameJob
        {
            public int Index;
            public ulong FrameTimestampNs;
            public long MonoNs;
            public string WallTimeUtc;
            public int Width;
            public int Height;
            public byte[] Payload;
            public uint DataSize;
            public string Container;
            public bool Saved;
            public bool HasCameraPose;
            public float CamPx, CamPy, CamPz;
            public float CamQx, CamQy, CamQz, CamQw;
            public int SensorStatus;
        }

        readonly string _sessionDir;
        readonly string _framesDir;
        readonly string _jsonlPath;
        readonly string _sessionJsonPath;
        readonly CaptureSettings _settings;
        readonly ConcurrentQueue<FrameJob> _queue = new ConcurrentQueue<FrameJob>();
        readonly AutoResetEvent _signal = new AutoResetEvent(false);
        readonly Thread _worker;
        readonly object _metaLock = new object();
        readonly string _sessionId;
        readonly string _wallStartUtc;

        volatile bool _running = true;
        int _enqueued;
        int _written;
        int _dropped;
        int _callbackCount;
        int _poseSamples;
        long _firstFrameTs = -1;
        long _lastFrameTs = -1;
        long _bytesWritten;
        long _datasizeSum;
        int _datasizeSamples;
        bool _completedOk;

        public string SessionDirectory => _sessionDir;
        public string SessionId => _sessionId;
        public int CallbackCount => _callbackCount;
        public int WrittenCount => _written;
        public int DroppedCount => _dropped;
        public bool CompletedOk => _completedOk;

        public CameraCollectionWriter(CaptureSettings settings)
        {
            _settings = settings ?? new CaptureSettings();
            _wallStartUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _sessionId = "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

            string root = Path.Combine(Application.persistentDataPath, _settings.collectionRootName);
            _sessionDir = Path.Combine(root, _sessionId);
            _framesDir = Path.Combine(_sessionDir, "frames");
            _jsonlPath = Path.Combine(_sessionDir, "frames.jsonl");
            _sessionJsonPath = Path.Combine(_sessionDir, "session.json");

            Directory.CreateDirectory(_framesDir);
            WriteSessionJson(finalizing: false);

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "CameraCollectionWriter"
            };
            _worker.Start();

            Debug.Log($"[CameraCapture] Session dir: {_sessionDir}");
        }

        public void SetPoseSampleCount(int count)
        {
            _poseSamples = count;
        }

        public void NotifyCallback(uint datasize)
        {
            Interlocked.Increment(ref _callbackCount);
            if (datasize > 0)
            {
                Interlocked.Add(ref _datasizeSum, datasize);
                Interlocked.Increment(ref _datasizeSamples);
            }
        }

        public bool TryEnqueue(FrameJob job)
        {
            if (job == null || job.Payload == null || job.Payload.Length == 0)
            {
                return false;
            }

            if (_settings.maxFramesToSave > 0 && _written + _enqueued >= _settings.maxFramesToSave)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }

            if (_firstFrameTs < 0)
            {
                _firstFrameTs = (long)job.FrameTimestampNs;
            }
            _lastFrameTs = (long)job.FrameTimestampNs;

            Interlocked.Increment(ref _enqueued);
            _queue.Enqueue(job);
            _signal.Set();
            return true;
        }

        void WorkerLoop()
        {
            while (_running || !_queue.IsEmpty)
            {
                if (!_queue.TryDequeue(out FrameJob job))
                {
                    _signal.WaitOne(100);
                    continue;
                }

                try
                {
                    string ext = string.Equals(job.Container, "jpeg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "rgba";
                    string fileName = $"{job.Index:D6}.{ext}";
                    string relPath = Path.Combine("frames", fileName).Replace('\\', '/');
                    string absPath = Path.Combine(_framesDir, fileName);
                    File.WriteAllBytes(absPath, job.Payload);
                    job.Saved = true;
                    Interlocked.Increment(ref _written);
                    Interlocked.Add(ref _bytesWritten, job.Payload.Length);

                    string line = BuildJsonlLine(job, relPath, ext);
                    File.AppendAllText(_jsonlPath, line + "\n", Encoding.UTF8);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[CameraCapture] Write failed index={job.Index}: {e.Message}");
                    Interlocked.Increment(ref _dropped);
                }
            }
        }

        static string BuildJsonlLine(FrameJob job, string relPath, string ext)
        {
            var sb = new StringBuilder(480);
            sb.Append('{');
            sb.Append("\"sequence\":").Append(job.Index).Append(',');
            sb.Append("\"index\":").Append(job.Index).Append(',');
            sb.Append("\"frame_timestamp_ns\":").Append(job.FrameTimestampNs).Append(',');
            sb.Append("\"timestamp\":").Append(job.FrameTimestampNs).Append(',');
            sb.Append("\"timestamp_unit\":\"ns\",");
            sb.Append("\"clock_domain\":\"BOOTTIME\",");
            sb.Append("\"mono_ns\":").Append(job.MonoNs).Append(',');
            sb.Append("\"wall_time_utc\":\"").Append(job.WallTimeUtc).Append("\",");
            sb.Append("\"path\":\"").Append(relPath).Append("\",");
            sb.Append("\"width\":").Append(job.Width).Append(',');
            sb.Append("\"height\":").Append(job.Height).Append(',');
            sb.Append("\"pixel_format\":\"").Append(ext == "jpg" ? "jpeg" : "RGBA8").Append("\",");
            sb.Append("\"container\":\"").Append(job.Container ?? ext).Append("\",");
            sb.Append("\"data_length\":").Append(job.Payload != null ? job.Payload.Length : 0).Append(',');
            sb.Append("\"datasize\":").Append(job.DataSize).Append(',');
            sb.Append("\"sensor_status\":").Append(job.SensorStatus).Append(',');
            if (job.HasCameraPose)
            {
                sb.Append("\"camera_pose\":{");
                sb.Append("\"px\":").Append(F(job.CamPx)).Append(',');
                sb.Append("\"py\":").Append(F(job.CamPy)).Append(',');
                sb.Append("\"pz\":").Append(F(job.CamPz)).Append(',');
                sb.Append("\"qx\":").Append(F(job.CamQx)).Append(',');
                sb.Append("\"qy\":").Append(F(job.CamQy)).Append(',');
                sb.Append("\"qz\":").Append(F(job.CamQz)).Append(',');
                sb.Append("\"qw\":").Append(F(job.CamQw));
                sb.Append("},");
            }
            sb.Append("\"saved\":").Append(job.Saved ? "true" : "false");
            sb.Append('}');
            return sb.ToString();
        }

        static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        public void WriteSessionJson(bool finalizing)
        {
            lock (_metaLock)
            {
                string wallEnd = finalizing
                    ? DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                    : "";

                double durationSec = 0;
                if (_firstFrameTs >= 0 && _lastFrameTs > _firstFrameTs)
                {
                    durationSec = (_lastFrameTs - _firstFrameTs) / 1e9;
                }

                int callbacks = _callbackCount;
                int written = _written;
                double sampleHz = durationSec > 0 ? callbacks / durationSec : 0;
                double saveHz = durationSec > 0 ? written / durationSec : 0;
                double mbPerSec = durationSec > 0 ? (_bytesWritten / (1024.0 * 1024.0)) / durationSec : 0;
                double avgDatasize = _datasizeSamples > 0 ? (double)_datasizeSum / _datasizeSamples : 0;
                string container = _settings.SaveAsJpeg ? "jpeg" : "rgba";
                string pixelFormat = _settings.SaveAsJpeg ? "jpeg" : "RGBA8";

                var sb = new StringBuilder(1400);
                sb.Append("{\n");
                sb.Append("  \"capture_api\": \"for4U\",\n");
                sb.Append("  \"sessionId\": \"").Append(Escape(_sessionId)).Append("\",\n");
                sb.Append("  \"packageName\": \"").Append(Escape(_settings.packageName)).Append("\",\n");
                sb.Append("  \"applicationIdentifier\": \"").Append(Escape(Application.identifier)).Append("\",\n");
                sb.Append("  \"collectionRootName\": \"").Append(Escape(_settings.collectionRootName)).Append("\",\n");
                sb.Append("  \"sessionDirectory\": \"").Append(Escape(_sessionDir.Replace('\\', '/'))).Append("\",\n");
                sb.Append("  \"xrtSessionId\": \"").Append(Escape(_settings.xrtSessionId ?? "")).Append("\",\n");
                sb.Append("  \"width\": ").Append(_settings.width).Append(",\n");
                sb.Append("  \"height\": ").Append(_settings.height).Append(",\n");
                sb.Append("  \"eyeMode\": \"").Append(Escape(_settings.eyeMode)).Append("\",\n");
                sb.Append("  \"outputCameraRawData\": ").Append(_settings.outputCameraRawData ? "true" : "false").Append(",\n");
                sb.Append("  \"KEY_OUTPUT_CAMERA_RAW_DATA\": ").Append(_settings.outputCameraRawData ? "true" : "false").Append(",\n");
                sb.Append("  \"targetFps\": ").Append(_settings.targetFps).Append(",\n");
                sb.Append("  \"saveFps\": ").Append(_settings.saveFps).Append(",\n");
                sb.Append("  \"poseFps\": ").Append(_settings.poseFps).Append(",\n");
                sb.Append("  \"saveFormat\": \"").Append(Escape(_settings.saveFormat)).Append("\",\n");
                sb.Append("  \"jpegQuality\": ").Append(_settings.jpegQuality).Append(",\n");
                sb.Append("  \"container\": \"").Append(container).Append("\",\n");
                sb.Append("  \"pixel_format\": \"").Append(pixelFormat).Append("\",\n");
                sb.Append("  \"poseCaptured\": true,\n");
                sb.Append("  \"poseStreams\": [\"poses.jsonl:head,left_controller,right_controller\", \"frames.jsonl:camera_pose\"],\n");
                sb.Append("  \"timestampDomain\": \"BOOTTIME_ns\",\n");
                sb.Append("  \"poseClockDomain\": \"realtimeSinceStartup_ns\",\n");
                sb.Append("  \"alignNotes\": \"Align frames.jsonl.frame_timestamp_ns (BOOTTIME) with poses via wall_time_utc/mono; camera_pose is for4U Frame.pose.\",\n");
                sb.Append("  \"unrenderedDefinition\": \"Enterprise for4U camera buffer not composited with Unity scene UI. VST is display-only.\",\n");
                sb.Append("  \"previewSource\": \"for4U_buffer_downsample\",\n");
                sb.Append("  \"wallStartUtc\": \"").Append(Escape(_wallStartUtc)).Append("\",\n");
                sb.Append("  \"wallEndUtc\": \"").Append(Escape(wallEnd)).Append("\",\n");
                sb.Append("  \"firstFrameTimestampNs\": ").Append(_firstFrameTs).Append(",\n");
                sb.Append("  \"lastFrameTimestampNs\": ").Append(_lastFrameTs).Append(",\n");
                sb.Append("  \"durationSecByFrameTimestamp\": ").Append(durationSec.ToString("F6", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"totalCallbacks\": ").Append(callbacks).Append(",\n");
                sb.Append("  \"totalWritten\": ").Append(written).Append(",\n");
                sb.Append("  \"totalDropped\": ").Append(_dropped).Append(",\n");
                sb.Append("  \"totalPoseSamples\": ").Append(_poseSamples).Append(",\n");
                sb.Append("  \"bytesWritten\": ").Append(_bytesWritten).Append(",\n");
                sb.Append("  \"avgDatasize\": ").Append(avgDatasize.ToString("F1", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"sampleHz\": ").Append(sampleHz.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"saveHz\": ").Append(saveHz.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"writeMBps\": ").Append(mbPerSec.ToString("F3", CultureInfo.InvariantCulture)).Append(",\n");
                sb.Append("  \"finalized\": ").Append(finalizing ? "true" : "false").Append("\n");
                sb.Append("}\n");

                string json = sb.ToString();
                File.WriteAllText(_sessionJsonPath, json, Encoding.UTF8);
                File.WriteAllText(Path.Combine(_sessionDir, "meta.json"), json, Encoding.UTF8);
            }
        }

        void WriteCompleteMarker()
        {
            string path = Path.Combine(_sessionDir, "COMPLETE");
            string body =
                $"ok\nframes={_written}\ncallbacks={_callbackCount}\nposes={_poseSamples}\nsession={_sessionId}\n" +
                $"dir={_sessionDir.Replace('\\', '/')}\n";
            File.WriteAllText(path, body, Encoding.UTF8);
            _completedOk = true;
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            _running = false;
            _signal.Set();
            if (_worker != null && _worker.IsAlive)
            {
                _worker.Join(15000);
            }
            WriteSessionJson(finalizing: true);
            if (_written > 0 || _poseSamples > 0)
            {
                WriteCompleteMarker();
            }
            _signal.Dispose();
        }
    }
}
#endif
