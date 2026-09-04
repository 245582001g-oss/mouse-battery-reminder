# 鼠标电量提醒

[English](README.md) | 简体中文

一个轻量级 Windows 托盘工具，用于显示无线游戏鼠标电量，并在低电量时播放提示音。

最初目标很简单：打游戏时，不要等鼠标突然没电、失去控制之后才发现电量不足。

## 当前状态

这个项目**目前还不是通用无线鼠标电量读取工具**。当前版本重点支持两个已经验证过的设备族，同时保留本地 profile / 自动学习流程作为备用方案。

已验证 / 部分支持：

- **Ninjutso SORA V2 部分型号**：使用从官方 NinjaForce 网页驱动里确认的 HID Feature Report 协议读取。
- **ATK / COMPX 系 2.4G 鼠标部分型号**：使用部分 8K NANO 接收器暴露的 COMPX HID 电量命令读取。
- 支持同时插入多个已支持鼠标，并在托盘菜单里一起显示。
- 未知设备仍可走本地 profile / 自动候选学习流程，但这是备用方案，需要用户用官方驱动显示的电量做一次确认。

已知限制：

- 很多无线鼠标使用私有 HID 报告、本地驱动桥、厂商服务或未公开协议。没有实物时，通常可以从公开网页驱动代码里提取读取方法，但无法完整验证真实设备行为。

## 当前版本支持的协议

### Ninjutso SORA V2 官方 HID

SORA V2 现在使用 NinjaForce 网页驱动中观察到的官方风格 HID Feature Report 读取电量。

已知 VID/PID 家族：

```text
VID: 0x1915
已知 / 预期产品 ID 包括：0xAE11-0xAE16, 0xAE1C, 0xAE8A, 0xAE8C
```

电量查询：

```text
Feature report ID: 0x05
命令字节:          0x15
请求 payload:      15 00 00 01 00 00 04 ...
电量字节:          response[9]
充电字节:          response[10] == 1
```

当这个内置 provider 命中时，旧的 `draft-1915-*` 学习 profile 会被主动忽略，所以 SORA V2 不再依赖猜测 offset。

### ATK / COMPX HID

部分 ATK / COMPX 2.4G 接收器会通过 vendor HID 命令暴露电量。

已知 VID 家族：

```text
VID: 0x373B
示例产品：Wireless mouse 8k NANO dongle-L
```

实现说明：

```text
Report ID:      0x08
Command:        0x04
Payload length: 16 bytes
电量字节:       从命令响应 payload 中解析
充电字节:       从命令响应 payload 中解析
```

这个 provider 只在开发时手头的设备上验证过。它应被理解为 ATK/COMPX 风格设备的部分支持，不代表所有 ATK 鼠标都一定可用。

## 核心功能

- 作为 Windows 托盘程序运行。
- 左键点击托盘图标：立即检测一次电量。
- 右键点击托盘图标：调整阈值、检测间隔、提醒冷却、语言、提示音、音量、开机自启、profile 工具、卸载。
- 多个已支持鼠标同时连接时，可一起显示。
- 插线 / 充电状态使用静态充电托盘图标。
- 无线状态使用电池托盘图标，并按 5% 档位绘制，避免频繁重绘。
- 低电量提示音可自定义 WAV。
- 默认提示音音量为 15%，除非用户自行修改。
- 配置、profile、音效、日志和电量历史保存在 `文档\SoraV2BatteryTip`（为兼容旧版本保留目录名）。

## 稳定性原则

最终流畅版本的关键是：**USB 插拔瞬间不要重度查询 HID**。

早期版本曾经这样做：

```text
USB 设备变化
-> 枚举 HID
-> 打开 HID 接口
-> 发送 Feature Report
-> 解析电量
```

这可能会在 Windows 和接收器重新稳定的瞬间打扰鼠标输入。

当前设计把两件事拆开：

