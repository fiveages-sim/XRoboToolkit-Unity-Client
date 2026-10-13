#if !PICO_OPENXR_SDK
using System;
using UnityEngine;

namespace Pico.CameraCapture
{
    [Serializable]
    public class CaptureSettings
    {
        public string packageName = "com.pico.enterprise.cameracapture";
        public int width = 2048;
        public int height = 1536;
        public string eyeMode = "LEFT";
        public bool outputCameraRawData = true;
        public int targetFps = 30;
        public int saveFps = 10;
        public int poseFps = 50;
        public bool enableMctf = false;
        public bool enableEis = false;
        public bool enableMfnr = true;
        public bool autoStartOnAwake = false;
        public int maxFramesToSave = 0;
        public string collectionRootName = "CameraCollection";
        /// <summary>jpeg (default) or rgba (probe/debug only).</summary>
        public string saveFormat = "jpeg";
        public int jpegQuality = 85;
        public string xrtSessionId = "";
        public int previewMaxSide = 512;

        public bool SaveAsJpeg =>
            !string.Equals(saveFormat, "rgba", StringComparison.OrdinalIgnoreCase);

        public static CaptureSettings LoadOrDefault()
        {
            var asset = Resources.Load<TextAsset>("CaptureSettings");
            if (asset == null || string.IsNullOrEmpty(asset.text))
            {
                Debug.LogWarning("[CameraCapture] CaptureSettings.json missing; using defaults.");
                return new CaptureSettings();
            }

            try
            {
                return JsonUtility.FromJson<CaptureSettings>(asset.text) ?? new CaptureSettings();
            }
            catch (Exception e)
            {
                Debug.LogError($"[CameraCapture] Failed to parse CaptureSettings.json: {e.Message}");
                return new CaptureSettings();
            }
        }
    }
}
#endif
