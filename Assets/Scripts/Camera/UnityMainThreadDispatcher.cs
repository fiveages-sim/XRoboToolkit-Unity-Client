using System;
using System.Collections.Generic;
using UnityEngine;

public class UnityMainThreadDispatcher : MonoBehaviour
{
    private static UnityMainThreadDispatcher _instance;
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();

    public static UnityMainThreadDispatcher Instance()
    {
        if (_instance == null)
        {
            var obj = new GameObject("UnityMainThreadDispatcher");
            _instance = obj.AddComponent<UnityMainThreadDispatcher>();
            DontDestroyOnLoad(obj);
        }

        return _instance;
    }

    public static void Enqueue(Action action)
    {
        if (action == null)
        {
            return;
        }

        lock (_executionQueue)
        {
            _executionQueue.Enqueue(action);
        }
    }

    public void EnqueueInstance(Action action)
    {
        Enqueue(action);
    }

    void Update()
    {
        while (true)
        {
            Action action;
            lock (_executionQueue)
            {
                if (_executionQueue.Count == 0)
                {
                    break;
                }

                action = _executionQueue.Dequeue();
            }

            try
            {
                action?.Invoke();
            }
            catch (Exception exception)
            {
                Debug.LogError("[UnityMainThreadDispatcher] " + exception);
            }
        }
    }
}