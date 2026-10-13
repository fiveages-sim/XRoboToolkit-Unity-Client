#if !PICO_OPENXR_SDK
using UnityEngine;

namespace Pico.CameraCapture
{
    /// <summary>
    /// Intentionally does NOT use RuntimeInitializeOnLoad.
    /// Auto-spawning CameraCaptureRoot from the shared PICO SDK package would inject
    /// the capture panel into every Unity project that references this SDK (e.g. a new
    /// unity-PICO-test). Capture UI is created only when the scene contains
    /// CameraCaptureRoot / CameraFrameCapture (+ MrSceneSetup), as in PICO-CameraCapture.
    /// </summary>
    public class CameraCaptureBootstrap : MonoBehaviour
    {
        // Kept as an empty MonoBehaviour so existing scene references / docs remain valid.
        // Do not add [RuntimeInitializeOnLoadMethod] here.
    }
}
#endif
