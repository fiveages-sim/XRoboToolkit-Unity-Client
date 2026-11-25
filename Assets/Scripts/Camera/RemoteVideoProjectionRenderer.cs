using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Switches the remote video between the legacy floating screen and a true
/// equirectangular skybox. The panoramic mode never fabricates a panorama from
/// a flat camera; it is enabled only when the operator explicitly advertises
/// equirectangular projection metadata.
/// </summary>
public sealed class RemoteVideoProjectionRenderer : MonoBehaviour
{
    public const string FlatProjection = "flat";
    public const string EquirectangularProjection = "equirectangular";
    public const string MonoLayout = "mono";
    public const string SideBySideLayout = "side_by_side";
    public const string TopBottomLayout = "top_bottom";

    private RemoteCameraWindow remoteWindow;
    private SetLERE legacyStereoRenderer;
    private RawImage flatImage;
    private Material panoramaMaterial;
    private Material previousSkybox;
    private Camera targetCamera;
    private CameraClearFlags previousClearFlags;
    private bool panoramaApplied;
    private bool flatImageStateCaptured;
    private bool flatImageWasEnabled;
    private bool flatPreviewOverride;
    private string projection = FlatProjection;
    private string stereoLayout = MonoLayout;
    private string negotiatedProjection = FlatProjection;
    private string negotiatedStereoLayout = MonoLayout;
    private Texture appliedTexture;

    public bool IsEquirectangularNegotiated =>
        negotiatedProjection == EquirectangularProjection;

    public bool FlatPreviewOverride => flatPreviewOverride;

    private void Awake()
    {
        ResolveComponents();
    }

    public void SetLegacyStereoRenderer(SetLERE renderer)
    {
        if (legacyStereoRenderer == renderer)
        {
            return;
        }
        if (panoramaApplied && legacyStereoRenderer != null)
        {
            legacyStereoRenderer.SetProjectionSuspended(false);
        }
        legacyStereoRenderer = renderer;
        if (panoramaApplied && legacyStereoRenderer != null)
        {
            legacyStereoRenderer.SetProjectionSuspended(true);
        }
    }

    public void Configure(string requestedProjection, string requestedStereoLayout)
    {
        negotiatedProjection = NormalizeProjection(requestedProjection);
        negotiatedStereoLayout = NormalizeStereoLayout(requestedStereoLayout);
        projection = negotiatedProjection;
        stereoLayout = negotiatedStereoLayout;
        CrashProbe.Breadcrumb(
            "projection_renderer.configure",
            $"projection={projection} layout={stereoLayout} flat_preview={flatPreviewOverride}");
        ResolveComponents();
        ApplyConfiguredMode();
    }

    /// <summary>
    /// Temporary flat floating-screen preview while an equirectangular session remains active.
    /// Does not renegotiate with the operator; Stop still tears the whole session down.
    /// </summary>
    public void SetFlatPreviewOverride(bool enabled)
    {
        flatPreviewOverride = enabled && IsEquirectangularNegotiated;
        CrashProbe.Breadcrumb(
            "projection_renderer.flat_preview",
            flatPreviewOverride.ToString());
        ApplyConfiguredMode();
    }

    private void ApplyConfiguredMode()
    {
        if (negotiatedProjection == EquirectangularProjection && !flatPreviewOverride)
        {
            projection = EquirectangularProjection;
            stereoLayout = negotiatedStereoLayout;
            ApplyPanorama();
            return;
        }

        // Flat External (PICO4U / ZEDMINI) or temporary flat preview of panorama.
        projection = FlatProjection;
        stereoLayout = negotiatedStereoLayout;
        ShowFlatScreenKeepingNegotiation();
    }

    public static string NormalizeProjection(string value)
    {
        return string.Equals(value, EquirectangularProjection, StringComparison.Ordinal)
            ? EquirectangularProjection
            : FlatProjection;
    }

    public static string NormalizeStereoLayout(string value)
    {
        if (string.Equals(value, SideBySideLayout, StringComparison.Ordinal))
        {
            return SideBySideLayout;
        }
        if (string.Equals(value, TopBottomLayout, StringComparison.Ordinal))
        {
            return TopBottomLayout;
        }
        return MonoLayout;
    }

    private void ResolveComponents()
    {
        if (remoteWindow == null)
        {
            remoteWindow = GetComponent<RemoteCameraWindow>();
        }
        if (remoteWindow != null && flatImage == null)
        {
            flatImage = remoteWindow.RemoteCameraImage;
        }
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
    }

