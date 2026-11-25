# exp_crop A/B + 内外参 + 不裁切黑边 MP4

## 面板

`exp_Image=1` 时多一个 **Crop**（`exp_crop`，Prefs `RecordExpCrop`，默认开）：

| 配置 | 含义 | 编码尺寸 |
|------|------|----------|
| **A** Raw关 · Crop任意 · LEFT | processed direct（验证「PICO processed FOV」） | 2048×1536 |
| **B** Raw开 · **Crop关** | 保留官方 SBS 半幅+黑边 | **4096×1536** |
| 对照 Raw开 · Crop开 | 旧路径：GLES 半裁 | 2048×1536 |

建议 A/B 都关 EIS/MCTF；A 可关 MFNR 便于对比。

## 会话落盘（临时传送内外参）

开相机成功后写入：

- `session_*/meta.json` → `capture.calibration`、`cameraIntrinsicsE` / `cameraExtrinsicsE`
- `session_*/camera_calib.json`
- `session_*/camera_params.txt`（PC 熟悉的 Extrinsics/Intrinsics 文本）
- `persistentDataPath/camera_params_last.txt`（便于 adb）

标定域固定 **2048×1536** 单眼；**无 k1/k2**。

## 离线输出不裁切带黑边 MP4

```bash
adb pull /sdcard/Download/CameraCollection/session_<id> ./session_<id>
python3 Docs/tools/raw_sbs_letterbox_mp4.py ./session_<id>
# → uncropped_black_border.mp4 + uncropped_black_border_note.json
```

- B（已是 4096 SBS）：默认 stream copy 保留黑边  
- 若误开 Crop 录成 2048：脚本把该眼贴回 4096 画布另一半填黑  
- `--force-sbs`：processed 单目也可做成对比用黑边 SBS  

## 判读

比 A 的 2048 有效内容张角 vs B 的 **非黑半幅** 张角。实测常见：B 左半更宽更完整；A（processed）更窄。

## 内外参脚本为什么“没变化”

`raw_sbs_letterbox_mp4.py` **不会**用内外参去畸变（无 k1/k2）。对已是 4096 SBS 的 B 几乎是 copy；对 Crop 开的 2048 只是补回黑边。不是导入错视频。

## 还原「PICO 采到的」——用 Buf

面板：`exp_Image` + **Buf**（自动 Raw、Crop 关）→ CPU buffer 落 `frames/*.jpg`，无 StartPreview / 无 eye-crop。

```bash
adb pull …/session_<id> .
python3 Docs/tools/buffer_frames_to_mp4.py ./session_<id>
```

## Vision（不开 exp_Image）底下被裁

旧默认 **2160×810** 超宽会砍竖直 FOV。新默认 **2048×1536**；若 Prefs 仍是旧值请改高度，或改用 Buf。
