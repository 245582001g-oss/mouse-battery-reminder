# Sora V2 Battery 提示

[English](README.md) | 简体中文

一个极简 Windows 托盘工具，用于显示 Ninjutso SORA V2 鼠标电量状态，并在低电量时播放提示音。

最初目标很简单：打游戏时，不要等鼠标突然没电、失去控制之后才发现电量不足。

## 当前状态

- 当前稳定支持设备：**Ninjutso SORA V2**。
- 当前版本通过 Windows HID 设备接口通知同步判断 SORA V2 插线 / 无线状态。
- 电量百分比通过开发过程中确认的 SORA V2 HID Feature Report 路径读取。
- 当前项目**还不是通用无线鼠标电量读取工具**。

## 为什么开源

Windows 对很多 2.4G 游戏鼠标并没有统一、可靠的电量读取 API。不同品牌可能把电量放在私有 HID 报告、Vendor Defined Usage Page、网页驱动、桌面驱动或私有协议里。

这个项目公开 SORA V2 上可行的实现路径，让其他人可以研究、验证和扩展，也希望未来能逐步兼容更多无线鼠标。

## 核心功能

- 作为托盘程序运行。
- 左键点击托盘图标：立即检测一次电量。
- 右键点击托盘图标：调整阈值、检测间隔、提醒冷却、语言、提示音、音量、开机自启、卸载。
- 插线状态：托盘图标切换为静态充电图标。
- 拔线 / 无线状态：托盘图标切回电池图标。
- 电池图标按 5% 档位绘制，避免 1% 变化就频繁重绘。
- 低电量提示音可自定义 WAV。
- 默认提示音音量为 15%，除非用户自行修改。
- 配置和音效保存在 `文档\SoraV2BatteryTip`。

## 最关键的实现经验

最终丝滑版本的关键是：**USB 插拔瞬间完全不查询 HID 电量**。

早期版本曾经这样做：

```text
USB 设备变化
-> 枚举 HID
-> 打开 HID 接口
-> 发送 Feature Report
-> 解析电量
```

这样会在拔线、无线接收器恢复、Windows HID 栈重新枚举的瞬间打扰鼠标，导致鼠标短暂假死。

稳定版本改成：

```text
Windows HID 接口通知
-> 只读取通知里的设备路径字符串
-> VID_1915 PID_AE12 表示 SORA V2 有线接口
-> VID_1915 PID_AE1C 表示 SORA V2 无线接口
-> 立即更新托盘状态
-> 插拔瞬间绝不打开 HID、绝不发包
```

电量读取只发生在：

- 程序启动时
- 定时检测时
- 用户手动立即检测时

这个拆分是整个项目流畅的核心。

## SORA V2 电量读取路径

SORA V2 当前使用 HID Feature Report 读取电量：

1. 根据 VID/PID 找到 SORA V2 HID 设备。
2. 使用 report `0x05` 读取 profile index。
3. 使用 report `0x04` 加 profile index 请求状态。
4. 从返回 payload 中解析电量、充电、满电、模式、在线状态。
5. 对切换过程中的异常瞬时值做稳定化处理，例如 `1%`。

当前已知 ID：

```text
VID: 0x1915
有线 PID: 0xAE12
无线 PID: 0xAE1C
```

## 未来计划

长期目标是尽可能兼容更多无线鼠标。欢迎熟悉 HID、USB、WebHID、Vendor Defined Report、游戏鼠标固件协议的人参与。

可能方向：

- Provider 架构，支持不同设备族。
- 已知鼠标配置文件。
- 安全 HID 诊断模式。
- 学习模式：让用户输入官方驱动 / 网页驱动显示的电量，对比原始 HID 报告候选字节。
- 社区提交 JSON profile。
- 更通用地识别电量、充电、满电、在线、有线 / 无线状态。

## 编译

需要 Windows 和 .NET 8 Desktop Runtime / SDK。

```powershell
dotnet publish .\src\SoraV2BatteryTip\SoraV2BatteryTip.csproj -c Release -r win-x64 --self-contained false -o .\releases\latest
```

运行：

```powershell
.\releases\latest\SoraV2BatteryTip.exe
```

## 数据目录

```text
%USERPROFILE%\Documents\SoraV2BatteryTip
```

包含：

- `settings.json`
- `status.json`
- `sounds\*.wav`

## 许可证

MIT