    private void Update()
    {
        if (projection != EquirectangularProjection)
        {
            return;
        }
        if (!panoramaApplied)
        {
            ApplyPanorama();
            return;
        }
        // Keep skybox sticky: suppress flat RawImage / LE/RE that would cause
        // mesh flicker when other paths briefly fight the panorama session.
        if (flatImage != null && flatImage.enabled)
        {
            flatImage.enabled = false;
        }
        if (legacyStereoRenderer != null && !legacyStereoRenderer.ProjectionSuspended)
        {
            legacyStereoRenderer.ForceSessionOff();
        }
        if (panoramaMaterial != null && RenderSettings.skybox != panoramaMaterial)
        {
            RenderSettings.skybox = panoramaMaterial;
        }
        Texture texture = remoteWindow != null ? remoteWindow.Texture : null;
        if (texture != null && texture != appliedTexture)
        {
            ApplyTexture(texture);
        }
    }

    private void ApplyPanorama()
    {
        ResolveComponents();
        Shader shader = Shader.Find("Skybox/Panoramic");
        if (shader == null)
        {
            CrashProbe.Breadcrumb(
                "projection_renderer.panorama_shader_missing",
                "Skybox/Panoramic",
                LogType.Error);
            Debug.LogError("Skybox/Panoramic shader is unavailable; keeping flat remote video.");
            negotiatedProjection = FlatProjection;
            projection = FlatProjection;
            ShowFlatScreenKeepingNegotiation();
            return;
        }

        if (!panoramaApplied)
        {
            previousSkybox = RenderSettings.skybox;
            if (targetCamera != null)
            {
                previousClearFlags = targetCamera.clearFlags;
                targetCamera.clearFlags = CameraClearFlags.Skybox;
            }
            if (flatImage != null)
            {
                flatImageWasEnabled = flatImage.enabled;
                flatImageStateCaptured = true;
            }
        }
        if (panoramaMaterial == null)
        {
            panoramaMaterial = new Material(shader);
            panoramaMaterial.name = "XRoboToolkit Runtime Panorama";
            // Single Pass Instanced XR: runtime materials must opt in or only one eye renders.
            panoramaMaterial.enableInstancing = true;
        }

        panoramaMaterial.SetFloat("_Mapping", 1f);
        panoramaMaterial.EnableKeyword("_MAPPING_LATITUDE_LONGITUDE_LAYOUT");
        panoramaMaterial.DisableKeyword("_MAPPING_6_FRAMES_LAYOUT");
        panoramaMaterial.SetFloat("_ImageType", 0f);
        panoramaMaterial.SetFloat("_Layout", LayoutValue(stereoLayout));
        panoramaMaterial.SetFloat("_Rotation", 0f);
        RenderSettings.skybox = panoramaMaterial;
        if (flatImage != null)
        {
            flatImage.enabled = false;
        }
        if (legacyStereoRenderer != null)
        {
            legacyStereoRenderer.SetProjectionSuspended(true);
        }
        panoramaApplied = true;

        Texture texture = remoteWindow != null ? remoteWindow.Texture : null;
        if (texture != null)
        {
            ApplyTexture(texture);
        }
        CrashProbe.Breadcrumb("projection_renderer.panorama_applied", stereoLayout);
        Debug.Log($"Remote video projection=equirectangular layout={stereoLayout}");
    }

    private void ApplyTexture(Texture texture)
    {
        appliedTexture = texture;
        panoramaMaterial.SetTexture("_MainTex", texture);
        WarnIfUnexpectedAspect(texture);
    }

    private void WarnIfUnexpectedAspect(Texture texture)
    {
        if (texture == null || texture.height <= 0)
        {
            return;
        }
        float expected = stereoLayout == SideBySideLayout
            ? 4f
            : stereoLayout == TopBottomLayout
                ? 1f
                : 2f;
        float actual = (float)texture.width / texture.height;
        if (Mathf.Abs(actual - expected) > 0.08f)
        {
            CrashProbe.Breadcrumb(
                "projection_renderer.unexpected_aspect",
                $"actual={actual:F2} expected={expected:F2} layout={stereoLayout}",
                LogType.Warning);
            Debug.LogWarning(
                $"Equirectangular texture aspect is {actual:F2}; expected {expected:F2} " +
                $"for layout={stereoLayout}. Source metadata/resolution may be wrong.");
        }
    }

    private static float LayoutValue(string layout)
    {
        if (layout == SideBySideLayout)
        {
            return 1f;
        }
        if (layout == TopBottomLayout)
        {
            return 2f;
        }
        return 0f;
    }

