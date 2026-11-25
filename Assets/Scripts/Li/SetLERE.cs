using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class SetLERE : MonoBehaviour
{

    public GameObject CanvLE;
    public GameObject CanvRE;
    public RemoteCameraWindow remoteCameraWindow;
    public Material matLE;

    public Material matRE;

    //private float visibleRatio = 0.75f;
    //private float contentRatio = 0.88f;
    private float visibleRatio = 0.555f;
    private float contentRatio = 1.8f;
    private float heightCompressionFactor = 1.333333f; // 4:3 aspect ratio
    private bool projectionSuspended;
    private bool canvasStateCaptured;
    private bool canvLEWasActive;
    private bool canvREWasActive;
    private bool eyeInputSanitized;

    public bool ProjectionSuspended => projectionSuspended;

    public void UpdateParameters(float visible, float content, float heightCompression)
    {
        // Adjust the ratios based on the height compression factor
        visibleRatio = visible;
        contentRatio = content;
        heightCompressionFactor = heightCompression;

        // Log the updated values
        Debug.Log($"Updated Ratios - visible: {visibleRatio}, content: {contentRatio}, heightCompression: {heightCompressionFactor}");
    }
    
    public void ResetCanvases()
    {
        if (CanvLE != null)
        {
            CanvLE.SetActive(false);
        }
        if (CanvRE != null)
        {
            CanvRE.SetActive(false);
        }
    }

    /// <summary>
    /// PICO4U / ZEDMINI Listen: allow LE/RE stereo after a prior ForceSessionOff.
    /// Right-hand B (ToggleCameraClippingPlane) hides these canvases via near-clip
    /// so the SBS floating window and main panel are usable again.
    /// </summary>
    public void BeginFlatStereoSession()
    {
        projectionSuspended = false;
        canvasStateCaptured = false;
        canvLEWasActive = false;
        canvREWasActive = false;
        eyeInputSanitized = false;
        ResetCanvases();
    }

    /// <summary>
    /// End a Remote Vision session: keep both eye canvases off and clear any
    /// "restore previous active state" memory so OnDisable/unsuspend cannot
    /// resurrect floating LE/RE windows after Stop.
    /// </summary>
    public void ForceSessionOff()
    {
        projectionSuspended = true;
        canvasStateCaptured = false;
        canvLEWasActive = false;
        canvREWasActive = false;
        ResetCanvases();
    }

    public void SetProjectionSuspended(bool suspended)
    {
        if (suspended == projectionSuspended)
        {
            if (suspended)
            {
                ResetCanvases();
            }
            return;
        }

        if (suspended)
        {
            canvLEWasActive = CanvLE != null && CanvLE.activeSelf;
            canvREWasActive = CanvRE != null && CanvRE.activeSelf;
            canvasStateCaptured = true;
            projectionSuspended = true;
            ResetCanvases();
            return;
        }

        projectionSuspended = false;
        eyeInputSanitized = false;
        if (canvasStateCaptured)
        {
            if (CanvLE != null)
            {
                CanvLE.SetActive(canvLEWasActive);
            }
            if (CanvRE != null)
            {
                CanvRE.SetActive(canvREWasActive);
            }
        }
        canvasStateCaptured = false;
    }

    /// <summary>
    /// LE/RE are Screen-Space canvases at ~0.31m (official full-face stereo).
    /// With raycasts enabled they sit in front of the floating RemoteCamera X and
    /// the main Listen button, reproducing the official "cannot close" trap once
    /// enterprise 2160×810 actually paints frames. Display only — never capture input.
    /// </summary>
    private void EnsureEyeCanvasesDoNotCaptureInput()
    {
        if (eyeInputSanitized)
        {
            return;
        }

        SanitizeEyeCanvasInput(CanvLE);
        SanitizeEyeCanvasInput(CanvRE);
        eyeInputSanitized = true;
    }

    private static void SanitizeEyeCanvasInput(GameObject canvasObject)
    {
        if (canvasObject == null)
        {
            return;
        }

        var raycasters = canvasObject.GetComponents<GraphicRaycaster>();
        for (int i = 0; i < raycasters.Length; i++)
        {
            if (raycasters[i] != null)
            {
                raycasters[i].enabled = false;
            }
        }

        // PICO OpenXR / custom raycasters share the same "block everything" failure mode.
        var behaviours = canvasObject.GetComponents<Behaviour>();
        for (int i = 0; i < behaviours.Length; i++)
        {
            Behaviour behaviour = behaviours[i];
            if (behaviour == null || !behaviour.enabled)
            {
                continue;
            }
            string typeName = behaviour.GetType().Name;
            if (typeName.IndexOf("Raycaster", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                behaviour.enabled = false;
            }
        }

        var graphics = canvasObject.GetComponentsInChildren<Graphic>(true);
        for (int i = 0; i < graphics.Length; i++)
        {
            if (graphics[i] != null)
            {
                graphics[i].raycastTarget = false;
            }
        }
    }

    void Update()
    {
        if (projectionSuspended)
        {
            // Keep the legacy eye canvases off for the complete panoramic
            // lifecycle. ResetCanvases() and another component may run in a
            // different frame, so this invariant is enforced continuously.
            if (CanvLE != null && CanvLE.activeSelf)
            {
                CanvLE.SetActive(false);
            }
            if (CanvRE != null && CanvRE.activeSelf)
            {
                CanvRE.SetActive(false);
            }
            return;
        }

        if (CanvLE == null || CanvRE == null || remoteCameraWindow == null)
        {
            return;
        }

        if ((!CanvLE.activeSelf) || (!CanvRE.activeSelf))
        {
            CanvLE.SetActive(true);
            CanvRE.SetActive(true);
            EnsureEyeCanvasesDoNotCaptureInput();

            if (matLE != null && matRE != null)
            {
                matLE.SetTexture("_mainRT", remoteCameraWindow.Texture);
                matRE.SetTexture("_mainRT", remoteCameraWindow.Texture);

                matLE.SetInt("_isLE", 1);
                matRE.SetInt("_isLE", 0);

                matLE.SetFloat("_visibleRatio", visibleRatio);
                matRE.SetFloat("_visibleRatio", visibleRatio);
                matLE.SetFloat("_contentRatio", contentRatio);
                matRE.SetFloat("_contentRatio", contentRatio);
                matLE.SetFloat("_heightCompressionFactor", heightCompressionFactor);
                matRE.SetFloat("_heightCompressionFactor", heightCompressionFactor);
            }
        }
        else if (!eyeInputSanitized)
        {
            EnsureEyeCanvasesDoNotCaptureInput();
        }

        if (Input.GetKeyDown(KeyCode.Q))
        {
            visibleRatio += 0.005f;
            matLE.SetFloat("_visibleRatio", visibleRatio);
            matRE.SetFloat("_visibleRatio", visibleRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.E))
        {
            visibleRatio -= 0.005f;
            matLE.SetFloat("_visibleRatio", visibleRatio);
            matRE.SetFloat("_visibleRatio", visibleRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.A))
        {
            contentRatio += 0.005f;
            matLE.SetFloat("_contentRatio", contentRatio);
            matRE.SetFloat("_contentRatio", contentRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }

        if (Input.GetKeyDown(KeyCode.D))
        {
            contentRatio -= 0.005f;
            matLE.SetFloat("_contentRatio", contentRatio);
            matRE.SetFloat("_contentRatio", contentRatio);
            Debug.Log($"visibleRatio: {visibleRatio} - contentRatio: {contentRatio}");
        }
    }
}