```text
USB/HID 通知
-> 低成本更新状态或安排刷新
-> 插拔过渡期避免重度 HID 探测

程序启动 / 定时检测 / 用户手动检测
-> 通过 provider 读取电量
-> 更新托盘状态和历史记录
```

这是这个项目最重要的实现经验。

## 架构

电量读取采用 provider 架构：

```text
NinjutsoSoraOfficialProvider
-> CompxBatteryProvider
-> KnownDeviceProfileProvider / 本地学习 JSON profile
```

程序优先使用已验证的内置官方协议。本地学习 profile 适合暂时不值得写成内置 provider 的未知设备，但优先级更低，未来可以被内置 provider 替代。

### 状态与诊断流水线

```text
Windows 设备/电源通知
-> HidDeviceInventory（可靠快照缓存，失败快照短时重试）
-> BatteryProviderManager（并行 provider 读取与按设备去重）
-> NinjutsoSoraTransportPolicy（已验证的接收器配对与有线端点仲裁）
-> DeviceBatteryStateStore（多设备合并、短时失败保留、离线与身份迁移）
-> DevicePowerSemantics（统一插线、充电与满电语义）
-> BatteryHistoryStore / LowBatteryAlertTracker / ChargingPollingPolicy
-> TrayAppContext（状态文字、提示音、轮询计时器与实际图标）
```

所有关键分支同时向 `FlightRecorder` 发送结构化事件。记录器使用独立后台写线程；业务代码不直接写日志文件。一次检测的事件共享 `op_id`，设备同时带稳定关联 token 与原始名称/path/serial，因此可以直接还原 Windows 通知、实际 provider 读取、状态推导、提醒决定与图标赋值之间的完整顺序。

设备运行身份变化（例如重连后的新 path，或序列号从占位值升级为真实值）会由状态存储显式上报，并同步迁移低电量提醒与充电轮询状态，避免把同一只鼠标误当成新设备。

对已经验证配对关系的 SORA V2 接收器/直连端点，provider 只输出一个逻辑设备，同时保留两条原始 HID 路径作为该读数覆盖的端点。这样在 `Receiver` 与 `WiredUsb` 之间切换时，电量历史、提醒、轮询、USB 插拔刷新和飞行日志关联都保持连续。

身份层把电量曲线键、接收器路径关联键和稳定序列号代际分开处理。每个物理路径按时间保留最新的 `ConfirmReceiver` / `RejectPersistedReceiver` 事实；有线单端点恢复只采用当前仍有效的确认关系。换接收器时会开启新的提醒与充电轮询代际，不继承旧设备的告警抑制或快轮询状态。

发现配对不一致时，provider 会同时记录“接收器自身当前序列号代际”的确认事实，以及仅作用于有线配对端点的拒绝事实。因此，即使接收器在这一轮电量读取失败，只要仍然实际在场，也不会被错误地当成离线设备。

## 数据目录

```text
%USERPROFILE%\Documents\SoraV2BatteryTip
```

包含：

- `settings.json`
- `status.json`
- `sounds\*.wav`
- `profiles\*.json`
- 电量历史和诊断导出

## 编译

需要 Windows 和 .NET 8 Desktop Runtime / SDK。

```powershell
dotnet publish .\src\SoraV2BatteryTip\SoraV2BatteryTip.csproj -c Release -r win-x64 --self-contained false -o .\releases\latest
```

运行：

```powershell
.\releases\latest\SoraV2BatteryTip.exe
```

## 未来计划

长期目标是尽可能兼容更多无线鼠标。欢迎熟悉 HID、USB、WebHID、Vendor Defined Report、游戏鼠标固件协议的人参与。

有价值的贡献方向：

- 更多鼠标品牌的已验证协议。
- 安全诊断包：VID/PID、report 长度、匿名原始响应。
- 暂时不需要内置 provider 的设备 JSON profile。
- 更准确的充电、满电、在线状态识别。
- 厂商网页驱动 HID 逻辑分析。

## 许可证

MIT
