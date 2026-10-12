using Robot;
using UnityEditor;
using UnityEngine;

public static class CapturePipelineChecks
{
    [MenuItem("XRoboToolkit/Check Capture Pipeline")]
    public static void RunFromMenu()
    {
        string error = CapturePipeline.SelfCheck();
        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError("[CapturePipeline] " + error);
            return;
        }

        Debug.Log("[CapturePipeline] SelfCheck passed");
    }
}
