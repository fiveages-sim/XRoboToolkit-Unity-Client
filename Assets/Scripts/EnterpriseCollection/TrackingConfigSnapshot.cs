using System;
using LitJson;

namespace Robot
{
    /// <summary>
    /// Frozen Tracking panel selection for one collection session.
    /// Mid-session UI changes must not alter which jsonl files are written.
    /// </summary>
    public struct TrackingConfigSnapshot
    {
        public bool Head;
        public bool Controller;
        public bool Hand;
        public bool FullBody;
        public bool MotionTracker;
        public bool Send;

        public static TrackingConfigSnapshot CaptureFromPanel()
        {
            TrackingData.TrackingType tracker = TrackingData.TrackingTypeValue;
            return new TrackingConfigSnapshot
            {
                Head = TrackingData.HeadOn,
                Controller = TrackingData.ControllerOn,
                Hand = TrackingData.HandTrackingOn,
                FullBody = tracker == TrackingData.TrackingType.Body,
                MotionTracker = tracker == TrackingData.TrackingType.Motion,
                Send = TcpHandler.SendTrackingData
            };
        }

        public bool AnyTracking
        {
            get
            {
                return Head || Controller || Hand || FullBody || MotionTracker;
            }
        }

        public JsonData ToJson()
        {
            JsonData json = new JsonData();
            json["head"] = Head;
            json["controller"] = Controller;
            json["hand"] = Hand;
            json["full_body"] = FullBody;
            json["motion_tracker"] = MotionTracker;
            json["send"] = Send;
            return json;
        }

        public static string DescribeDelta(TrackingConfigSnapshot before, TrackingConfigSnapshot after)
        {
            return string.Join(",",
                Diff("head", before.Head, after.Head),
                Diff("controller", before.Controller, after.Controller),
                Diff("hand", before.Hand, after.Hand),
                Diff("full_body", before.FullBody, after.FullBody),
                Diff("motion_tracker", before.MotionTracker, after.MotionTracker),
                Diff("send", before.Send, after.Send)).Trim(',');
        }

        private static string Diff(string name, bool before, bool after)
        {
            if (before == after)
            {
                return "";
            }

            return name + ":" + before.ToString().ToLowerInvariant() + "→" +
                   after.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// Session-wide lock so UI toggles cannot change which collection files are written.
    /// </summary>
    public static class TrackingConfigLock
    {
        private static readonly object Gate = new object();
        private static bool s_locked;
        private static TrackingConfigSnapshot s_snapshot;
        private static Action<string, bool, string> s_onChangeAttempt;

        public static bool IsLocked
        {
            get
            {
                lock (Gate)
                {
                    return s_locked;
                }
            }
        }

        public static TrackingConfigSnapshot Snapshot
        {
            get
            {
                lock (Gate)
                {
                    return s_snapshot;
                }
            }
        }

        public static void Lock(TrackingConfigSnapshot snapshot, Action<string, bool, string> onChangeAttempt)
        {
            lock (Gate)
            {
                s_snapshot = snapshot;
                s_onChangeAttempt = onChangeAttempt;
                s_locked = true;
            }
        }

        public static void Unlock()
        {
            lock (Gate)
            {
                s_locked = false;
                s_onChangeAttempt = null;
            }
        }

        /// <summary>
        /// Call from Tracking panel handlers. Returns true if the live panel value may change.
        /// When locked, records an unapplied config_change event and still allows TCP/live
        /// toggles (send path), but collection writers ignore the change.
        /// </summary>
        public static void NotifyPanelChange(string change, bool appliedForTcp)
        {
            Action<string, bool, string> callback;
            lock (Gate)
            {
                if (!s_locked)
                {
                    return;
                }

                callback = s_onChangeAttempt;
            }

            callback?.Invoke(change, false, appliedForTcp
                ? "config_locked_collection; tcp_follows_panel"
                : "config_locked");
        }
    }
}
