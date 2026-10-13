using System;
using System.Globalization;
using System.IO;
using System.Text;
using LitJson;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;

namespace Robot
{
    /// <summary>
    /// Facts written into recording_meta. Does not open a camera and does not change file names.
    /// </summary>
    public static class CaptureReport
    {
        public static void WriteMp4Plan(JsonData meta, int width, int height, int fps)
        {
            JsonData pre = new JsonData();
            pre["width"] = width;
            pre["height"] = height;
            pre["crop"] =
                "CameraHandle.SetConfig calls enableEIS(true). The session then calls enableEIS(false). " +
                "StartPreview bytecode sets no crop rectangle.";
            pre["rotationDegrees"] = 0;
            pre["scale"] =
                "The requested width and height are passed to StartPreview and to the encoder MediaFormat. " +
                "The AAR still hardcodes the encoder to 60fps and 5Mbps.";
            pre["layout"] = "PXRCapture_RenderMode_3D side-by-side, left half then right half";
            meta["preEncode"] = pre;

            JsonData modes = new JsonData();
            modes["selected"] = "3D";
            modes["available"] = "LEFT, RIGHT, 3D, Interlace";
            modes["note"] = "Selected mode is the capturelib preview mode. Other modes are not switched during this recording.";
            meta["cameraModes"] = modes;

            meta["picoApiFrame"] = UnavailableFrame(
                "This save type does not read SetCameraFrameBufferfor4U. Width and height are the StartPreview request.");
            meta["calibration"] = CalibrationNote(width, height, false);
            meta["timeSync"] = Mp4TimeSync();
            meta["configuredFps"] = fps;
        }

        public static void WriteApiMp4Plan(JsonData meta, int width, int height, int fps, string cameraMode)
        {
            JsonData modes = new JsonData();
            modes["selected"] = cameraMode ?? "LEFT";
            modes["available"] = "LEFT, RIGHT, 3D, Interlace";
            modes["note"] =
                "Only the selected mode is started with StartGetImageDatafor4U. Other modes are listed from PXRCaptureRenderMode.";
            meta["cameraModes"] = modes;
            meta["configuredFps"] = fps;
            meta["picoApiFrame"] = UnavailableFrame(
                "Waiting for the first for4U Frame callback to fill width, height, datasize, and pixel format.");
            WriteApiPreEncode(meta, width, height, cameraMode);
            meta["calibration"] = CalibrationNote(width, height, false);
            meta["timeSync"] = ApiTimeSync(0, 0, false);
        }

        public static void WriteApiFrameFacts(JsonData meta, int width, int height, string pixelFormat,
            int dataSize, int configuredFps, float measuredFps, string cameraMode, bool modeStarted)
        {
            JsonData frame = new JsonData();
            frame["width"] = width;
            frame["height"] = height;
            frame["pixelFormat"] = pixelFormat ?? "not-read";
            frame["datasize"] = dataSize;
            frame["configuredFps"] = configuredFps;
            frame["measuredFps"] = measuredFps.ToString("R", CultureInfo.InvariantCulture);
            frame["selectedMode"] = cameraMode ?? "LEFT";
            frame["selectedModeStarted"] = modeStarted;
            frame["availableModes"] = "LEFT, RIGHT, 3D, Interlace";
            frame["note"] =
                "Width and height come from Frame.width/height when non-zero, otherwise the SetCameraFrameBufferfor4U request.";
            meta["picoApiFrame"] = frame;
        }

        public static void WriteApiPreEncode(JsonData meta, int width, int height, string cameraMode)
        {
            JsonData pre = new JsonData();
            pre["width"] = width;
            pre["height"] = height;
            pre["crop"] = "none";
            pre["rotationDegrees"] = 0;
            pre["scale"] = "1:1 RGBA buffer copied into FrameMuxer without scaling";
            if (string.Equals(cameraMode, "3D", StringComparison.OrdinalIgnoreCase))
            {
                pre["layout"] = "3D side-by-side in one buffer, left half then right half";
            }
            else if (string.Equals(cameraMode, "RIGHT", StringComparison.OrdinalIgnoreCase))
            {
                pre["layout"] = "RIGHT, single eye, full buffer";
            }
            else
            {
                pre["layout"] = "LEFT, single eye, full buffer";
            }

            meta["preEncode"] = pre;
        }

        public static void WriteMp4Result(JsonData meta, string path)
        {
            JsonData mp4 = meta != null && meta.ContainsKey("mp4") && meta["mp4"] != null && meta["mp4"].IsObject
                ? meta["mp4"]
                : new JsonData();
            mp4["path"] = path ?? "";
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                mp4["present"] = false;
                meta["mp4"] = mp4;
                return;
            }

