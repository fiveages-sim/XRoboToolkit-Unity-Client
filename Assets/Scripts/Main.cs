using Unity.XR.PICO.TOBSupport;
using Unity.XR.PXR;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Robot;

public class Main : MonoBehaviour
{
    // Compositor passthrough behind the floating panel. This only calls
    // PXR_Manager.EnableVideoSeeThrough. It does not call OpenVSTCamera:
    // that acquires frames in-process, was tied to the native memory climb,
    // and is a different camera client from capturelib recording.
    [SerializeField] private bool showPassthroughOutsidePanel = true;
    [SerializeField] private float eyeTextureResolutionScale = 1.0f;

    private void Awake()
    {
        EnterpriseCollectionRecorder.EnsureCreated();
        EnterpriseAuxTrackingSampler.EnsureCreated();
        DebugManager.instance.enableRuntimeUI = false;
        Application.logMessageReceived += OnLogMessageReceived;
        float clampedEyeTextureScale = Mathf.Clamp(eyeTextureResolutionScale, 0.7f, 1.0f);
        XRSettings.eyeTextureResolutionScale = clampedEyeTextureScale;
        ApplyPassthroughBackground(showPassthroughOutsidePanel, "Awake");
        Debug.Log($"Main passthrough outside panel={showPassthroughOutsidePanel}, eyeTextureResolutionScale={clampedEyeTextureScale:F2}");
        //Closing the security fence is only effective on B-end devices.
        PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_SECURITY_ZONE_PERMANENTLY, SwitchEnum.S_OFF);
    }

    private void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        LogView.Push(condition, stackTrace, type);
        if (type == LogType.Error)
        {
            Toast.Show(condition);
        }
    }

    private void OnEnable()
    {
        ApplyPassthroughBackground(showPassthroughOutsidePanel, "OnEnable");
    }

    private void OnDisable()
    {
        ApplyPassthroughBackground(false, "OnDisable");
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        Debug.Log("OnApplicationPause " + pauseStatus);
        if (pauseStatus)
        {
            ApplyPassthroughBackground(false, "pause");
            PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_SECURITY_ZONE_PERMANENTLY,
                SwitchEnum.S_ON);
        }
        else
        {
            //Closing the security fence is only effective on B-end devices.
            PXR_Enterprise.SwitchSystemFunction(SystemFunctionSwitchEnum.SFS_SECURITY_ZONE_PERMANENTLY,
                SwitchEnum.S_OFF);
            ApplyPassthroughBackground(showPassthroughOutsidePanel, "resume");
        }
    }

    private void ApplyPassthroughBackground(bool enabled, string reason)
    {
        if (Application.platform != RuntimePlatform.Android)
        {
            return;
        }

        Camera headsetCamera = Camera.main;
        if (headsetCamera != null)
        {
            headsetCamera.clearFlags = CameraClearFlags.SolidColor;
            headsetCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        }

        if (PXR_Manager.EnableVideoSeeThrough == enabled)
        {
            Debug.Log($"Main passthrough unchanged: enabled={enabled}, reason={reason}");
            return;
        }

        PXR_Manager.EnableVideoSeeThrough = enabled;
        Debug.Log($"Main passthrough background: enabled={enabled}, reason={reason}");
    }
}
