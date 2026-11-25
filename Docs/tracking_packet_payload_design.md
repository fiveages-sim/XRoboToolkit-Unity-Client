# Tracking payload 打包设计评审

主题：`Tracking` 包把多个数据源捆在一个 JSON 里、以最快源的节奏整体重发，这在"单独 Head / Head+Controller / Head+Controller+Hand"三种组合下分别付出多少代价，以及该怎么改。

数据来自本机 `Docs/tools/fake_headset.py`（按真机形态造包）+ `Docs/tools/tracking_probe.py`（PC 侧解析统计），源频率取真机 Enterprise 300Hz 采样下的典型值：Head 300Hz、Controller 90Hz、Hand 90Hz。

## 当前打包形态

`Assets/Scripts/Network/TcpHandler.cs:660` 起的 tracking 线程，每个"出包时刻"做一次：

```
{"functionName":"Tracking","value":"{\"Head\":{...},\"Controller\":{...},\"Hand\":{...},\"timeStampNs\":...,\"appState\":...,\"Input\":1}"}
```

- 出包节奏由一个"主源"决定：Head 开且就绪时用 Head 的 seq 变化（`TcpHandler.cs:663`），否则退到 Controller / Hand / Body / Motion。
- 包里带的是**各源的最新缓存值**，不区分"这一包是它新采的"还是"上一包的原样"。
- `timeStampNs`：构包时刻用 `Utils.GetElapsedRealtimeNs()`（CLOCK_BOOTTIME，与 PICO 帧时间同族，纳秒单调），供 fa-py 帧门；另带 `unixNs` 墙钟。
- 只有 Controller 自带采样 `timeStampNs`（来自 pose），Head / Hand 在源对象内仍无 per-source seq/ts（见 P1）。

## 三种组合的代价（实测）

| 组合 | 包率 | 带宽（含 15B 帧头） | payload 里"原样重发"的部分 |
|---|---|---|---|
| 单独 Head | 300/s | 67 KiB/s | 0%（Head 本身 300Hz，每包都是新样本） |
| 单独 Controller | 90/s | 106 KiB/s | 0%（Controller 当主源，节奏 90Hz） |
| 单独 Hand | 90/s | 513 KiB/s | 0%（Hand 当主源，节奏 90Hz） |
| Head + Controller | 300/s | 386 KiB/s | 64%（Controller 90Hz 被 300Hz 重发） |
| Head + Controller + Hand（全部） | 300/s | 2058 KiB/s | **69%（1638 → 491 KiB/s 即可）** |

关键结论：**单独发任何一路都不亏**（节奏跟着那一路走）；亏在组合——一旦 Head 在，包率被拉到 300Hz，
其它 90Hz 的源就按 300Hz 全量重发。

- 单独 Head 时带宽很小（67KiB/s 含帧头，payload 29KiB/s），大头不在 Head。
- 一旦打开 Hand，单包涨到约 6.9KB（26 关节 × 2 手，`TrackingData.cs:513` 里每关节 `"x,y,z,qx,qy,qz,qw"` 十进制字符串），300Hz 重发 → **2 MB/s**，而 Hand 实际只更新 90Hz。
- 也就是说 **69% 的流量是同一个值被重复发了几十次**（每 3.3ms 发一次、每 11ms 才变一次）。
- 反过来说：Hand 单独发只要 513KiB/s，全部打开变成 2058KiB/s，多出来的 1.5MB/s 全是重复。

三种"该怎么发"的对比（同一个 Head300/Controller90/Hand90 输入）：

| 方案 | 包率 | 带宽 | 相对现状 |
|---|---|---|---|
| `bundle` 现状：每 Head 包带全部源 | 300/s | 2059 KiB/s | — |
| `bundle-changed`：只带本包有新样本的源 | 300/s | 665 KiB/s | **−68%** |
| `split`：每个源事件单独出包 | 480/s | 685 KiB/s | −67%（包数变多、单包变小） |

`bundle-changed` 与 `split` 带宽接近，差别在语义：`split` 每个包的 `timeStampNs` 天然就是该源自己的时间；`bundle-changed` 仍共享一个时间戳。

## 四个具体问题