            FileInfo info = new FileInfo(path);
            mp4["present"] = true;
            mp4["bytes"] = info.Length;
            if (!mp4.ContainsKey("edges"))
            {
                mp4["edges"] =
                    "Pixels are not readable before the muxer. Edge content is the capture size after EIS is turned off.";
            }

            TryReadMovieHeader(path, mp4);
            meta["mp4"] = mp4;
        }

        public static JsonData CalibrationNote(int width, int height, bool queryFor4U)
        {
            JsonData calib = new JsonData();
            calib["applicableWidth"] = width;
            calib["applicableHeight"] = height;
            calib["distortionAvailable"] = false;
            calib["distortion"] =
                "This SDK's RGBCameraParamsNew has fx, fy, cx, cy and left/right poses. It has no k1, k2, p1, or p2.";
            if (!queryFor4U)
            {
                calib["intrinsicsStatus"] = "not-queried";
                calib["extrinsicsStatus"] = "not-queried";
                calib["note"] =
                    "for4U calibration is not queried during capturelib recording. " +
                    "The tracking header still stores CameraHandle getCameraIntrinsics/getCameraExtrinsics strings.";
                return calib;
            }

            try
            {
                RGBCameraParamsNew parameters = PXR_Enterprise.GetCameraParametersNewfor4U(width, height);
                bool ok = !(parameters.fx == 0 && parameters.fy == 0 && parameters.cx == 0 && parameters.cy == 0);
                calib["intrinsicsStatus"] = ok ? "ok" : "unknown";
                if (ok)
                {
                    calib["fx"] = parameters.fx;
                    calib["fy"] = parameters.fy;
                    calib["cx"] = parameters.cx;
                    calib["cy"] = parameters.cy;
                    calib["leftPosition"] = parameters.l_pos.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                                            parameters.l_pos.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                                            parameters.l_pos.z.ToString("R", CultureInfo.InvariantCulture);
                    calib["leftRotation"] = parameters.l_rot.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                                            parameters.l_rot.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                                            parameters.l_rot.z.ToString("R", CultureInfo.InvariantCulture) + "," +
                                            parameters.l_rot.w.ToString("R", CultureInfo.InvariantCulture);
                    calib["rightPosition"] = parameters.r_pos.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                                             parameters.r_pos.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                                             parameters.r_pos.z.ToString("R", CultureInfo.InvariantCulture);
                    calib["rightRotation"] = parameters.r_rot.x.ToString("R", CultureInfo.InvariantCulture) + "," +
                                             parameters.r_rot.y.ToString("R", CultureInfo.InvariantCulture) + "," +
                                             parameters.r_rot.z.ToString("R", CultureInfo.InvariantCulture) + "," +
                                             parameters.r_rot.w.ToString("R", CultureInfo.InvariantCulture);
                }
            }
            catch (Exception exception)
            {
                calib["intrinsicsStatus"] = "unknown";
                calib["intrinsicsError"] = exception.Message;
            }

            try
            {
                bool extrinsics = PXR_Enterprise.GetCameraExtrinsicsfor4U(out Matrix4x4 left, out Matrix4x4 right);
                calib["extrinsicsStatus"] = extrinsics ? "ok" : "unknown";
                if (extrinsics)
                {
                    calib["leftMatrix"] = MatrixText(left);
                    calib["rightMatrix"] = MatrixText(right);
                }
            }
            catch (Exception exception)
            {
                calib["extrinsicsStatus"] = "unknown";
                calib["extrinsicsError"] = exception.Message;
            }

            return calib;
        }

        public static JsonData UnavailableFrame(string note)
        {
            JsonData frame = new JsonData();
            frame["width"] = 0;
            frame["height"] = 0;
            frame["pixelFormat"] = "not-read";
            frame["datasize"] = 0;
            frame["measuredFps"] = 0;
            frame["note"] = note;
            return frame;
        }

        public static JsonData Mp4TimeSync()
        {
            JsonData sync = new JsonData();
            sync["imageClock"] = "MP4 PTS is MediaCodec presentationTimeUs. It is not Unix time and not CLOCK_BOOTTIME.";
            sync["trackingClock"] =
                "elapsedRealtimeNs is CLOCK_BOOTTIME nanoseconds. timeStampNs is UTC milliseconds times 1000000.";
            sync["canAlign"] =
                "Indirectly. videoStartElapsedRealtimeNs and videoStartPredictTimeUs are stored when StartRecord returns. " +
                "Per-frame PTS is not exposed to C#. Do not subtract timeStampNs from PTS.";
            return sync;
        }

