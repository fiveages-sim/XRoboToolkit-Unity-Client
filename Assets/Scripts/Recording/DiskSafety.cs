using System;
using System.IO;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Pre-flight and mid-session free-space checks for long capture runs.
    /// Rule: free &gt; 1.5 * estimatedBytes + 2GB.
    /// </summary>
    public static class DiskSafety
    {
        public const long ReserveBytes = 2L * 1024L * 1024L * 1024L;

        public static long RequiredBytes(long estimatedBytes)
        {
            if (estimatedBytes < 0)
            {
                estimatedBytes = 0;
            }

            return (long)(estimatedBytes * 1.5) + ReserveBytes;
        }

        public static long GetFreeBytes(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = VisionTrackingSession.DownloadDir;
            }

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                string probe = path;
                if (!Directory.Exists(probe) && !File.Exists(probe))
                {
                    Directory.CreateDirectory(probe);
                }

                using (AndroidJavaObject statFs = new AndroidJavaObject("android.os.StatFs", probe))
                {
                    long blockSize = statFs.Call<long>("getBlockSizeLong");
                    long available = statFs.Call<long>("getAvailableBlocksLong");
                    return blockSize * available;
                }
#else
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root))
                {
                    return long.MaxValue;
                }

                DriveInfo drive = new DriveInfo(root);
                return drive.AvailableFreeSpace;
#endif
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[DiskSafety] free-space probe failed: " + exception.Message);
                return -1;
            }
        }

        public static bool EnsureSpace(string path, long estimatedBytes, out long freeBytes,
            out long requiredBytes, out string error)
        {
            requiredBytes = RequiredBytes(estimatedBytes);
            freeBytes = GetFreeBytes(path);
            error = null;
            if (freeBytes < 0)
            {
                error = "could not read free space for " + path;
                return false;
            }

            if (freeBytes <= requiredBytes)
            {
                error = "insufficient disk: free=" + freeBytes + " required=" + requiredBytes +
                        " estimated=" + estimatedBytes + " path=" + path;
                return false;
            }

            return true;
        }
    }
}
