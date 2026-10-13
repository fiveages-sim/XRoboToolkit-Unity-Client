using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RecordDialog : MonoBehaviour
{
    public TMP_InputField widthInputField;

    public TMP_InputField heightInputField;

    public TMP_InputField fpsInputField;

    public TMP_InputField bitrateInputField;

    public Toggle trackingDataTog;
    public Button expImageButton;
    public Text expImageLabel;
    public Image expImageBackground;

    private Action _confirm;
    private Action _cancel;
    private int _draftExpImage;
    private bool _draftOutputRaw;
    private bool _draftEnableEis;
    private bool _draftEnableMctf;
    private bool _draftEnableMfnr;
    private bool _expUiReady;
    private FlagButton[] _flagButtons;
    private Sprite _sharedButtonSprite;
    private Image.Type _sharedButtonType = Image.Type.Sliced;
    private float _sharedPpu = 1f;
    private Font _labelFont;
    private Vector2 _confirmButtonSize = new Vector2(116.55f, 30f);

    private sealed class FlagButton
    {
        public string Label;
        public string PrefsKey;
        public bool DefaultOn;
        public Func<bool> GetDraft;
        public Action<bool> SetDraft;
        public Button Button;
        public Image Background;
        public Text Text;
    }

    public int ResolutionWidth
    {
        get { return PlayerPrefs.GetInt("RecordResolutionWidth", 2160); }
        set { PlayerPrefs.SetInt("RecordResolutionWidth", value); }
    }

    public int ResolutionHeight
    {
        get { return PlayerPrefs.GetInt("RecordResolutionHeight", 810); }
        set { PlayerPrefs.SetInt("RecordResolutionHeight", value); }
    }

    public int Fps
    {
        get { return PlayerPrefs.GetInt("RecordFps", 30); }
        set { PlayerPrefs.SetInt("RecordFps", value); }
    }

    public int Bitrate
    {
        get { return PlayerPrefs.GetInt("RecordBitrate", 20 * 1024 * 1024); }
        set { PlayerPrefs.SetInt("RecordBitrate", value); }
    }

    public bool RecordTrackingData { get; private set; }

    /// <summary>0 = existing Vision path, 1 = professional for4U API path. Applied only on Confirm.</summary>
    public int ExpImage { get; private set; }

    public bool ExpOutputRaw { get; private set; }
    public bool ExpEnableEis { get; private set; }
    public bool ExpEnableMctf { get; private set; }
    public bool ExpEnableMfnr { get; private set; }

    private void Start()
    {
        widthInputField.text = ResolutionWidth.ToString();
        heightInputField.text = ResolutionHeight.ToString();
        fpsInputField.text = Fps.ToString();
        bitrateInputField.text = Bitrate.ToString();
        if (trackingDataTog != null)
        {
            trackingDataTog.isOn = true;
        }

        EnsureExpImageUi();
        ResetDraftExpImage();
    }

    public void Show(Action confirm, Action cancel)
    {
        gameObject.SetActive(true);
        _confirm = confirm;
        _cancel = cancel;
        EnsureExpImageUi();
        ResetDraftExpImage();
        if (widthInputField != null)
        {
            widthInputField.text = ResolutionWidth.ToString();
        }

        if (heightInputField != null)
        {
            heightInputField.text = ResolutionHeight.ToString();
        }

        if (fpsInputField != null)
        {
            fpsInputField.text = Fps.ToString();
        }

        if (bitrateInputField != null)
        {
            bitrateInputField.text = Bitrate.ToString();
        }
    }

    public void OnConfirm()
    {
        gameObject.SetActive(false);
        ResolutionWidth = int.Parse(widthInputField.text);
        ResolutionHeight = int.Parse(heightInputField.text);
        Fps = int.Parse(fpsInputField.text);
        Bitrate = int.Parse(bitrateInputField.text);
        RecordTrackingData = trackingDataTog != null && trackingDataTog.isOn;
        ExpImage = _draftExpImage;
        SaveFlagPrefs();
        if (ExpImage == 1)
        {
            ExpOutputRaw = _draftOutputRaw;
            ExpEnableEis = _draftEnableEis;
            ExpEnableMctf = _draftEnableMctf;
            ExpEnableMfnr = _draftEnableMfnr;
        }
        else
        {
            ExpOutputRaw = false;
            ExpEnableEis = false;
            ExpEnableMctf = false;
            ExpEnableMfnr = false;
        }

        if (_confirm != null)
        {
            _confirm.Invoke();
        }
    }

    public void OnCancel()
    {
        gameObject.SetActive(false);
        ResetDraftExpImage();
        if (_cancel != null)
        {
            _cancel.Invoke();
        }
    }

    private void ResetDraftExpImage()
    {
        _draftExpImage = 0;
        ExpImage = 0;
        LoadFlagPrefsIntoDraft();
        ApplyExpImageVisual();
        ApplyFlagVisuals();
    }

    private void LoadFlagPrefsIntoDraft()
    {
        // Defaults: Raw off, EIS off, MCTF off, MFNR on.
        _draftOutputRaw = PlayerPrefs.GetInt("RecordExpRaw", 0) == 1;
        _draftEnableEis = PlayerPrefs.GetInt("RecordExpEis", 0) == 1;
        _draftEnableMctf = PlayerPrefs.GetInt("RecordExpMctf", 0) == 1;
        _draftEnableMfnr = PlayerPrefs.GetInt("RecordExpMfnr", 1) == 1;
        // Raw conflicts with processed-image flags; prefer Raw if both were saved on.
        if (_draftOutputRaw && (_draftEnableEis || _draftEnableMctf || _draftEnableMfnr))
        {
            _draftEnableEis = false;
            _draftEnableMctf = false;
            _draftEnableMfnr = false;
        }
    }

    private void SaveFlagPrefs()
    {
        PlayerPrefs.SetInt("RecordExpRaw", _draftOutputRaw ? 1 : 0);
        PlayerPrefs.SetInt("RecordExpEis", _draftEnableEis ? 1 : 0);
        PlayerPrefs.SetInt("RecordExpMctf", _draftEnableMctf ? 1 : 0);
        PlayerPrefs.SetInt("RecordExpMfnr", _draftEnableMfnr ? 1 : 0);
    }

    private void ToggleExpImage()
    {
        _draftExpImage = _draftExpImage == 0 ? 1 : 0;
        ApplyExpImageVisual();
        ApplyFlagVisuals();
    }

    private void ApplyExpImageVisual()
    {
        ApplyToggleVisual(expImageButton, expImageBackground, expImageLabel, "exp_Image",
            _draftExpImage == 1, true);
    }

    private void ApplyFlagVisuals()
    {
        if (_flagButtons == null)
        {
            return;
        }

        bool enabled = _draftExpImage == 1;
        for (int i = 0; i < _flagButtons.Length; i++)
        {
            FlagButton flag = _flagButtons[i];
            if (flag == null || flag.Button == null)
            {
                continue;
            }

            ApplyToggleVisual(flag.Button, flag.Background, flag.Text, flag.Label,
                flag.GetDraft(), enabled);
        }
    }

    private static void ApplyToggleVisual(Button button, Image background, Text label, string text,
        bool on, bool interactable)
    {
        Color baseColor = !interactable
            ? new Color(0.75f, 0.75f, 0.75f, 1f)
            : (on ? new Color(0.45f, 0.45f, 0.45f, 1f) : Color.white);

        if (background != null)
        {
            background.color = Color.white;
            background.raycastTarget = interactable;
        }

        if (button != null)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = baseColor;
            colors.highlightedColor = Color.Lerp(baseColor, Color.black, 0.12f);
            colors.pressedColor = Color.Lerp(baseColor, Color.black, 0.28f);
            colors.selectedColor = baseColor;
            colors.disabledColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.interactable = interactable;
        }

        if (label != null)
        {
            label.text = text;
            label.color = interactable ? Color.black : new Color(0.35f, 0.35f, 0.35f, 1f);
            label.alignment = TextAnchor.MiddleCenter;
        }
    }

    private void EnsureExpImageUi()
    {
        if (_expUiReady)
        {
            LayoutExpRow();
            ApplyExpImageVisual();
            ApplyFlagVisuals();
            return;
        }

        Transform panel = transform.Find("Image");
        if (panel == null && transform.childCount > 0)
        {
            panel = transform.GetChild(0);
        }

        if (panel == null)
        {
            return;
        }

        Transform frame = panel.Find("Frame");
        Transform cancel = panel.Find("Cancel");
        Transform confirm = panel.Find("Confirm");
        if (confirm != null)
        {
            RectTransform confirmRect = confirm as RectTransform;
            if (confirmRect != null)
            {
                _confirmButtonSize = confirmRect.sizeDelta;
            }

            Image confirmImage = confirm.GetComponent<Image>();
            if (confirmImage != null && confirmImage.sprite != null)
            {
                _sharedButtonSprite = confirmImage.sprite;
                _sharedButtonType = confirmImage.type;
                _sharedPpu = confirmImage.pixelsPerUnitMultiplier;
            }
        }

        if (_sharedButtonSprite == null)
        {
            _sharedButtonSprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            _sharedButtonType = Image.Type.Sliced;
        }

        _labelFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_labelFont == null)
        {
            _labelFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        if (frame != null)
        {
            RectTransform frameRect = frame as RectTransform;
            if (frameRect != null)
            {
                frameRect.anchoredPosition = new Vector2(frameRect.anchoredPosition.x, 48f);
            }
        }

        if (cancel != null)
        {
            RectTransform cancelRect = cancel as RectTransform;
            if (cancelRect != null)
            {
                cancelRect.anchoredPosition = new Vector2(cancelRect.anchoredPosition.x, -100f);
            }
        }

        if (confirm != null)
        {
            RectTransform confirmRect = confirm as RectTransform;
            if (confirmRect != null)
            {
                confirmRect.anchoredPosition = new Vector2(confirmRect.anchoredPosition.x, -100f);
            }
        }

        RectTransform panelRect = panel as RectTransform;
        if (panelRect != null && panelRect.sizeDelta.y < 250f)
        {
            panelRect.sizeDelta = new Vector2(panelRect.sizeDelta.x, 250f);
        }

        if (expImageButton == null || expImageBackground == null || expImageLabel == null)
        {
            CreateExpImageButton(panel);
        }
        else
        {
            expImageButton.onClick.RemoveListener(ToggleExpImage);
            expImageButton.onClick.AddListener(ToggleExpImage);
        }

        EnsureFlagButtons(panel);
        LayoutExpRow();
        _expUiReady = true;
        ApplyExpImageVisual();
        ApplyFlagVisuals();
    }

    private void CreateExpImageButton(Transform panel)
    {
        GameObject buttonObject = new GameObject("exp_Image", typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(panel, false);
        buttonObject.layer = panel.gameObject.layer;

        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = _confirmButtonSize;
        buttonRect.localScale = Vector3.one;

        expImageBackground = buttonObject.GetComponent<Image>();
        expImageBackground.sprite = _sharedButtonSprite;
        expImageBackground.type = _sharedButtonType;
        expImageBackground.pixelsPerUnitMultiplier = _sharedPpu;
        expImageBackground.raycastTarget = true;
        expImageBackground.color = Color.white;

        expImageButton = buttonObject.GetComponent<Button>();
        expImageButton.transition = Selectable.Transition.ColorTint;
        expImageButton.targetGraphic = expImageBackground;
        Navigation navigation = expImageButton.navigation;
        navigation.mode = Navigation.Mode.None;
        expImageButton.navigation = navigation;
        expImageButton.onClick.RemoveListener(ToggleExpImage);
        expImageButton.onClick.AddListener(ToggleExpImage);

        expImageLabel = CreateButtonLabel(buttonObject.transform, panel.gameObject.layer, 14);
    }

    private void EnsureFlagButtons(Transform panel)
    {
        if (_flagButtons != null)
        {
            return;
        }

        _flagButtons = new[]
        {
            CreateFlagSpec("Raw", "RecordExpRaw", false,
                () => _draftOutputRaw, v => _draftOutputRaw = v),
            CreateFlagSpec("EIS", "RecordExpEis", false,
                () => _draftEnableEis, v => _draftEnableEis = v),
            CreateFlagSpec("MCTF", "RecordExpMctf", false,
                () => _draftEnableMctf, v => _draftEnableMctf = v),
            CreateFlagSpec("MFNR", "RecordExpMfnr", true,
                () => _draftEnableMfnr, v => _draftEnableMfnr = v)
        };

        for (int i = 0; i < _flagButtons.Length; i++)
        {
            CreateFlagButton(panel, _flagButtons[i]);
        }
    }

    private FlagButton CreateFlagSpec(string label, string prefsKey, bool defaultOn,
        Func<bool> getter, Action<bool> setter)
    {
        return new FlagButton
        {
            Label = label,
            PrefsKey = prefsKey,
            DefaultOn = defaultOn,
            GetDraft = getter,
            SetDraft = setter
        };
    }

    private void CreateFlagButton(Transform panel, FlagButton flag)
    {
        GameObject buttonObject = new GameObject("exp_" + flag.Label, typeof(RectTransform),
            typeof(CanvasRenderer), typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(panel, false);
        buttonObject.layer = panel.gameObject.layer;

        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);
        buttonRect.sizeDelta = new Vector2(52f, _confirmButtonSize.y);
        buttonRect.localScale = Vector3.one;

        flag.Background = buttonObject.GetComponent<Image>();
        flag.Background.sprite = _sharedButtonSprite;
        flag.Background.type = _sharedButtonType;
        flag.Background.pixelsPerUnitMultiplier = _sharedPpu;
        flag.Background.raycastTarget = true;
        flag.Background.color = Color.white;

        flag.Button = buttonObject.GetComponent<Button>();
        flag.Button.transition = Selectable.Transition.ColorTint;
        flag.Button.targetGraphic = flag.Background;
        Navigation navigation = flag.Button.navigation;
        navigation.mode = Navigation.Mode.None;
        flag.Button.navigation = navigation;
        FlagButton captured = flag;
        flag.Button.onClick.AddListener(() =>
        {
            if (_draftExpImage != 1)
            {
                return;
            }

            bool next = !captured.GetDraft();
            captured.SetDraft(next);
            // Raw (unprocessed) conflicts with EIS/MCTF/MFNR processing flags.
            if (next && captured.Label == "Raw")
            {
                _draftEnableEis = false;
                _draftEnableMctf = false;
                _draftEnableMfnr = false;
            }
            else if (next && (captured.Label == "EIS" || captured.Label == "MCTF" ||
                              captured.Label == "MFNR"))
            {
                _draftOutputRaw = false;
            }

            ApplyFlagVisuals();
        });

        flag.Text = CreateButtonLabel(buttonObject.transform, panel.gameObject.layer, 12);
    }

    private Text CreateButtonLabel(Transform parent, int layer, int fontSize)
    {
        GameObject labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
        labelObject.transform.SetParent(parent, false);
        labelObject.layer = layer;
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        labelRect.localScale = Vector3.one;
        Text label = labelObject.AddComponent<Text>();
        label.font = _labelFont;
        label.fontSize = fontSize;
        label.alignment = TextAnchor.MiddleCenter;
        label.color = Color.black;
        label.raycastTarget = false;
        label.horizontalOverflow = HorizontalWrapMode.Overflow;
        label.verticalOverflow = VerticalWrapMode.Overflow;
        return label;
    }

    private void LayoutExpRow()
    {
        const float rowY = -26f;
        const float gap = 6f;
        float expWidth = _confirmButtonSize.x;
        float flagWidth = 52f;
        int flagCount = _flagButtons != null ? _flagButtons.Length : 0;
        float total = expWidth + flagCount * (flagWidth + gap);
        float startX = -total * 0.5f + expWidth * 0.5f;

        if (expImageButton != null)
        {
            RectTransform expRect = expImageButton.transform as RectTransform;
            if (expRect != null)
            {
                expRect.anchoredPosition = new Vector2(startX, rowY);
                expRect.sizeDelta = _confirmButtonSize;
                expImageButton.transform.SetAsLastSibling();
            }
        }

        float x = startX + expWidth * 0.5f + gap + flagWidth * 0.5f;
        if (_flagButtons == null)
        {
            return;
        }

        for (int i = 0; i < _flagButtons.Length; i++)
        {
            FlagButton flag = _flagButtons[i];
            if (flag == null || flag.Button == null)
            {
                continue;
            }

            RectTransform rect = flag.Button.transform as RectTransform;
            if (rect != null)
            {
                rect.anchoredPosition = new Vector2(x, rowY);
                rect.sizeDelta = new Vector2(flagWidth, _confirmButtonSize.y);
            }

            flag.Button.transform.SetAsLastSibling();
            x += flagWidth + gap;
        }
    }
}
