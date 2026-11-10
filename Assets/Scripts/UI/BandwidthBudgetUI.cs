using Robot;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// On-device control for TcpHandler bandwidth budget (KiB/s). Wire +/- buttons like
/// EnterpriseSampleRateUI so Inspector defaults are not the only way to change it.
/// </summary>
public class BandwidthBudgetUI : MonoBehaviour
{
    public CustomButton IncreaseButton;
    public CustomButton DecreaseButton;
    public Text BudgetText;

    [Min(50f)]
    public float StepKiBps = 200f;

    private float _lastDisplayed = float.NaN;

    private void Awake()
    {
        if (IncreaseButton != null)
        {
            IncreaseButton.OnChange += OnIncrease;
        }

        if (DecreaseButton != null)
        {
            DecreaseButton.OnChange += OnDecrease;
        }

        RefreshText(force: true);
    }

    private void OnDestroy()
    {
        if (IncreaseButton != null)
        {
            IncreaseButton.OnChange -= OnIncrease;
        }

        if (DecreaseButton != null)
        {
            DecreaseButton.OnChange -= OnDecrease;
        }
    }

    private void Update()
    {
        RefreshText(force: false);
    }

    public void IncreaseBudget()
    {
        AdjustBudget(StepKiBps);
    }

    public void DecreaseBudget()
    {
        AdjustBudget(-StepKiBps);
    }

    private void OnIncrease(bool _)
    {
        IncreaseBudget();
    }

    private void OnDecrease(bool _)
    {
        DecreaseBudget();
    }

    private void AdjustBudget(float delta)
    {
        float next = Mathf.Max(100f, TcpHandler.SharedBandwidthBudgetKiBps + delta);
        TcpHandler.SetSharedBandwidthBudgetKiBps(next);
        RefreshText(force: true);
    }

    private void RefreshText(bool force)
    {
        if (BudgetText == null)
        {
            return;
        }

        float budget = TcpHandler.SharedBandwidthBudgetKiBps;
        if (!force && Mathf.Approximately(budget, _lastDisplayed))
        {
            return;
        }

        _lastDisplayed = budget;
        BudgetText.text = "BW: " + budget.ToString("F0");
    }
}
