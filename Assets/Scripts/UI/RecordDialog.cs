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
    private bool _expUiReady;

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
        ApplyExpImageVisual();
    }

    private void ToggleExpImage()
    {
        _draftExpImage = _draftExpImage == 0 ? 1 : 0;
        ApplyExpImageVisual();
    }

    private void ApplyExpImageVisual()
    {
        Color baseColor = _draftExpImage == 0
            ? Color.white
            : new Color(0.45f, 0.45f, 0.45f, 1f);

        if (expImageBackground != null)
        {
            expImageBackground.color = Color.white;
            expImageBackground.raycastTarget = true;
        }

        if (expImageButton != null)
        {
            ColorBlock colors = expImageButton.colors;
            colors.normalColor = baseColor;
            colors.highlightedColor = Color.Lerp(baseColor, Color.black, 0.12f);
            colors.pressedColor = Color.Lerp(baseColor, Color.black, 0.28f);
            colors.selectedColor = baseColor;
            colors.disabledColor = new Color(baseColor.r, baseColor.g, baseColor.b, 0.5f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.05f;
            expImageButton.colors = colors;
            expImageButton.interactable = true;
        }

        if (expImageLabel != null)
        {
            expImageLabel.text = "exp_Image";
            expImageLabel.color = Color.black;
            expImageLabel.alignment = TextAnchor.MiddleCenter;
        }
    }

    private void EnsureExpImageUi()
    {
        if (_expUiReady)
        {
            if (expImageButton != null)
            {
                expImageButton.transform.SetAsLastSibling();
            }

            ApplyExpImageVisual();
            return;
        }

        if (expImageButton != null && expImageBackground != null && expImageLabel != null)
        {
            expImageButton.onClick.RemoveListener(ToggleExpImage);
            expImageButton.onClick.AddListener(ToggleExpImage);
            expImageButton.transform.SetAsLastSibling();
            _expUiReady = true;
            ApplyExpImageVisual();
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
        Vector2 buttonSize = new Vector2(116.55f, 30f);
        if (confirm != null)
        {
            RectTransform confirmRect = confirm as RectTransform;
            if (confirmRect != null)
            {
                buttonSize = confirmRect.sizeDelta;
            }
        }

        if (frame != null)
        {
            RectTransform frameRect = frame as RectTransform;
            if (frameRect != null)
            {
                frameRect.anchoredPosition = new Vector2(frameRect.anchoredPosition.x, 40f);
            }
        }

        if (cancel != null)
        {
            RectTransform cancelRect = cancel as RectTransform;
            if (cancelRect != null)
            {
                cancelRect.anchoredPosition = new Vector2(cancelRect.anchoredPosition.x, -88f);
            }
        }

        if (confirm != null)
        {
            RectTransform confirmRect = confirm as RectTransform;
            if (confirmRect != null)
            {
                confirmRect.anchoredPosition = new Vector2(confirmRect.anchoredPosition.x, -88f);
            }
        }

        RectTransform panelRect = panel as RectTransform;
        if (panelRect != null && panelRect.sizeDelta.y < 230f)
        {
            panelRect.sizeDelta = new Vector2(panelRect.sizeDelta.x, 230f);
        }

        GameObject buttonObject = new GameObject("exp_Image", typeof(RectTransform), typeof(CanvasRenderer),
            typeof(Image), typeof(Button));
        buttonObject.transform.SetParent(panel, false);
        buttonObject.layer = panel.gameObject.layer;
        buttonObject.transform.SetAsLastSibling();

        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.5f, 0.5f);
        buttonRect.anchorMax = new Vector2(0.5f, 0.5f);
        buttonRect.pivot = new Vector2(0.5f, 0.5f);
        buttonRect.anchoredPosition = new Vector2(0f, -26f);
        buttonRect.sizeDelta = buttonSize;
        buttonRect.localScale = Vector3.one;

        expImageBackground = buttonObject.GetComponent<Image>();
        Image confirmImage = confirm != null ? confirm.GetComponent<Image>() : null;
        if (confirmImage != null && confirmImage.sprite != null)
        {
            expImageBackground.sprite = confirmImage.sprite;
            expImageBackground.type = confirmImage.type;
            expImageBackground.pixelsPerUnitMultiplier = confirmImage.pixelsPerUnitMultiplier;
        }
        else
        {
            expImageBackground.sprite = Resources.GetBuiltinResource<Sprite>("UI/Skin/UISprite.psd");
            expImageBackground.type = Image.Type.Sliced;
        }

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

        GameObject labelObject = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer));
        labelObject.transform.SetParent(buttonObject.transform, false);
        labelObject.layer = panel.gameObject.layer;
        RectTransform labelRect = labelObject.GetComponent<RectTransform>();
        labelRect.anchorMin = Vector2.zero;
        labelRect.anchorMax = Vector2.one;
        labelRect.offsetMin = Vector2.zero;
        labelRect.offsetMax = Vector2.zero;
        labelRect.localScale = Vector3.one;
        expImageLabel = labelObject.AddComponent<Text>();
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font == null)
        {
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        expImageLabel.font = font;
        expImageLabel.fontSize = 14;
        expImageLabel.alignment = TextAnchor.MiddleCenter;
        expImageLabel.color = Color.black;
        expImageLabel.raycastTarget = false;
        expImageLabel.horizontalOverflow = HorizontalWrapMode.Overflow;
        expImageLabel.verticalOverflow = VerticalWrapMode.Overflow;

        _expUiReady = true;
        ApplyExpImageVisual();
    }
}
