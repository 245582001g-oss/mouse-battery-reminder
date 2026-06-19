# Sora V2 Battery 提示

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
- 配置、profile、音效、日志和电量历史保存在 `文档\SoraV2BatteryTip`。

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
