using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.UI;

public class Toast : MonoBehaviour
{
    private const int MaxQueuedMessages = 32;

    public GameObject Panel;
    public Text text;

    private static readonly ConcurrentQueue<string> Messages = new ConcurrentQueue<string>();
    private static Toast _instance;
    private float _lastTime;
    private float _hideStartTime;

    private void Awake()
    {
        _instance = this;
        if (Panel != null)
        {
            Panel.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    /// <summary>
    /// Thread-safe. Never touches UnityEngine objects here — Update() owns Panel/Text.
    /// </summary>
    public static void Show(string info)
    {
        if (string.IsNullOrEmpty(info))
        {
            return;
        }

        while (Messages.Count >= MaxQueuedMessages && Messages.TryDequeue(out _))
        {
        }

        Messages.Enqueue(info);
    }

    private void Update()
    {
        if (Panel == null || text == null)
        {
            return;
        }

        if (Time.time - _lastTime <= 1f)
        {
            return;
        }

        _lastTime = Time.time;
        if (Messages.TryDequeue(out string message))
        {
            if (!Panel.activeSelf)
            {
                Panel.SetActive(true);
            }

            text.text = message;
            _hideStartTime = Time.time;
        }
        else if (Panel.activeSelf && Time.time - _hideStartTime > 5f)
        {
            Panel.SetActive(false);
        }
    }

    [ContextMenu("Test")]
    private void Test()
    {
        Show("Test " + Time.time);
    }
}
