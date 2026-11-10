using System.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

public class FPSDisplay : MonoBehaviour
{
    private const long StaleMilliseconds = 1500;

    public Text fpsText;
    private static float deltaTime;
    private static Stopwatch stopwatch;
    private static bool hasSample;

    private void Awake()
    {
        stopwatch = Stopwatch.StartNew();
    }

    /// <summary>Called from the tracking send thread when a packet is sent.</summary>
    public static void UpdateTime()
    {
        if (stopwatch == null)
        {
            stopwatch = Stopwatch.StartNew();
        }

        var unDeltaTime = stopwatch.ElapsedMilliseconds;
        stopwatch.Restart();
        deltaTime += (unDeltaTime - deltaTime) * 0.1f;
        hasSample = true;
    }

    public static void Reset()
    {
        deltaTime = 0f;
        hasSample = false;
        if (stopwatch != null)
        {
            stopwatch.Restart();
        }
    }

    private void Update()
    {
        if (fpsText == null || Time.frameCount % 10 != 0)
        {
            return;
        }

        bool stale = !hasSample || stopwatch == null || stopwatch.ElapsedMilliseconds > StaleMilliseconds;
        if (stale || deltaTime <= 0f)
        {
            fpsText.text = "FPS: --";
            return;
        }

        float fps = 1000.0f / deltaTime;
        fpsText.text = $"FPS: {fps:F1}";
    }
}
