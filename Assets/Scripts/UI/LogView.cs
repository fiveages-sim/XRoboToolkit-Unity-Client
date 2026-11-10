using System.Collections.Concurrent;
using UnityEngine;
using UnityEngine.UI;

public class LogView : MonoBehaviour
{
    public const int LogCacheCount = 20;
    private const int MaxWaiting = 64;
    public Text itemTmp;

    private static readonly ConcurrentQueue<LogData> Waitings = new ConcurrentQueue<LogData>();
    private static LogView _instance;

    private RectTransform _root;
    private ScrollRect _scrollRect;
    private bool _change;

    private void Awake()
    {
        _instance = this;
        itemTmp.gameObject.SetActive(false);
        _root = (RectTransform)itemTmp.transform.parent;
        _scrollRect = itemTmp.GetComponentInParent<ScrollRect>();
    }

    private void OnDestroy()
    {
        if (_instance == this)
        {
            _instance = null;
        }
    }

    public static void Push(string condition, string stackTrace, LogType type)
    {
        // No consumer mounted → drop immediately (avoids unbounded growth / OOM).
        if (_instance == null)
        {
            return;
        }

        if (Waitings.Count >= MaxWaiting)
        {
            Waitings.TryDequeue(out _);
        }

        Waitings.Enqueue(new LogData
        {
            Condition = condition,
            StackTrace = stackTrace,
            LogType = type
        });
    }

    private void Update()
    {
        if (_change)
        {
            _scrollRect.verticalNormalizedPosition = 0;
            _change = false;
        }

        while (Waitings.TryDequeue(out LogData data))
        {
            Text item;
            if (_root.childCount > LogCacheCount)
            {
                item = _root.GetChild(0).GetComponent<Text>();
                item.transform.SetSiblingIndex(_root.childCount - 1);
            }
            else
            {
                item = Instantiate(itemTmp, _root);
            }

            item.gameObject.SetActive(true);

            if (data.LogType == LogType.Error || data.LogType == LogType.Exception)
            {
                item.color = Color.red;
            }
            else if (data.LogType == LogType.Warning || data.LogType == LogType.Assert)
            {
                item.color = Color.yellow;
            }
            else
            {
                item.color = Color.white;
            }

            item.text = data.Condition;
            _change = true;
        }
    }

    private struct LogData
    {
        public string Condition;
        public string StackTrace;
        public LogType LogType;
    }
}