    private void ShowFlatScreenKeepingNegotiation()
    {
        if (panoramaApplied)
        {
            CrashProbe.Breadcrumb("projection_renderer.show_flat_preview");
            if (RenderSettings.skybox == panoramaMaterial)
            {
                RenderSettings.skybox = previousSkybox;
            }
            if (targetCamera != null)
            {
                targetCamera.clearFlags = previousClearFlags;
            }
            panoramaApplied = false;
        }

        // Do NOT unsuspend LE/RE here. ZED uses floating RawImage only; unsuspending
        // would resurrect full-face canvases that cover the main panel and alternate
        // with it. PICO4U stereo is armed explicitly via SetLERE.BeginFlatStereoSession.
        if (flatImage != null)
        {
            flatImage.enabled = true;
        }
    }

    /// <summary>
    /// ZEDMINI / floating External: SBS on the RemoteCamera plane only.
    /// LE/RE stay forced off so the main control panel remains usable.
    /// </summary>
    public void PrepareFloatingFlat()
    {
        flatPreviewOverride = false;
        negotiatedProjection = FlatProjection;
        negotiatedStereoLayout = SideBySideLayout;
        projection = FlatProjection;
        stereoLayout = SideBySideLayout;

        if (panoramaApplied || RenderSettings.skybox == panoramaMaterial)
        {
            if (previousSkybox != null || RenderSettings.skybox == panoramaMaterial)
            {
                RenderSettings.skybox = previousSkybox;
            }
            if (targetCamera != null)
            {
                targetCamera.clearFlags = previousClearFlags;
            }
            panoramaApplied = false;
        }

        if (legacyStereoRenderer != null)
        {
            legacyStereoRenderer.ForceSessionOff();
        }

        ResolveComponents();
        if (flatImage != null)
        {
            flatImage.enabled = true;
        }
        CrashProbe.Breadcrumb("projection_renderer.floating_flat");
    }

    public void RestoreFlatScreen()
    {
        ShowFlatScreenKeepingNegotiation();
        flatPreviewOverride = false;
        negotiatedProjection = FlatProjection;
        negotiatedStereoLayout = MonoLayout;
        projection = FlatProjection;
        stereoLayout = MonoLayout;
        flatImageStateCaptured = false;
        if (panoramaMaterial != null)
        {
            panoramaMaterial.SetTexture("_MainTex", null);
        }
        appliedTexture = null;
        previousSkybox = null;
        targetCamera = null;
    }

    /// <summary>
    /// End Remote Vision cleanly: restore skybox, hide flat RawImage, and force
    /// LE/RE canvases off. Must NOT re-enable SetLERE windows (that was the bug
    /// that left a ZED-like floating view after Stop).
    /// </summary>
    public void ShutdownSession()
    {
        CrashProbe.Breadcrumb("projection_renderer.shutdown");
        flatPreviewOverride = false;
        negotiatedProjection = FlatProjection;
        negotiatedStereoLayout = MonoLayout;
        projection = FlatProjection;
        stereoLayout = MonoLayout;

        if (panoramaApplied || RenderSettings.skybox == panoramaMaterial)
        {
            if (previousSkybox != null || RenderSettings.skybox == panoramaMaterial)
            {
                RenderSettings.skybox = previousSkybox;
            }
            if (targetCamera != null)
            {
                targetCamera.clearFlags = previousClearFlags;
            }
        }

        if (flatImage != null)
        {
            flatImage.enabled = false;
        }

        if (legacyStereoRenderer != null)
        {
            legacyStereoRenderer.ForceSessionOff();
        }

        panoramaApplied = false;
        flatImageStateCaptured = false;
        if (panoramaMaterial != null)
        {
            panoramaMaterial.SetTexture("_MainTex", null);
        }
        appliedTexture = null;
        previousSkybox = null;
        targetCamera = null;
    }

    /// <summary>
    /// Prepare a Panorama Listen before AUDIO_CONFIG arrives: no LE/RE canvases,
    /// no flat RawImage preview. Apply the skybox shell immediately so the UI
    /// never flickers through floating / LE/RE while waiting for frames.
    /// </summary>
    public void PreparePendingPanorama()
    {
        flatPreviewOverride = false;
        negotiatedProjection = EquirectangularProjection;
        negotiatedStereoLayout = SideBySideLayout;
        projection = EquirectangularProjection;
        stereoLayout = SideBySideLayout;
        if (legacyStereoRenderer != null)
        {
            legacyStereoRenderer.ForceSessionOff();
        }
        ResolveComponents();
        if (flatImage != null)
        {
            flatImage.enabled = false;
        }
        ApplyPanorama();
        CrashProbe.Breadcrumb("projection_renderer.pending_panorama");
    }

    private void OnDisable()
    {
        ShutdownSession();
    }

    private void OnDestroy()
    {
        ShutdownSession();
        if (panoramaMaterial != null)
        {
            Destroy(panoramaMaterial);
            panoramaMaterial = null;
        }
    }
}
