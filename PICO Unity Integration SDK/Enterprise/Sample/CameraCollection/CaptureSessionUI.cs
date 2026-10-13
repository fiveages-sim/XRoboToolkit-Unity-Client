#if !PICO_OPENXR_SDK
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Pico.CameraCapture
{
    /// <summary>
    /// World-space capture panel with XR ray support (TrackedDeviceGraphicRaycaster).
    /// Preview shows for4U buffer only — never a game-view screenshot.
    /// </summary>
    public class CaptureSessionUI : MonoBehaviour
    {
        const float FollowDistance = 1.4f;
        const float FollowHeightOffset = -0.05f;

        CameraFrameCapture _capture;
        Transform _canvasTransform;
        Text _status;
        Text _fps;
        Text _path;
        Text _hint;
        Text _banner;
        Text _probe;
        Button _startBtn;
        Button _stopBtn;
        RawImage _preview;
        Renderer _previewQuad;
        Transform _previewQuadTransform;
        Font _font;
        bool _followHead = true;

        public static CaptureSessionUI Build(CameraFrameCapture capture, bool followHead = true)
        {
            var uiGo = new GameObject("CaptureSessionUI");
            uiGo.transform.SetParent(capture.transform, false);
            var ui = uiGo.AddComponent<CaptureSessionUI>();
            ui._capture = capture;
            ui._followHead = followHead;
            ui._font = ResolveFont();
            ui.CreateWorldUi();
            ui.WireCapture();
            return ui;
        }

        static Font ResolveFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        void LateUpdate()
        {
            if (!_followHead) return;
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 pos = cam.transform.position
                          + cam.transform.forward * FollowDistance
                          + cam.transform.up * FollowHeightOffset;
            // World Space UI reads correctly when facing the camera then flipped 180° on Y
            // (otherwise Game view / HMD sees mirrored or backface text).
            Quaternion faceCam = Quaternion.LookRotation(pos - cam.transform.position, cam.transform.up)
                                 * Quaternion.Euler(0f, 180f, 0f);

            if (_canvasTransform != null)
            {
                _canvasTransform.position = pos;
                _canvasTransform.rotation = faceCam;
            }

            if (_previewQuadTransform != null)
            {
                Vector3 qpos = pos + cam.transform.up * -0.58f;
                Quaternion qFace = Quaternion.LookRotation(qpos - cam.transform.position, cam.transform.up)
                                   * Quaternion.Euler(0f, 180f, 0f);
                _previewQuadTransform.position = qpos;
                _previewQuadTransform.rotation = qFace;
            }

            RefreshProbe();
        }

        void RefreshProbe()
        {
            if (_probe == null || _capture == null || _capture.Settings == null) return;
            var s = _capture.Settings;
            _probe.text =
                $"for4U preview (not VST) · {s.width}x{s.height} · eye={s.eyeMode} · " +
                $"bound={_capture.IsBound} stream={_capture.IsCapturing} rec={_capture.IsRecording}";
        }

        void CreateWorldUi()
        {
            var canvasGo = new GameObject("CaptureCanvas");
            canvasGo.transform.SetParent(transform, false);
            _canvasTransform = canvasGo.transform;

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = Camera.main;
            canvasGo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
            canvasGo.AddComponent<GraphicRaycaster>();
            canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();

            var rt = canvasGo.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(1100, 960);
            canvasGo.transform.position = new Vector3(0f, 1.4f, 1.5f);
            // Initial pose for Edit/Play before LateUpdate; LateUpdate re-orients to Main Camera.
            canvasGo.transform.rotation = Quaternion.identity;
            canvasGo.transform.localScale = Vector3.one * 0.00135f;

            var bg = CreateUiObject("Background", canvasGo.transform);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = new Color(0.02f, 0.12f, 0.22f, 0.92f);
            Stretch(bg.GetComponent<RectTransform>());

            _banner = CreateText("Banner", canvasGo.transform, "CAMERA CAPTURE · MR READY", 40, TextAnchor.UpperCenter);
            _banner.color = new Color(0.2f, 1f, 0.85f);
            SetRect(_banner.rectTransform, 20, -16, -20, -66);

            var title = CreateText("Title", canvasGo.transform,
                "Ray Trigger = click · Right A start/stop · B stop", 26, TextAnchor.UpperCenter);
            SetRect(title.rectTransform, 20, -66, -20, -104);

            _probe = CreateText("Probe", canvasGo.transform, "for4U preview…", 22, TextAnchor.UpperLeft);
            _probe.color = new Color(0.7f, 0.9f, 1f);
            SetRect(_probe.rectTransform, 40, -104, -40, -150);

            _preview = CreatePreview(canvasGo.transform);
            SetRect(_preview.rectTransform, 40, -150, -40, -500);

            CreatePreviewQuad();

            _startBtn = CreateButton(canvasGo.transform, "StartBtn", "Start Recording", new Color(0.1f, 0.65f, 0.28f));
            SetRect(_startBtn.GetComponent<RectTransform>(), 40, -520, -560, -600);
            _startBtn.onClick.AddListener(() => _capture.StartRecording());

            _stopBtn = CreateButton(canvasGo.transform, "StopBtn", "Stop Recording", new Color(0.7f, 0.15f, 0.15f));
            SetRect(_stopBtn.GetComponent<RectTransform>(), 560, -520, -40, -600);
            _stopBtn.onClick.AddListener(() => _capture.StopRecording());

            _status = CreateText("Status", canvasGo.transform, "Waiting…", 26, TextAnchor.UpperLeft);
            SetRect(_status.rectTransform, 40, -620, -40, -720);

            _fps = CreateText("Fps", canvasGo.transform, "IDLE", 24, TextAnchor.UpperLeft);
            _fps.color = new Color(0.5f, 1f, 0.5f);
            SetRect(_fps.rectTransform, 40, -720, -40, -780);

            _path = CreateText("Path", canvasGo.transform, "path: (not recording)", 22, TextAnchor.UpperLeft);
            _path.color = new Color(0.85f, 0.9f, 1f);
            SetRect(_path.rectTransform, 40, -780, -40, -900);

            _hint = CreateText("Hint", canvasGo.transform,
                "VST = background · Panel image = Enterprise for4U buffer",
                20, TextAnchor.LowerCenter);
            _hint.color = new Color(0.85f, 0.85f, 0.85f);
            SetRect(_hint.rectTransform, 40, 10, -40, 50);

            CreateAliveMarker();
        }

        void CreateAliveMarker()
        {
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "AliveMarker";
            var col = marker.GetComponent<Collider>();
            if (col != null) Destroy(col);
            var sh = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
            marker.GetComponent<Renderer>().material = new Material(sh) { color = new Color(0f, 1f, 0.7f, 1f) };
            marker.transform.SetParent(_canvasTransform, false);
            marker.transform.localPosition = new Vector3(-650f, 380f, -20f);
            marker.transform.localRotation = Quaternion.identity;
            marker.transform.localScale = new Vector3(80f, 80f, 20f);
        }

        void CreatePreviewQuad()
        {
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "PreviewQuad";
            quad.transform.SetParent(transform, false);
            _previewQuadTransform = quad.transform;
            quad.transform.localScale = new Vector3(0.8f, 0.6f, 1f);
            var col = quad.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var shader = Shader.Find("Unlit/Texture")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "PreviewMaterial", color = new Color(0.15f, 0.35f, 0.55f, 1f) };
            _previewQuad = quad.GetComponent<Renderer>();
            _previewQuad.material = mat;
        }

        RawImage CreatePreview(Transform parent)
        {
            var go = CreateUiObject("Preview", parent);
            var img = go.AddComponent<RawImage>();
            img.color = new Color(0.2f, 0.45f, 0.7f, 1f);
            img.texture = Texture2D.blackTexture;
            return img;
        }

        void WireCapture()
        {
            _capture.statusText = _status;
            _capture.fpsText = _fps;
            _capture.pathText = _path;
            _capture.previewImage = _preview;
            _capture.previewRenderer = _previewQuad;
            _capture.RecordingStateChanged += RefreshButtons;
            RefreshButtons();
        }

        void RefreshButtons()
        {
            bool rec = _capture != null && _capture.IsRecording;
            if (_startBtn != null) _startBtn.interactable = !rec;
            if (_stopBtn != null) _stopBtn.interactable = rec;
            if (_banner != null)
            {
                _banner.text = rec ? "RECORDING…" : "CAMERA CAPTURE · MR READY";
                _banner.color = rec ? new Color(1f, 0.35f, 0.2f) : new Color(0.2f, 1f, 0.85f);
            }
        }

        static GameObject CreateUiObject(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>();
            return go;
        }

        Text CreateText(string name, Transform parent, string value, int size, TextAnchor anchor)
        {
            var go = CreateUiObject(name, parent);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.color = Color.white;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.text = value;
            t.raycastTarget = false;
            return t;
        }

        Button CreateButton(Transform parent, string name, string label, Color color)
        {
            var go = CreateUiObject(name, parent);
            var img = go.AddComponent<Image>();
            img.color = color;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            var textGo = CreateUiObject("Label", go.transform);
            var text = textGo.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 30;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleCenter;
            text.text = label;
            text.raycastTarget = false;
            Stretch(text.rectTransform);
            return btn;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        static void SetRect(RectTransform rt, float left, float top, float right, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(right, top);
        }
    }
}
#endif
