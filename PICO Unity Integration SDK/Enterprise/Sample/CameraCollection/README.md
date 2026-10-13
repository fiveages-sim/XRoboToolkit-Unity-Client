# CameraCollection（PICO 4 Ultra Enterprise）

Scripts for the standalone `PICO-CameraCapture` project.

| Script | Role |
| --- | --- |
| `MrSceneSetup.cs` | Strip stray cameras, XR UI EventSystem, wire rays, build panel |
| `CaptureSessionUI.cs` | World Canvas + `TrackedDeviceGraphicRaycaster` + for4U preview |
| `CameraFrameCapture.cs` | for4U open/configure/buffer/get; JPEG session; `Frame.pose` → jsonl |
| `PoseRecorder.cs` | Head + controllers → `poses.jsonl` while recording |
| `CameraCollectionWriter.cs` | `session_*/frames` + jsonl + `session.json` + `COMPLETE` |
| `CameraCaptureBootstrap.cs` | **No auto-start** (shared SDK must not inject UI into other projects). Place `CameraCaptureRoot` in the scene. |
| `CaptureSettings.cs` | Loads `Resources/CaptureSettings.json` |

Requires enterprise camera auth (package name + device SN). Video SeeThrough is display background only; capture source is for4U.
