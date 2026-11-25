# XRoboToolkit-Unity-Client (Enterprise / G1 fork)

面向 PICO 4 Ultra Enterprise（含 Swan / 4UE 机型）的机器人遥操作与数据采集 Unity 客户端。

本仓库是 [XR-Robotics/XRoboToolkit-Unity-Client](https://github.com/XR-Robotics/XRoboToolkit-Unity-Client) 的**二次开发分支**，在官方 `EnterpriseSDK` 基础上补齐了「头显 ↔ 操作端」的双向音视频、录制状态回传、企业级高频追踪与崩溃诊断，并强化了设备端录制管线。

| 项 | 值 |
|---|---|
| 官方上游 | https://github.com/XR-Robotics/XRoboToolkit-Unity-Client |
| 本仓库 | https://github.com/fiveages-sim/XRoboToolkit-Unity-Client |
| 分叉基点 | 上游 `EnterpriseSDK` @ `93c6072`（2026-08-10，含 PR #23） |
| 开发分支 | `enterprise-3.4.0`，已推为远端 `main` |
| 许可 | MIT（继承上游，见 [LICENSE.txt](LICENSE.txt)） |

---

## 1. 基础：官方仓库与本分支的关系

### 1.1 继承的官方能力

以下功能来自上游，本仓库未改变其行为：

- **位姿同步**：把 PICO 头显的 Head / Controller / Hand / Motion Tracker 位姿通过 TCP 发送给机器人侧 PC，用于遥操作。
- **本地数据采集**：将立体视觉（VST）与位姿同步录制到 `/sdcard/Download`。
- **头显间远程视觉**：机器端头显采集，操作端头显实时观看（SBS / 立体 3D 切换）。
- **PC / Orin 相机远程视觉**：`ZEDMINI` 视频源 + [XRoboToolkit-Orin-Video-Sender](https://github.com/XR-Robotics/XRoboToolkit-Orin-Video-Sender)。
- **工程基线**：Unity `2022.3.26f1c1`、Android minSdk 30 / targetSdk 31、IL2CPP、PICO Integration SDK **3.4.0**（vendored 于 `PICO Unity Integration SDK/`）。

> 上游原始文档（含完整 UI 面板说明、问答与构建细节）已原样保留：
> [README.upstream.md](README.upstream.md) ・ [README.upstream.cn.md](README.upstream.cn.md)

### 1.2 分支谱系

```
upstream/EnterpriseSDK 93c6072 (2026-08-10)
        │
        └── enterprise-3.4.0   ← 本分支（13 个提交）
              ├─ 2778ad6  相机采集管线修复 + camera/UI 脚本更新
              ├─ e42ddcc  Frame muxer / API frame session
              ├─ 523292a  for4U Surface 编码路径
              ├─ 7e104a1  Hand（实测 >300Hz）+ Tracker
              ├─ 0798fd8  for4U 单目视野叠裁修复、vision 模式停止卡死修复
              ├─ 8867045 / c247a9a  采样率与面板一致（Head 200Hz 稳定，Hand 受 SDK 限制）
              ├─ c0dd787  远程视觉 / 双向音频 / 企业级追踪强化
              └─ 5cdc2b4  停止跟踪本机设置与 macOS 元数据
```

同步上游：

```bash
git fetch upstream
git merge upstream/EnterpriseSDK      # 或 git rebase upstream/EnterpriseSDK
```

---

## 2. 不同处：本分支新增与修改

### 2.1 双向音频（Duplex Audio）

官方只有「视频下行」，本分支补齐了操作端与机器端的语音链路。

| 方向 | 组件 | 端口 | 规格 |
|---|---|---|---|
| 下行（听对方） | `Assets/Scripts/Audio/RemotePcmAudioPlayer.cs` | `13580`（可被视频源 `AudioStreamPort` 覆盖） | PCM 16 kHz 单声道；prebuffer 160 ms / 目标缓冲 300 ms / 硬上限 1000 ms；默认数字增益 6 |
| 上行（说给对方） | `Assets/Scripts/Audio/PicoMicrophoneStreamer.cs` | `13581` | 重采样到 16 kHz 单声道，20 ms 一帧，队列上限 5 帧，超限丢旧帧 |
| 重采样计划 | `Assets/Scripts/Audio/MicrophoneCapturePlan.cs` | — | 纯函数式分块 / 积压计算，便于单测 |

- 上行带**会话令牌鉴权**（`G1AT` 认证 / `G1AF` 音频帧 / `G1AH` 心跳，schema `g1_wuji_audio_uplink_v1`）；令牌缺失或非法时跳过上传并提示，不会静默失败。
- 端口由控制通道 `AUDIO_CONFIG`（`g1_wuji_audio_ports_v2`）协商，协商失败回落配置默认值。
- 需要 `android.permission.RECORD_AUDIO`（已加入 `AndroidManifest.xml`）。

### 2.2 远程视觉控制通道重写

| 文件 | 说明 |
|---|---|
| `Assets/Scripts/Network/OperatorControlClient.cs` | 托管 C# 的长度前缀（length-framed）控制客户端，替代 AAR 中「单次 Read 取 4 字节长度 + 多线程共享 socket」的实现 |
| `Assets/Scripts/UI/UICameraCtrl.RemoteVision.cs` | 远端视觉主逻辑：`OPEN_CAMERA` / `OPEN_CAMERA_ACK` / `CLOSE_CAMERA` / `AUDIO_CONFIG` / `RECORD_STATUS` / `PING` 状态机 |
| `Assets/Scripts/Network/NetworkCommander.cs` | 命令常量与语义 |
| `Assets/Scripts/Network/TcpManager.cs` | 设备端默认监听 `13579`，保证头显间 `OPEN_CAMERA` 有对端可接 |

关键修复：

- **read-exact 语义**：长度前缀跨多次 `Read` 也能正确拼接，不再依赖单次读取。
- **run generation**：旧连接线程无法关闭/篡改新连接，避免重连风暴。
- **部分帧读取超时 15 s**（等下一个长度前缀不设 idle 超时）。头显间源在 `OPEN_CAMERA` 后常静默，原先 5 s 超时会触发 Java `TcpServer` EOF → Listen 反复重协商 → 画面永久黑屏。
- **`PING` 作为传输心跳**在接收线程内直接回，不产生 UI 事件。

### 2.3 远端录制状态叠层

`Assets/Scripts/Camera/RemoteRecordStatusOverlay.cs`

- 解析 `g1_wuji_record_status_v1`：`state` / `operator_time` / `episode` / `frame_count`，载荷上限 4 KiB。
- 渲染在头显上，**独立于 H.264 视频通路**——视频卡顿时状态依然可信。
- 仅在状态变化时写 breadcrumb，避免日志刷爆。

### 2.4 投影模式：平面窗口 / 全景天空盒

`Assets/Scripts/Camera/RemoteVideoProjectionRenderer.cs`

| 维度 | 取值 |
|---|---|
| 投影 | `flat`（平面浮窗，兼容旧行为）、`equirectangular`（等距柱状全景天空盒） |
| 立体布局 | `mono` / `side_by_side` / `top_bottom` |

- 只有当操作端**显式声明** `equirectangular` 元数据才切天空盒，绝不把普通平面相机伪造成全景。
- 会话结束恢复原 skybox、clear flags 与 `RawImage` 状态。
- 对应视频源 `Panorama`（官方 SBS `2560×640`，30 fps，6 Mbps）。

### 2.5 地址记忆与校验

`Assets/Scripts/UI/RemoteVisionAddressStore.cs`

- 规范化 IPv4 / IPv6（拒绝 `%scope`、拒绝 `::`、拒绝组播等）。
- 记住上次成功的连接目标，重开 Listen 无需重输。

### 2.6 企业级高频追踪

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/EnterpriseCollection/EnterpriseCollectionRecorder.cs` | 经 Enterprise SDK 直接取硬件数据（Head 最高 1000 Hz / Controller 500 Hz），含控制器位姿有效性过滤（>10 m 丢弃） |
| `Assets/Scripts/EnterpriseCollection/EnterpriseAuxTrackingSampler.cs` | Hand / Body / Motion 独立线程采样，**latest-only 缓存**；采样失败即清缓存，避免把过期 Hand 粘在 Head 包上；默认跟随面板 `CollectionRate` |
| `Assets/Scripts/EnterpriseCollection/CollectionSessionWriter.cs` | `CameraCollection/session_*/` 落盘：每源 jsonl + `meta.json`（schema v1） |
| `Assets/Scripts/EnterpriseCollection/TrackingConfigSnapshot.cs` | 录制开始时冻结追踪配置，便于数据回溯 |
| `Assets/Scripts/ExtraDev/MotionTrackerConnectionCache.cs` | 追踪器连接状态缓存，减少重复连接抖动 |
| `Assets/Scripts/UI/TrackingDataSourceCtrl.cs` | 面板切换 `UnityXR` / `EnterpriseSDK` 数据源 |
| `Assets/Scripts/UI/EnterpriseSampleRateUI.cs` | 面板直接调采样率（步进 50 Hz），无需改 Inspector |
| `Assets/Scripts/UI/BandwidthBudgetUI.cs` | 面板直接调 `TcpHandler` 带宽预算（KiB/s，步进 200） |

实测结论（详见 [Docs/tracking_packet_payload_design.md](Docs/tracking_packet_payload_design.md)）：

- Head 稳定 200 Hz（一般 120 Hz）；Hand 单发可 >300 Hz，但受 SDK 自身限制。
- 现有「按最快源整体重发」在 Head+Controller+Hand 全开时约 **2058 KiB/s**，其中 **69% 是重复样本**；改用 `bundle-changed`（只带本包有新样本的源）可降到 **665 KiB/s（−68%）**，且协议无需改动。

### 2.7 录制管线强化

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/Recording/VisionTrackingSession.cs` | 官方 Vision `StartRecord` 会话：开始/停止均校验，文件验证通过才算完成 |
| `Assets/Scripts/Recording/SurfaceEncodeProbe.cs` | for4U **Surface + MediaCodec** 路径，输出 `for4u_CameraRecord_*.mp4` + `for4u_frame_map_*.jsonl` |
| `Assets/Scripts/Recording/For4UBufferDumpSession.cs` | for4U **CPU Buffer dump**（最贴近 PICO 原始数据）：`SetCameraFrameBufferfor4U` + `StartGetImageDatafor4U`，不经 `StartPreview` / GLES 裁切 / capturelib Vision，落 JPEG 帧 |
| `Assets/Scripts/Recording/CapturePipeline.cs` | 幂等的捕获状态推进（`PXRCaptureState` 0–6 语义不可重映射） |
| `Assets/Scripts/Recording/CaptureReport.cs` | 会话报告：编码路径、时间戳策略、丢帧、磁盘预算等 |
| `Assets/Scripts/Recording/DiskSafety.cs` | 开录前与录制中检查空间：`free > 1.5 × 估算 + 2 GB` |
| `Assets/Scripts/RecordingCuePlayer.cs` | 开始 / 停止 / 删除 / 错误 提示音（44.1 kHz，非空间音频） |
| `Assets/Scripts/Camera/CameraHandle.cs` | 推流参数与 4UE VST `-99 (PREPROCESS_ERROR)` 规避 |

`RecordDialog` 新增实验开关（面板可直接切）：

| 开关 | Prefs Key | 默认 | 含义 |
|---|---|---|---|
| `exp_Image` | — | 关 | 走 for4U Surface 编码；关闭则走官方 Vision 路径 |
| `Raw` | `RecordExpRaw` | 关 | 输出 Raw（保留官方 SBS 半幅 + 黑边） |
| `Crop` | `RecordExpCrop` | **开** | GLES 半裁；`Raw 开 + Crop 关` → 4096×1536 不裁切 |
| `Buf` | `RecordExpBuf` | 关 | 与 `exp_Image` 同开 → CPU Buffer dump（JPEG 帧） |
| `EIS` | `RecordExpEis` | 关 | 电子防抖 |
| `MCTF` | `RecordExpMctf` | 关 | 运动补偿时域滤波 |
| `MFNR` | `RecordExpMfnr` | **开** | 多帧降噪 |

编码尺寸规则：`3D` 或 `Raw && !Crop` → **4096×1536**（SBS）；否则单眼 **2048×1536**。

### 2.8 崩溃诊断

`Assets/Scripts/Diagnostics/CrashProbe.cs`

- 输出目录：`{persistentDataPath}/g1_wuji_crash_probe/`（schema `g1_pico_crash_probe_v1`）。
- `active_session.json` 存活哨兵（5 s 心跳）+ `last_exit.json` 退出记录 + `breadcrumbs.jsonl`（上限 1 MiB / 2000 行自动裁剪）。
- 主线程**卡顿 10 s** 看门狗（2 s 轮询），用于定位 Pico Listen 类主线程 stall。

### 2.9 配置与工程改动

- `Assets/StreamingAssets/video_source.yml`
  - `PICO4U`：1280×480 @30 fps / 5 Mbps（避免 4UE VST 推流 `-99`），新增 `AudioStreamPort: 13580`。
  - `Panorama`：SBS 2560×640 @30 fps / 6 Mbps。
  - `ZEDMINI`：新增 `AudioStreamPort`。
- `ProjectSettings/ProjectSettings.asset`：`productName` → `XRoboToolkit-Enterprise`，`bundleVersion` → `1.1.2`；应用 ID 仍为 `com.xrobotoolkit.client`。
- `Assets/Plugins/Android/AndroidManifest.xml`：新增 `RECORD_AUDIO`。

### 2.10 工具与文档（新增）

| 路径 | 用途 |
|---|---|
| `Docs/tools/fake_headset.py` | 假冒头显，无真机即可压 PC 接收端 |
| `Docs/tools/tracking_probe.py` | 扮演 PC 服务，剖析 Tracking 流速率 / 带宽 |
| `Docs/tools/tracking_recv_diag.py` | 接收 Enterprise Tracking TCP 并诊断真实接收率 |
| `Docs/tools/buffer_frames_to_mp4.py` | `exp_buf=1` 的 JPEG 帧 → MP4 |
| `Docs/tools/raw_sbs_letterbox_mp4.py` | Raw Surface 会话布局还原（**仅布局，不做去畸变**） |
| `Docs/tools/selftest.sh` | 不用真机测发送速率 / 带宽 |
| `Docs/for4u_frames_to_mp4.sh` | 按 sessionId 合成 `CameraRecord_*.mp4` |
| `Docs/analyze_encode_timestamps.sh` | Vision / Surface / JPEG 会话对时分析 |
| `scripts/probe_enterprise_recv_hz.py` | 企业级接收帧率探针 |

配套设计文档：[encode_timestamp_gate.md](Docs/encode_timestamp_gate.md)、[exp_crop_ab_verify.md](Docs/exp_crop_ab_verify.md)、[for4u_frames_session.md](Docs/for4u_frames_session.md)、[enterprise_collection_data_guide.md](Docs/enterprise_collection_data_guide.md)、[enterprise_tracking_pipeline_design.md](Docs/enterprise_tracking_pipeline_design.md)。

---

## 3. 使用方法

### 3.1 环境要求

- Unity **2022.3.26f1c1**（其他 2022.3.x 未验证）
- Android SDK（minSdk 30 / targetSdk 31）+ NDK（Unity Android 模块自带即可）
- PICO Integration SDK **3.4.0**（已 vendored，无需另行安装）
- 真机：PICO 4 Ultra **Enterprise** / Swan / 4UE（VST 相机需 Pico 企业团队开通权限）

### 3.2 打开与打包

1. Unity Hub 添加本目录，用 `2022.3.26f1c1` 打开。
2. `File → Build Settings → Android → Switch Platform`。
3. 签名：`Player Settings → Publishing Settings`，首次构建用 Keystore Manager 新建。
4. 构建（任选）：
   - 菜单 **Build → One-click packaging**（Windows `Ctrl+Shift+B` / macOS `Cmd+Shift+B`），输出到 `Builds/Android/`；
   - `EnterpriseBuild.BuildAndroid()`（`Assets/Editor/EnterpriseBuild.cs`）；
   - `python3 build_unity_android.py <unityPath> <productName> <method>`。
5. 安装：`adb install -r Builds/Android/*.apk`

### 3.3 位姿同步（遥操作）

1. 机器端 PC 与头显接入同一局域网。
2. PC 端启动服务：Windows `C:\Program Files (x86)\roboticsservice\runService.bat`；Linux `/opt/apps/roboticsservice/runService.sh`。
3. 头显打开 App，在连接提示中指向 PC IP，用扳机确认。
4. 主面板显示 `WORKING` 即连上；勾选要发送的 Head / Controller / Hand / Tracker，打开 `Send`。
5. 需要手柄快速暂停：打开 `Switch w/ A Button`，右手 A 键切换。

> 数据源默认走 Enterprise SDK；如需回退官方 Unity XR，用 `TrackingDataSourceCtrl` 切到 `UnityXR`。

### 3.4 远程视觉 + 双向语音

**A. 头显 → 头显（机器人眼睛）**

1. 两台头显同一网络，均打开 App。
2. 源端 H1（机器端）：确认已开通 VST 相机权限，**保持在 App 内不要操作**。
3. 操作端 H2：`Camera` 面板选 `PICO4U` → `Listen` → 输入 H1 的 IP → 确认。
4. 看到画面后：右手 **B** 键切换 SBS / 立体 3D；关闭窗口后重复第 3 步即可重连。
5. 语音：链路建立后自动协商音频端口（下行 13580 / 上行 13581），首次会请求麦克风权限。

**B. PC / Orin → 头显（ZED / 全景）**

```bash
# Orin 侧
./OrinVideoSender --listen <Orin IP>:13579
```

头显：`Camera` 选 `ZEDMINI`（或 `Panorama`）→ `Listen` → 填 Orin IP → 确认 → 右手 B 键切换视图。

**视频源热更新**（免重打包）：

```bash
adb pull /sdcard/Android/data/com.xrobotoolkit.client/files/video_source.yml
# 编辑后
adb push video_source.yml /sdcard/Android/data/com.xrobotoolkit.client/files/video_source.yml
# 恢复默认
adb shell rm /sdcard/Android/data/com.xrobotoolkit.client/files/video_source.yml
```

### 3.5 数据采集 / 录制

1. 主面板勾选 `Tracking`（要位姿）和/或 `Vision`（要图像），点 `Record`。
2. 在录制对话框中设置分辨率 / 帧率 / 码率 / 追踪开关，以及 `exp_Image / Raw / Crop / Buf / EIS / MCTF / MFNR`。
3. 再点 `Record` 停止；过程中屏幕会有短暂模糊、按钮变红、状态变 `6`（Vision 路径）。

**三条编码路径的选择：**

| 目标 | 设置 | 产物 |
|---|---|---|
| 官方 Vision（默认，最稳） | `exp_Image` 关 | `CameraRecord_*.mp4` + `recording_meta_*.json` + `trackingData_*.txt` |
| for4U Surface（硬编，推荐生产） | `exp_Image` 开，`Buf` 关 | `for4u_CameraRecord_*.mp4` + `for4u_frame_map_*.jsonl` |
| for4U CPU Buffer（最贴近原始） | `exp_Image` 开 + `Buf` 开 | `CameraCollection/session_*/frames/*.jpg` + `frames.jsonl` |

**取回与出片：**

```bash
adb pull /sdcard/Download ./pull/Download
./Docs/for4u_frames_to_mp4.sh ./pull/Download <sessionId>              # api-frames → MP4
python3 Docs/tools/buffer_frames_to_mp4.py <session_dir>               # exp_buf 帧 → MP4
./Docs/analyze_encode_timestamps.sh auto ./pull/Download <sessionId>   # 时间戳对齐分析
```

> 空间要求：开录前校验 `free > 1.5 × 估算 + 2 GB`。参考值：Vision ~750 MB / 20 min；Surface @8 Mbps ~1.2 GB / 20 min。

### 3.6 无真机的链路自测

```bash
./Docs/tools/selftest.sh                     # fake_headset + tracking_probe，测发送速率 / 带宽
python3 Docs/tools/tracking_recv_diag.py --port 13579
python3 scripts/probe_enterprise_recv_hz.py  # 企业级接收帧率
```

### 3.7 崩溃 / 卡顿排查

```bash
adb pull /sdcard/Android/data/com.xrobotoolkit.client/files/g1_wuji_crash_probe
cat g1_wuji_crash_probe/last_exit.json
tail -50 g1_wuji_crash_probe/breadcrumbs.jsonl
```

`active_session.json` 仍在但 App 已退出 → 上次异常终止；`breadcrumbs.jsonl` 中出现 `main_thread_stall` 表示主线程卡顿超过 10 s。

### 3.8 常见问题

| 现象 | 处理 |
|---|---|
| 无法签名 APK | 见 [Docs/unable_to_sign.png](Docs/unable_to_sign.png)，参考 Pico 官方「密钥关联 APK 数量超限」方案 |
| `No entitlement info` | 头显需联网启动一次 |
| Linux 下连不上 | 确认同一 WiFi，且先启动 PC 服务再启动头显 App |
| 4UE 推流报 `-99` | 降分辨率/码率，用本仓库默认 1280×480@30 / 5 Mbps |
| 画面黑屏但已 Listen | 检查 13579 是否被占用；头显间源端需保持 App 前台 |
| 麦克风无声音 | 确认已授权 `RECORD_AUDIO`；上行需要控制通道下发的有效会话令牌 |
| 采集画面比预期窄 | 见 [Docs/exp_crop_ab_verify.md](Docs/exp_crop_ab_verify.md)，试 `Raw 开 + Crop 关`（4096×1536） |

---

## 4. 仓库维护约定

**应提交**：`Assets/` 源码与资源、`ProjectSettings/`、`Packages/`、`Docs/`、`scripts/`、`README*`。

**不提交**（均已写入 `.gitignore`）：

| 路径 | 原因 |
|---|---|
| `Library/` `Temp/` `Logs/` `Build/` `Builds/` `obj/` | Unity 生成物 / 构建输出 |
| `UserSettings/` | 每台机器的编辑器布局与 Logcat 配置 |
| `.cursor/` `.vscode/` `.idea/` | 本地 IDE / Agent 配置 |
| `pull/` | `adb pull` 下来的采集数据（体积大） |
| `__pycache__/` `*.pyc` | Python 缓存 |
| `.DS_Store` | macOS 元数据 |
| `.upgrade-3.4.0-backup/` `PICO_logs/` | 本地临时目录 |

提交前自查（两条命令都应为空输出）：

```bash
git status --short
git ls-files | grep -E '\.DS_Store|^UserSettings/|\.cursor|__pycache__'
```

---

## 5. 致谢与引用

基于 XR Robotics 的 [XRoboToolkit](https://github.com/XR-Robotics/XRoboToolkit-Unity-Client)。

```
@article{zhao2025xrobotoolkit,
      title={XRoboToolkit: A Cross-Platform Framework for Robot Teleoperation},
      author={Zhigen Zhao and Liuchuan Yu and Ke Jing and Ning Yang},
      journal={arXiv preprint arXiv:2508.00097},
      year={2025}
}
```
