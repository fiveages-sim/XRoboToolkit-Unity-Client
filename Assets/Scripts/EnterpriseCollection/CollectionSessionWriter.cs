using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using LitJson;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// CameraCollection/session_* store: per-source jsonl + meta.json schema v1.
    /// Files exist only for sources enabled in the start snapshot.
    /// </summary>
    public sealed class CollectionSessionWriter : IDisposable
    {
        public const string RootFolderName = "CameraCollection";
        public const int SchemaVersion = 1;
        private const int MaxPendingLines = 4096;
        private const int FlushIntervalMs = 250;

        public const string HeadFile = "head.jsonl";
        public const string ControllerFile = "controller.jsonl";
        public const string HandFile = "hand.jsonl";
        public const string FullBodyFile = "full_body.jsonl";
        public const string MotionTrackerFile = "motion_tracker.jsonl";
        public const string FrameMapFile = "frame_map.jsonl";
        public const string VideoFile = "CameraRecord.mp4";
        public const string MetaFile = "meta.json";

        private readonly bool _enabled;
        private readonly string _rootParent;
        private readonly ConcurrentQueue<FileLine> _pendingLines = new ConcurrentQueue<FileLine>();
        private readonly Dictionary<string, StreamWriter> _writers = new Dictionary<string, StreamWriter>();
        private readonly Dictionary<string, SourceStats> _stats = new Dictionary<string, SourceStats>();
        private readonly object _metaLock = new object();
        private readonly List<JsonData> _configChangeEvents = new List<JsonData>();

        private Thread _writerThread;
        private volatile bool _acceptingLines;
        private int _pendingCount;
        private long _droppedLineCount;
        private TrackingConfigSnapshot _snapshot;
        private string _recordingStatus = "recording";
        private string _stopReason = "";
        private string _interruptKind = "none";
        private string _interruptDetail = "";
        private string _startedUtc = "";
        private string _endedUtc = "";
        private long _startedElapsedRealtimeNs;
        private long _endedElapsedRealtimeNs;
        private int _enterpriseSampleHz;
        private int _unitySampleHz;
        private bool _videoEnabled;
        private bool _videoAvailable;
        private string _videoPathRelative = "";
        private long _videoBytes;
        private string _videoNote = "";
        private bool _diskOk = true;
        private long _diskFreeBytes;
        private long _diskRequiredBytes;
        private string _deviceModel = "";
        private bool _isPico4U;
        private JsonData _captureExtra;

        public CollectionSessionWriter(bool enabled, string rootParentDirectory)
        {
            _enabled = enabled;
            _rootParent = rootParentDirectory;
            SessionDir = "file-write-disabled";
            SessionId = "";
        }

        public string SessionId { get; private set; }
        public string SessionDir { get; private set; }
        public TrackingConfigSnapshot Snapshot => _snapshot;
        public int PendingCount => Volatile.Read(ref _pendingCount);
        public long DroppedLineCount => Interlocked.Read(ref _droppedLineCount);
        public bool IsWriterThreadAlive => _writerThread != null && _writerThread.IsAlive;

        public void Start(
            TrackingConfigSnapshot snapshot,
            int enterpriseSampleHz,
            int unitySampleHz,
            bool videoEnabled,
            string deviceModel,
            bool isPico4U,
            string preferredSessionId = null)
        {
            if (!_enabled)
            {
                SessionDir = "file-write-disabled";
                SessionId = "";
                return;
            }

            _snapshot = snapshot;
            _enterpriseSampleHz = enterpriseSampleHz;
            _unitySampleHz = unitySampleHz;
            _videoEnabled = videoEnabled;
            _videoAvailable = false;
            _deviceModel = deviceModel ?? "";
            _isPico4U = isPico4U;
            _startedUtc = DateTime.UtcNow.ToString("O");
            _startedElapsedRealtimeNs = Utils.GetElapsedRealtimeNs();
            _recordingStatus = "recording";
            _stopReason = "";
            _interruptKind = "none";
            _interruptDetail = "";

            if (!string.IsNullOrEmpty(preferredSessionId))
            {
                SessionId = preferredSessionId.StartsWith("session_", StringComparison.Ordinal)
                    ? preferredSessionId
                    : "session_" + preferredSessionId;
            }
            else
            {
                SessionId = "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            }

            string root = Path.Combine(_rootParent, RootFolderName);
            SessionDir = Path.Combine(root, SessionId);
            Directory.CreateDirectory(SessionDir);

            InitSource("head", snapshot.Head, HeadFile);
            InitSource("controller", snapshot.Controller, ControllerFile);
            InitSource("hand", snapshot.Hand, HandFile);
            InitSource("full_body", snapshot.FullBody, FullBodyFile);
            InitSource("motion_tracker", snapshot.MotionTracker, MotionTrackerFile);
            InitSource("frame_map", videoEnabled, FrameMapFile);

            // Create empty files for enabled sources so meta paths are stable.
            // frame_map may be owned by the for4U MediaCodec writer — do not pre-create it.
            foreach (KeyValuePair<string, SourceStats> pair in _stats)
            {
                if (pair.Value.EnabledAtStart &&
                    !string.IsNullOrEmpty(pair.Value.FileName) &&
                    pair.Value.FileName != FrameMapFile)
                {
                    GetWriter(pair.Value.FileName);
                }
            }

            WriteMetaFile();

            _acceptingLines = true;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "CollectionSessionWriter"
            };
            _writerThread.Start();
        }

        public void Enqueue(string sourceKey, string line, bool success)
        {
            if (!_enabled || !_acceptingLines || string.IsNullOrEmpty(line))
            {
                return;
            }

            if (!_stats.TryGetValue(sourceKey, out SourceStats stats) || !stats.EnabledAtStart)
            {
                return;
            }

            if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _droppedLineCount);
                Interlocked.Increment(ref stats.DroppedCount);
                return;
            }

            if (success)
            {
                Interlocked.Increment(ref stats.SampleCount);
            }
            else
            {
                Interlocked.Increment(ref stats.AnomalyCount);
            }

            _pendingLines.Enqueue(new FileLine(stats.FileName, line));
        }

        public void RecordConfigChange(string change, bool applied, string reason)
        {
            if (!_enabled || string.IsNullOrEmpty(change))
            {
                return;
            }

            JsonData evt = new JsonData();
            evt["utc"] = DateTime.UtcNow.ToString("O");
            evt["elapsedRealtimeNs"] = Utils.GetElapsedRealtimeNs();
            evt["change"] = change;
            evt["applied"] = applied;
            evt["reason"] = reason ?? "";
            lock (_metaLock)
            {
                _configChangeEvents.Add(evt);
            }
        }

        public void SetSourceAvailable(string sourceKey, bool available, string note = "")
        {
            if (!_stats.TryGetValue(sourceKey, out SourceStats stats))
            {
                return;
            }

            stats.Available = available;
            if (!string.IsNullOrEmpty(note))
            {
                stats.Note = note;
            }
        }

        public void SetVideoInfo(bool available, string absoluteOrRelativePath, long bytes, string note)
        {
            _videoAvailable = available;
            _videoPathRelative = string.IsNullOrEmpty(absoluteOrRelativePath)
                ? VideoFile
                : Path.GetFileName(absoluteOrRelativePath);
            _videoBytes = bytes;
            _videoNote = note ?? "";
        }

        public void SetDiskBudget(bool ok, long freeBytes, long requiredBytes)
        {
            _diskOk = ok;
            _diskFreeBytes = freeBytes;
            _diskRequiredBytes = requiredBytes;
        }

        /// <summary>
        /// Vision/for4U capture details merged into meta.json under "capture".
        /// Replaces the old separate recording_meta.json.
        /// </summary>
        public void SetCaptureExtra(JsonData captureExtra)
        {
            _captureExtra = captureExtra;
        }

        public void FlushMeta()
        {
            WriteMetaFile();
        }

        public void EnqueueFrameMap(string line)
        {
            Enqueue("frame_map", line, true);
        }

        public string GetAbsolutePath(string fileName)
        {
            return Path.Combine(SessionDir, fileName);
        }

        public StopStats Stop(string recordingStatus, string stopReason, string interruptKind, string interruptDetail,
            int joinTimeoutMs)
        {
            StopStats stats = new StopStats
            {
                PendingBeforeStop = PendingCount
            };

            _recordingStatus = string.IsNullOrEmpty(recordingStatus) ? "completed" : recordingStatus;
            _stopReason = stopReason ?? "";
            _interruptKind = string.IsNullOrEmpty(interruptKind) ? "none" : interruptKind;
            _interruptDetail = interruptDetail ?? "";
            _endedUtc = DateTime.UtcNow.ToString("O");
            _endedElapsedRealtimeNs = Utils.GetElapsedRealtimeNs();

            Stopwatch stageProfile = Stopwatch.StartNew();
            _acceptingLines = false;
            _writerThread?.Join(joinTimeoutMs);
            stats.WriterJoinMs = stageProfile.ElapsedMilliseconds;

            stageProfile.Restart();
            CloseWriters();
            WriteMetaFile();
            stats.CloseWritersMs = stageProfile.ElapsedMilliseconds;
            stats.PendingAfterStop = PendingCount;
            stats.WriterThreadAlive = IsWriterThreadAlive;
            stats.SessionDir = SessionDir;
            return stats;
        }

        public void Dispose()
        {
            if (_acceptingLines || (_writerThread != null && _writerThread.IsAlive))
            {
                Stop("aborted_user", "dispose", "user", "Dispose", 2000);
            }
        }

        private void InitSource(string key, bool enabled, string fileName)
        {
            _stats[key] = new SourceStats
            {
                EnabledAtStart = enabled,
                Available = enabled,
                FileName = enabled ? fileName : null,
                Note = ""
            };
        }

        private void WriterLoop()
        {
            Stopwatch flushTimer = Stopwatch.StartNew();
            bool wroteSinceFlush = false;
            while (_acceptingLines || !_pendingLines.IsEmpty)
            {
                if (_pendingLines.TryDequeue(out FileLine fileLine))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    StreamWriter writer = GetWriter(fileLine.FileName);
                    writer.WriteLine(fileLine.Line);
                    wroteSinceFlush = true;
                }
                else
                {
                    if (wroteSinceFlush)
                    {
                        FlushWriters();
                        wroteSinceFlush = false;
                        flushTimer.Restart();
                    }

                    Thread.Sleep(5);
                }

                if (wroteSinceFlush && flushTimer.ElapsedMilliseconds >= FlushIntervalMs)
                {
                    FlushWriters();
                    wroteSinceFlush = false;
                    flushTimer.Restart();
                }
            }

            FlushWriters();
        }

        private StreamWriter GetWriter(string fileName)
        {
            if (_writers.TryGetValue(fileName, out StreamWriter writer))
            {
                return writer;
            }

            string path = Path.Combine(SessionDir, fileName);
            // /sdcard goes through FUSE, so an implicit flush per line dominates the writer
            // thread. Buffer instead and flush on idle or every FlushIntervalMs.
            writer = new StreamWriter(path, true, new UTF8Encoding(false), 64 * 1024)
            {
                AutoFlush = false
            };
            _writers[fileName] = writer;
            return writer;
        }

        private void FlushWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                writer.Flush();
            }
        }

        private void CloseWriters()
        {
            foreach (StreamWriter writer in _writers.Values)
            {
                writer.Flush();
                writer.Dispose();
            }

            _writers.Clear();
        }

        private void WriteMetaFile()
        {
            if (!_enabled || string.IsNullOrEmpty(SessionDir) || SessionDir == "file-write-disabled")
            {
                return;
            }

            JsonData json = new JsonData();
            json["schema_version"] = SchemaVersion;
            json["session_id"] = SessionId;
            JsonData device = new JsonData();
            device["model"] = _deviceModel;
            device["is_pico4u"] = _isPico4U;
            json["device"] = device;
            json["recording_status"] = _recordingStatus;
            json["stop_reason"] = _stopReason;
            json["started_utc"] = _startedUtc;
            json["ended_utc"] = _endedUtc;
            json["started_elapsed_realtime_ns"] = _startedElapsedRealtimeNs;
            json["ended_elapsed_realtime_ns"] = _endedElapsedRealtimeNs;

            JsonData sampleHz = new JsonData();
            sampleHz["enterprise"] = _enterpriseSampleHz;
            sampleHz["unity"] = _unitySampleHz;
            json["sample_hz"] = sampleHz;

            json["tracking_config_at_start"] = _snapshot.ToJson();
            json["config_locked"] = true;

            JsonData events = new JsonData();
            events.SetJsonType(JsonType.Array);
            lock (_metaLock)
            {
                for (int i = 0; i < _configChangeEvents.Count; i++)
                {
                    events.Add(_configChangeEvents[i]);
                }
            }

            json["config_change_events"] = events;

            JsonData sources = new JsonData();
            sources["video"] = BuildVideoSourceJson();
            sources["head"] = BuildSourceJson("head");
            sources["controller"] = BuildSourceJson("controller");
            sources["hand"] = BuildSourceJson("hand");
            sources["full_body"] = BuildSourceJson("full_body");
            sources["motion_tracker"] = BuildSourceJson("motion_tracker");
            sources["frame_map"] = BuildSourceJson("frame_map");
            json["sources"] = sources;

            JsonData disk = new JsonData();
            disk["ok"] = _diskOk;
            disk["free_bytes"] = _diskFreeBytes;
            disk["required_bytes"] = _diskRequiredBytes;
            json["disk"] = disk;

            JsonData interrupt = new JsonData();
            interrupt["kind"] = _interruptKind;
            interrupt["detail"] = _interruptDetail;
            json["interrupt"] = interrupt;

            json["record_dir"] = SessionDir;
            json["dropped_line_count"] = DroppedLineCount;
            if (_captureExtra != null)
            {
                json["capture"] = _captureExtra;
            }

            File.WriteAllText(Path.Combine(SessionDir, MetaFile), json.ToJson());
        }

        private JsonData BuildVideoSourceJson()
        {
            JsonData json = new JsonData();
            json["enabled_at_start"] = _videoEnabled;
            json["available"] = _videoAvailable;
            json["path"] = _videoEnabled ? (string.IsNullOrEmpty(_videoPathRelative) ? VideoFile : _videoPathRelative) : null;
            json["bytes"] = _videoBytes;
            json["sample_count"] = 0;
            json["dropped_count"] = 0;
            json["anomaly_count"] = 0;
            json["note"] = _videoNote;
            return json;
        }

        private JsonData BuildSourceJson(string key)
        {
            JsonData json = new JsonData();
            if (!_stats.TryGetValue(key, out SourceStats stats))
            {
                json["enabled_at_start"] = false;
                json["available"] = false;
                json["path"] = null;
                json["sample_count"] = 0;
                json["dropped_count"] = 0;
                json["anomaly_count"] = 0;
                json["note"] = "";
                return json;
            }

            json["enabled_at_start"] = stats.EnabledAtStart;
            json["available"] = stats.Available;
            json["path"] = stats.EnabledAtStart ? stats.FileName : null;
            json["sample_count"] = Interlocked.Read(ref stats.SampleCount);
            json["dropped_count"] = Interlocked.Read(ref stats.DroppedCount);
            json["anomaly_count"] = Interlocked.Read(ref stats.AnomalyCount);
            json["note"] = stats.Note ?? "";
            return json;
        }

        public struct StopStats
        {
            public int PendingBeforeStop;
            public int PendingAfterStop;
            public long WriterJoinMs;
            public long CloseWritersMs;
            public bool WriterThreadAlive;
            public string SessionDir;
        }

        private sealed class SourceStats
        {
            public bool EnabledAtStart;
            public bool Available;
            public string FileName;
            public string Note;
            public long SampleCount;
            public long DroppedCount;
            public long AnomalyCount;
        }

        private struct FileLine
        {
            public readonly string FileName;
            public readonly string Line;

            public FileLine(string fileName, string line)
            {
                FileName = fileName;
                Line = line;
            }
        }
    }
}