        public static JsonData ApiTimeSync(long firstSdkTimestampNs, long firstElapsedRealtimeNs, bool haveFrame)
        {
            JsonData sync = new JsonData();
            sync["imageClock"] = "Frame.timestamp is exposure-start CLOCK_BOOTTIME nanoseconds.";
            sync["trackingClock"] =
                "elapsedRealtimeNs is CLOCK_BOOTTIME nanoseconds. timeStampNs is UTC milliseconds times 1000000.";
            if (!haveFrame)
            {
                sync["canAlign"] = "No frame arrived, so no image timestamp was paired with tracking.";
                return sync;
            }

            long delta = firstElapsedRealtimeNs - firstSdkTimestampNs;
            sync["firstFrameElapsedMinusSdkNs"] = delta;
            sync["canAlign"] = Math.Abs(delta) < 60L * 1000000000L
                ? "Yes, as the same CLOCK_BOOTTIME family. Compare frames.jsonl sdk_timestamp_ns with tracking elapsedRealtimeNs. Do not subtract timeStampNs."
                : "The first-frame delta is larger than 60 seconds, so the two timestamps are not treated as one clock.";
            return sync;
        }

        private static string MatrixText(Matrix4x4 matrix)
        {
            StringBuilder builder = new StringBuilder(160);
            for (int i = 0; i < 16; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(matrix[i].ToString("R", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        private static void TryReadMovieHeader(string path, JsonData mp4)
        {
            try
            {
                byte[] window = ReadEnds(path, 1024 * 1024);
                int mvhd = IndexOf(window, Encoding.ASCII.GetBytes("mvhd"));
                int tkhd = IndexOf(window, Encoding.ASCII.GetBytes("tkhd"));
                if (mvhd >= 0 && TryDuration(window, mvhd, out double seconds, out int timescale))
                {
                    mp4["durationSec"] = seconds.ToString("R", CultureInfo.InvariantCulture);
                    mp4["timescale"] = timescale;
                }
                else
                {
                    mp4["durationSec"] = "unparsed";
                }

                if (tkhd >= 0 && TryTrackSize(window, tkhd, out int width, out int height))
                {
                    mp4["streamWidth"] = width;
                    mp4["streamHeight"] = height;
                }
                else
                {
                    mp4["streamWidth"] = "unparsed";
                    mp4["streamHeight"] = "unparsed";
                }
            }
            catch (Exception exception)
            {
                mp4["probeError"] = exception.Message;
            }
        }

        private static byte[] ReadEnds(string path, int budget)
        {
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length <= budget)
                {
                    byte[] all = new byte[stream.Length];
                    stream.Read(all, 0, all.Length);
                    return all;
                }

                int half = budget / 2;
                byte[] head = new byte[half];
                byte[] tail = new byte[half];
                stream.Read(head, 0, half);
                stream.Seek(-half, SeekOrigin.End);
                stream.Read(tail, 0, half);
                byte[] both = new byte[budget];
                Buffer.BlockCopy(head, 0, both, 0, half);
                Buffer.BlockCopy(tail, 0, both, half, half);
                return both;
            }
        }

        private static bool TryDuration(byte[] data, int typeAt, out double seconds, out int timescale)
        {
            seconds = 0;
            timescale = 0;
            if (typeAt + 24 >= data.Length)
            {
                return false;
            }

            int version = data[typeAt + 4];
            if (version == 0 && typeAt + 24 < data.Length)
            {
                timescale = ReadInt(data, typeAt + 16);
                uint duration = (uint)ReadInt(data, typeAt + 20);
                if (timescale <= 0)
                {
                    return false;
                }

                seconds = duration / (double)timescale;
                return true;
            }

            if (version == 1 && typeAt + 36 < data.Length)
            {
                timescale = ReadInt(data, typeAt + 24);
                ulong duration = ReadUInt64(data, typeAt + 28);
                if (timescale <= 0)
                {
                    return false;
                }

                seconds = duration / (double)timescale;
                return true;
            }

            return false;
        }

        private static bool TryTrackSize(byte[] data, int typeAt, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (typeAt + 4 >= data.Length)
            {
                return false;
            }

            int version = data[typeAt + 4];
            int widthAt = version == 0 ? typeAt + 80 : typeAt + 92;
            if (widthAt + 8 > data.Length)
            {
                return false;
            }

            uint rawWidth = (uint)ReadInt(data, widthAt);
            uint rawHeight = (uint)ReadInt(data, widthAt + 4);
            width = (int)(rawWidth >> 16);
            height = (int)(rawHeight >> 16);
            return width > 0 && height > 0 && width < 10000 && height < 10000;
        }

        private static int ReadInt(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }

        private static ulong ReadUInt64(byte[] data, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value = (value << 8) | data[offset + i];
            }

            return value;
        }

        private static int IndexOf(byte[] buffer, byte[] needle)
        {
            int last = buffer.Length - needle.Length;
            for (int i = 0; i <= last; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (buffer[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
