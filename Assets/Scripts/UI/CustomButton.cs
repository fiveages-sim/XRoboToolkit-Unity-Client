using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public class CustomButton : MonoBehaviour, IPointerClickHandler
{
    public event Action<bool> OnChange;

    public Color selectedColor;

    private Color _defaultColor;

    private Image _image;

    public bool On { get; private set; }

    private void Awake()
    {
        _image = GetComponent<Image>();
        _defaultColor = _image.color;
    }

    public void SetOn(bool value)
    {
        On = value;
        if (_image == null)
        {
            _image = GetComponent<Image>();
        }

        _image.color = On ? selectedColor : _defaultColor;
    }

    /// <summary>
    /// Update visual state and invoke OnChange (e.g. Listen-off from RemoteCameraWindow close).
    /// </summary>
    public void SetOnAndNotify(bool value)
    {
        SetOn(value);
        OnChange?.Invoke(value);
    }

    public void SetVisual(bool engaged, bool recording, string label)
    {
        On = engaged;
        if (_image == null)
        {
            _image = GetComponent<Image>();
        }

        if (_defaultColor.a <= 0f && _image != null)
        {
            _defaultColor = _image.color;
        }

        if (recording)
        {
            _image.color = selectedColor;
        }
        else if (engaged)
        {
            _image.color = new Color(0.85f, 0.72f, 0.15f, 1f);
        }
        else
        {
            _image.color = _defaultColor;
        }

        Text labelText = GetComponentInChildren<Text>(true);
        if (labelText != null)
        {
            labelText.text = label;
            labelText.horizontalOverflow = HorizontalWrapMode.Overflow;
        }
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (OnChange != null)
        {
            OnChange.Invoke(!On);
        }
    }
}