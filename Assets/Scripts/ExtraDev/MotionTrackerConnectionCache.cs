using System.Collections.Generic;
using Unity.XR.PXR;

namespace Robot
{
    /// <summary>
    /// SDK 3.4.0 removed GetMotionTrackerConnectStateWithSN (obsolete error).
    /// Track connected motion-tracker IDs via MotionTrackerConnectionAction /
    /// RequestMotionTrackerCompleteAction.
    /// </summary>
    public static class MotionTrackerConnectionCache
    {
        private static readonly HashSet<long> ConnectedIds = new HashSet<long>();
        private static bool _subscribed;

        public static void EnsureSubscribed()
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            PXR_MotionTracking.MotionTrackerConnectionAction += OnConnectionChanged;
            PXR_MotionTracking.RequestMotionTrackerCompleteAction += OnRequestComplete;
        }

        private static void OnConnectionChanged(long trackerId, int state)
        {
            // Same convention as ExpandDeviceConnectionAction: 0 disconnected, 1 connected.
            if (state == 1)
            {
                ConnectedIds.Add(trackerId);
            }
            else
            {
                ConnectedIds.Remove(trackerId);
            }
        }

        private static void OnRequestComplete(RequestMotionTrackerCompleteEventData data)
        {
            ConnectedIds.Clear();
            if (data.trackerIds == null)
            {
                return;
            }

            int count = (int)data.trackerCount;
            for (int i = 0; i < count && i < data.trackerIds.Length; i++)
            {
                ConnectedIds.Add(data.trackerIds[i]);
            }
        }

        public static int Count
        {
            get
            {
                EnsureSubscribed();
                return ConnectedIds.Count;
            }
        }

        public static long[] GetConnectedIds()
        {
            EnsureSubscribed();
            long[] ids = new long[ConnectedIds.Count];
            ConnectedIds.CopyTo(ids);
            return ids;
        }
    }
}