1. **节奏被最快源绑架**：Hand 90Hz 的数据被 300Hz 重发，白付 3.3 倍带宽和序列化/转义/UTF8 编码 CPU。
2. **没有 per-source 的 seq / ts**：接收端无法判断"这个 Hand 值是新的还是 1.5 秒前的"。实测 `stale-at-send p50 6.5ms / p99 10ms`（Hand 90Hz 时）；若 Head 开到 1024Hz，Hand 的陈旧度会到 11ms 量级，而包时间戳看起来是"刚刚"。
3. **时间戳精度与时钟族**：`Utils.GetCurrentTimestamp()` 以 ms 为粒度（`Utils.cs:12-17`），300Hz（3.3ms/包）下量化误差可达 30% 周期，且 `UtcNow` 会随 NTP 跳变；工程里已经有 `elapsedRealtimeNanos`（CLOCK_BOOTTIME，与 PICO 帧时间同族，见 `Utils.cs:19-24`），tracking 包却没用它。
4. **纯文本 + 二次转义**：`value` 是"JSON 里的字符串"，所有引号被再转义一次（`TcpHandler.cs:792`），体积 +8~10%，还要多一遍全量拷贝；浮点用 `ToString("R")` 十进制字符串（`TrackingData.cs:551`），一个 7 维位姿约 70B，二进制是 28B。

## 建议的改法（按风险从低到高）

- **P0 只发变化的源**（无协议新增字段）：出包时对比各源 seq，未变化的源不带该 key。带宽 −75%。**已落地为 `TcpHandler.omitUnchangedSources`（默认 true）**；fa-py/pybind 对缺 key 沿用缓存。关开关即恢复官方全量捆。另有 `autoThrottleEnabled`：超 `bandwidthBudgetKiBps` 时按优先级下调 Hand/Body/Motion 采样帽（Head 不砍）。
- **P1 每源 seq + tsNs**：在每个源对象里加 `"seq"`、`"tsNs"`，接收端可做丢帧检测与按源插值；`timeStampNs` 改成 `elapsedRealtimeNanos`，另留 `unixNs` 给需要墙钟的消费者。接收端只需忽略未知字段，向后兼容。
- **P2 每源独立节奏 / 分通道**：Head 300Hz 一包（~90B），Controller 90Hz 一包（~700B），Hand 90Hz 一包（~5KB）。语义最干净，但每条包只含一个源，接收端必须做状态合并。
- **P3 二进制载荷**：固定 offset 布局，Hand 从 5KB 降到 ~1.9KB，省掉转义和字符串格式化。需要协议版本号协商，收益是 CPU，不只是带宽。

在 P0/P1 落地前，不建议动 P2/P3：当前 Sender 线程优化后已只占 3.7% 单核，**剩余卡顿主要来自出包节拍与落盘 I/O，而不是序列化**。

## 复测方法

工具分工：
- **真机**：`tracking_probe.py` / `selftest.sh --live` — 面板 CollectionRate 生效；输出像 `ros2 topic hz`
- **模拟**：`selftest.sh`（无 `--live`）+ `fake_headset.py` — **忽略面板**，频率写死在脚本里

`Docs/tools/selftest.sh 63901 6 detail` 以前很容易误以为在测真机；现在会拒绝，并提示改用 `--live`。

真机（面板设 400 → 期望 average rate ≈ 400，前提 Head+Send 开）：

```bash
ss -lntp | rg 63901                                 # 先停掉 PC 服务
Docs/tools/selftest.sh --live                       # 或: python3 Docs/tools/tracking_probe.py
# 每秒类似：
#   average rate: 399.847
#       min: 0.00210s max: 0.00355s std dev: 0.00021s window: 400
#       send 399.8Hz  .... KiB/s  Head:399.8chg/...
```

模拟对比三种打法 / 源矩阵（面板无关）：

```bash
Docs/tools/selftest.sh --suite designs --seconds 6
Docs/tools/selftest.sh --suite solo --detail
Docs/tools/selftest.sh --rates Head=400,Controller=90,Hand=90   # 想看 400 就写进 --rates
```

设备侧 logcat 对照：

```bash
adb logcat -d -v threadtime | rg "TCP tracking send rate"
```
