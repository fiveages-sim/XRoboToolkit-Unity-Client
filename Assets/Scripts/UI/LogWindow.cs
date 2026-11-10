using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LogWindow : MonoBehaviour
{
    private const int MaxLines = 200;

    public TextMeshProUGUI text;

    public ScrollRect scrollRect;

    private readonly Queue<string> _lines = new Queue<string>();
    private bool _scrollPending;

    private static LogWindow _instance;
    private static readonly ConcurrentQueue<string> PendingMessages = new ConcurrentQueue<string>();
    private static int _mainThreadId;

    public RectTransform rectTransform;

    private void Awake()
    {
        _instance = this;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        FlushPendingMessages();
    }

    private void Update()
    {
        FlushPendingMessages();
    }

    private IEnumerator AutoScrollCoroutine()
    {
        LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.content as RectTransform);
        yield return new WaitForEndOfFrame(); // Wait one frame for layout to update

        // Update rectTransform height based on text content
        UpdateRectTransformHeight();

        scrollRect.verticalNormalizedPosition = 0f;
        _scrollPending = false;
    }

    private void UpdateRectTransformHeight()
    {
        if (rectTransform != null && text != null)
        {
            // Force the text to update its preferred height
            text.ForceMeshUpdate();

            // Get the preferred height of the text
            float preferredHeight = text.preferredHeight;

            // Update the rectTransform height
            Vector2 sizeDelta = rectTransform.sizeDelta;
            sizeDelta.y = preferredHeight + 10f; // Add some padding
            rectTransform.sizeDelta = sizeDelta;
        }
    }

    public void AppendText(string message)
    {
        if (_instance == null)
        {
            PendingMessages.Enqueue(message);
            return;
        }

        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            PendingMessages.Enqueue(message);
            return;
        }

        if (_instance != null)
        {
            // add time prefix of local timezone to the message
            string timePrefix = $"[{System.DateTime.Now:HH:mm:ss}] ";
            _instance._lines.Enqueue($"{timePrefix}{message}");
            while (_instance._lines.Count > MaxLines)
            {
                _instance._lines.Dequeue();
            }

            // Keep the text bounded: appending with += copies the whole buffer and a long
            // session otherwise ends up rebuilding an ever growing text mesh per line.
            _instance.text.text = string.Join("\n", _instance._lines);

            if (!_instance._scrollPending)
            {
                _instance._scrollPending = true;
                StartCoroutine(AutoScrollCoroutine());
            }
        }
    }

    private static void Message(string message)
    {
        if (_instance == null)
        {
            PendingMessages.Enqueue(message);
            return;
        }

        if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
        {
            PendingMessages.Enqueue(message);
            return;
        }

        _instance.AppendText(message);
    }

    private static void FlushPendingMessages()
    {
        if (_instance == null)
        {
            return;
        }

        while (PendingMessages.TryDequeue(out string message))
        {
            _instance.AppendText(message);
        }
    }

    public static void Info(string info)
    {
        // white color text
        Message($"<color=white>{info}</color>");
    }

    public static void Warn(string info)
    {
        // yellow color text
        Message($"<color=yellow>{info}</color>");
    }

    public static void Error(string info)
    {
        // red color text
        Message($"<color=red>{info}</color>");
    }
}
