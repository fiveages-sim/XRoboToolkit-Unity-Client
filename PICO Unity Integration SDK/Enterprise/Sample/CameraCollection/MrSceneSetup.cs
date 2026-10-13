#if !PICO_OPENXR_SDK
using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Pico.CameraCapture
{
    /// <summary>
    /// Hardens the MR scene at runtime: remove stray cameras, ensure XR UI EventSystem,
    /// build capture panel once, wire controller rays to XR Interaction Manager.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class MrSceneSetup : MonoBehaviour
    {
        public CameraFrameCapture capture;
        public bool followHead = true;

        CaptureSessionUI _ui;

        void Awake()
        {
            if (capture == null)
            {
                capture = GetComponent<CameraFrameCapture>();
            }

            RemoveStrayMainCameras();
            EnsureXrUiEventSystem();
            WireControllerInteractionManagers();

            // Phase-1 scene panel root — never spawn a second CaptureSessionUI on top of it.
            if (GameObject.Find("DataCollectorPanel") != null)
            {
                Debug.Log("[CameraCapture] DataCollectorPanel present; skipping CaptureSessionUI.");
                return;
            }

            if (FindObjectOfType<CaptureSessionUI>() == null && capture != null)
            {
                _ui = CaptureSessionUI.Build(capture, followHead);
            }
        }

        static void RemoveStrayMainCameras()
        {
            var cameras = FindObjectsOfType<Camera>();
            foreach (var cam in cameras)
            {
                if (cam == null) continue;
                // Keep the HMD camera under XR Origin / Camera Offset.
                if (IsUnderXrRig(cam.transform)) continue;

                if (cam.CompareTag("MainCamera") || cam.gameObject.name == "Main Camera")
                {
                    Debug.Log("[CameraCapture] Disabling stray Main Camera: " + cam.gameObject.name);
                    cam.gameObject.SetActive(false);
                }
            }
        }

        static void EnsureXrUiEventSystem()
        {
            var es = FindObjectOfType<EventSystem>();
            if (es == null)
            {
                var go = new GameObject("EventSystem");
                es = go.AddComponent<EventSystem>();
            }

            // Prefer XRUIInputModule for controller ray UI; remove Standalone if present.
            var standalone = es.GetComponent<StandaloneInputModule>();
            if (standalone != null)
            {
                Destroy(standalone);
            }

            if (es.GetComponent<XRUIInputModule>() == null)
            {
                es.gameObject.AddComponent<XRUIInputModule>();
            }
        }

        static void WireControllerInteractionManagers()
        {
            var mgr = FindObjectOfType<XRInteractionManager>();
            if (mgr == null) return;

            var interactors = FindObjectsOfType<UnityEngine.XR.Interaction.Toolkit.Interactors.XRBaseInteractor>();
            foreach (var interactor in interactors)
            {
                if (interactor.interactionManager == null)
                {
                    interactor.interactionManager = mgr;
                }
            }
        }

        static bool IsUnderXrRig(Transform t)
        {
            while (t != null)
            {
                string n = t.name;
                if (n.IndexOf("XR Origin", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Camera Offset", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Video Seethrough", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
                t = t.parent;
            }
            return false;
        }
    }
}
#endif
