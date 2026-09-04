# DEBUG 飞行记录器说明

## 目标

`flight-current.jsonl` 是默认开启、只追加的事实日志。它让一次鼠标状态变化可以按 `op_id` 从 Windows 事件一路追到 HID 枚举、provider 读取、状态合并、历史、提醒、轮询间隔与托盘图标。

实时文件：

```text
%USERPROFILE%\Documents\SoraV2BatteryTip\logs\flight-current.jsonl
```

运行中跟随：

```powershell
Get-Content "$env:USERPROFILE\Documents\SoraV2BatteryTip\logs\flight-current.jsonl" -Wait
```

## 固定字段

- `ts_utc` / `ts_local`：UTC 与本地墙钟时间。
- `mono_ms`：从本次程序启动开始的单调时间，不受系统改钟影响。
- `seq`：本次运行严格递增的事件序号。
- `run_id`：本次启动 ID。
- `op_id`：一次完整检测的关联 ID。
- `level`、`event`、`component`、`outcome`：严重度、稳定事件名、来源组件、结果。
- `device`：稳定关联 token、原始设备名称/path/serial、VID/PID、电量、电源状态、数据新鲜度和 provider。
- `data`：该事件自己的白名单事实。
- `error`：错误类别、异常类型、真实错误消息、HRESULT 和调用栈指纹。

## 如何判断“事实”和“推导”

- `fact_basis=windows_wm_devicechange`：Windows 直接发送的 HID 到达/移除通知。
- `fact_basis=provider_reading`：设备 provider 本次实际接受的读取结果。
- `fact_basis=derived_from_consecutive_provider_readings`：比较同一设备前后两次读取后得到的插线、拔线、充电、电量等转变。
- `fact_basis=application_icon_assignment`：程序确实把哪个图标设置到了托盘。

provider 没有提供独立电缆位时，“插线”可能由充电位或外接电源位推导。日志会同时保留原始布尔事实与推导后的 `power_state`，避免把推导伪装成硬件直接上报。

## 关键事件

- 生命周期：`app.start`、`app.stop`、`app.previous_run_unclean`、`app.*exception`。
- 检测：`poll.requested`、`poll.started`、`poll.completed`、`poll.failed`、`poll.cancelled`。
- HID：`hid.inventory.*`、`system.device_arrival`、`system.device_removal`、`device_refresh.*`。
- provider：`provider.read_*`、`provider.device_open_failed`、`provider.io_failed`、`provider.parse_rejected`。
- 设备：`device.reading_observed`、`device.battery_changed`、`device.cable_connected`、`device.cable_disconnected`、`device.charging_started`、`device.charging_stopped`、`device.full_charge_reached`、`device.state_stale`、`device.state_recovered`、`device.state_offline`。
- UI：`tray.status_changed`、`tray.tooltip_changed`、`tray.icon_changed`、`poll.interval_changed`。
- 提醒：`alert.fired`、`alert.reset`、`alert.suppressed`、`sound.*`。
- 数据：`history.*`、`settings.*`、`profile.*`、`diagnostics.*`。

## 本机完整性与文件管理

- 日志同时写稳定 token 和原始 HID path、设备名、序列号；错误消息也保留，方便直接还原本机事实。
- provider 的 HID request/response payload 不单独写入日志，避免无界放大；请求类型、长度、超时、解析结果等事件会记录。
- 每条事件最多 16 KiB，数组和字符串有长度上限。
- 单后台写线程保证多 provider 并行时仍是一行一个完整 JSON；日志写入失败不会拖垮鼠标程序。
- 活动文件允许其他进程同时读取，但不允许第二个写入者交错破坏 JSONL。
- 达到 4 MiB、跨 UTC 日期或程序重启时轮转；归档最多 14 天、12 个文件、32 MiB。

## 提交问题时

右键托盘图标，选择“设备配置”→“导出诊断包”。导出器会保留本机状态、完整设置、profile、HID path/serial、历史 key/serial，并截取最近 5 MiB 的 flight 事件。`candidates` 中的大体积原始 HID 报告不会自动进入诊断包。诊断包定位为本机使用；如需分享，请先自行检查内容。
