using Robot;
using UnityEngine;
using UnityEngine.UI;

public class EnterpriseSampleRateUI : MonoBehaviour
{
    public CustomButton IncreaseButton;
    public CustomButton DecreaseButton;
    public Text SampleHzText;

    [Min(0)]
    public int Step = 50;

    private EnterpriseCollectionRecorder _recorder;
    private Text _increaseLabel;
    private Text _decreaseLabel;
    private int _lastDisplayedHz = int.MinValue;
    private int _lastStep = int.MinValue;

    private void Awake()
    {
        if (IncreaseButton != null)
        {
            IncreaseButton.OnChange += OnIncreaseButton;
            _increaseLabel = IncreaseButton.GetComponentInChildren<Text>(true);
        }

        if (DecreaseButton != null)
        {
            DecreaseButton.OnChange += OnDecreaseButton;
            _decreaseLabel = DecreaseButton.GetComponentInChildren<Text>(true);
        }

        _recorder = FindObjectOfType<EnterpriseCollectionRecorder>();
        RefreshButtonText(force: true);
        RefreshText(force: true);
    }

    private void OnValidate()
    {
        Step = Mathf.Max(0, Step);
        RefreshButtonText(force: true);
    }

    private void OnDestroy()
    {
        if (IncreaseButton != null)
        {
            IncreaseButton.OnChange -= OnIncreaseButton;
        }

        if (DecreaseButton != null)
        {
            DecreaseButton.OnChange -= OnDecreaseButton;
        }
    }

    private void Update()
    {
        if (_recorder == null)
        {
            _recorder = FindObjectOfType<EnterpriseCollectionRecorder>();
        }

        RefreshButtonText(force: false);
        RefreshText(force: false);
    }

    public void IncreaseSampleHz()
    {
        AdjustSampleHz(Step);
    }

    public void DecreaseSampleHz()
    {
        AdjustSampleHz(-Step);
    }

    private void OnIncreaseButton(bool _)
    {
        IncreaseSampleHz();
    }

    private void OnDecreaseButton(bool _)
    {
        DecreaseSampleHz();
    }

    public void RefreshText()
    {
        RefreshText(force: true);
    }

    private void RefreshText(bool force)
    {
        if (SampleHzText == null)
        {
            return;
        }

        int hz = _recorder != null ? _recorder.EnterpriseSampleHz : 0;
        if (!force && hz == _lastDisplayedHz)
        {
            return;
        }

        _lastDisplayedHz = hz;
        SampleHzText.text = _recorder != null ? "Hz: " + hz : "0";
    }

    private void RefreshButtonText(bool force)
    {
        if (!force && Step == _lastStep)
        {
            return;
        }

        _lastStep = Step;
        if (_increaseLabel != null)
        {
            _increaseLabel.text = "+" + Step;
        }

        if (_decreaseLabel != null)
        {
            _decreaseLabel.text = "-" + Step;
        }
    }

    private void AdjustSampleHz(int delta)
    {
        if (_recorder == null)
        {
            _recorder = FindObjectOfType<EnterpriseCollectionRecorder>();
        }

        if (_recorder == null)
        {
            RefreshText(force: true);
            return;
        }

        _recorder.AdjustEnterpriseSampleHz(delta);
        RefreshText(force: true);
    }
}
