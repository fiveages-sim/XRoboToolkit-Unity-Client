using System;
using TMPro;
using UnityEngine;

public class ResolutionDialog : MonoBehaviour
{
    public TextMeshProUGUI titleTmp;
    public TMP_InputField widthInputField;

    public TMP_InputField heightInputField;

    public int ResolutionWidth
    {
        get { return PlayerPrefs.GetInt("ResolutionWidth", 1024); }
        set { PlayerPrefs.SetInt("ResolutionWidth", value); }
    }

    public int ResolutionHeight
    {
        get { return PlayerPrefs.GetInt("ResolutionHeight", 768 / 2); }
        set { PlayerPrefs.SetInt("ResolutionHeight", value); }
    }

    private Action _cancel;
    private Action<int, int> _confirm;

    public void Show(string title, Action<int, int> call, Action cancel)
    {
        gameObject.SetActive(true);
        titleTmp.text = title;
        widthInputField.text = ResolutionWidth.ToString();
        heightInputField.text = ResolutionHeight.ToString();
        _confirm = call;
        _cancel = cancel;
        if (widthInputField != null)
        {
            widthInputField.shouldHideMobileInput = false;
            widthInputField.Select();
            widthInputField.ActivateInputField();
        }
    }

    public void OnConfirm()
    {
        if (!TryParseClamped(widthInputField != null ? widthInputField.text : null, 16, 8192, out int width) ||
            !TryParseClamped(heightInputField != null ? heightInputField.text : null, 16, 8192, out int height))
        {
            Toast.Show("Invalid resolution");
            return;
        }

        gameObject.SetActive(false);
        ResolutionWidth = width;
        ResolutionHeight = height;
        if (_confirm != null)
        {
            _confirm.Invoke(ResolutionWidth, ResolutionHeight);
        }
    }

    private static bool TryParseClamped(string text, int minInclusive, int maxInclusive, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text.Trim(), out value))
        {
            return false;
        }

        value = Mathf.Clamp(value, minInclusive, maxInclusive);
        return true;
    }

    public void OnCancel()
    {
        gameObject.SetActive(false);
        if (_cancel != null)
        {
            _cancel.Invoke();
        }
    }
}