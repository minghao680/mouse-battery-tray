# MouseBatteryTray

在 Windows 任务栏托盘实时显示 **2.4G 无线鼠标电量** 的小工具（无窗口、零依赖、单文件源码）。

托盘图标直接显示电量数字，颜色分级：<kbd>绿</kbd> >50% · <kbd>橙</kbd> 20–50% · <kbd>红</kbd> <20% · <kbd>灰</kbd> 未连接/休眠。

## 适用设备

针对 **VID_1EA7 / PID_0064**（总线名 "2.4G Mouse"）的接收器逆向而成。这是一颗常见的杂牌方案商芯片，大量廉价 2.4G 无线鼠标使用它。Windows 不为它创建电池节点（固件未实现标准 HID Battery 用法页 0x85），所以系统设置里看不到电量——本工具直接与接收器的厂商自定义 HID 接口对话。

> 其他 VID/PID 的接收器只要协议相同也能用：修改源码顶部 `VID` / `PID` 常量后重编译即可。

## 逆向得到的厂商协议（Report ID 0xB5）

接收器 Col01 接口（厂商自定义 usage page 0xFFB5）使用 8 字节请求/应答：

| 请求（主机 → 接收器） | 应答（接收器 → 主机） | 含义 |
|---|---|---|
| `B5 01 00 00 00 00 00 00` | `B5 01 01 <电量%> …` | 查询电量（最稳定） |
| `B5 05 <addr> 04 …` | `B5 05 <addr> 04 <d0 d1 d2 d3>` | 读芯片 RAM，**电量存于 RAM 0x38** |
| `B5 02 <addr> 04 …` | 同上结构 | 读 ROM 映射区 |
| `B5 20 / 23 / 28 …` | 各种射频参数 | 配对/RF 状态查询 |

注意：应答包尾部字节是**上一包残留**（固件缓冲区不清零），解析时只取 `B5 <cmd> <len> <data…>`。

## 使用

去 [Releases](../../releases) 下载 `MouseBatteryTray.exe`，放到任意**非**"文档"目录（如 `%LOCALAPPDATA%\MouseBatteryTray\`）运行：

| 操作 | 效果 |
|---|---|
| 左键点图标 | 气泡显示当前电量 |
| 右键菜单 | 立即刷新 / 轮询间隔(10s~5min) / 开机自启 / 退出 |
| `--once` | 命令行单次查询，输出如 `鼠标电量: 56%`（console 版） |
| `--interval=N` | 初始轮询间隔（秒，≥5，菜单修改后会持久化覆盖） |
| `--log` | 在 exe 旁写运行日志 |

> **为什么不能放"文档"目录**：部分 HIPS（如火绒）会按路径拦截"文档目录中的未知程序"注册托盘图标（`Shell_NotifyIcon` 返回拒绝访问），换到 `%LOCALAPPDATA%` 即可。

## 从源码编译

无需安装任何 SDK，Windows 自带编译器：

```bat
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ ^
    /out:MouseBatteryTray.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll MouseBatteryTray.cs
```

加 `/target:exe`（去掉 `win`）编译命令行版。

## 实现要点（踩坑记录）

- **HID 异步读**：`ReadFile` OVERLAPPED 在 x64 下 `hEvent` 位于偏移 **24**（不是 16）；OVERLAPPED 内存必须清零；先挂读、再 `HidD_SetOutputReport` 发命令、`WaitForSingleObject` 后必须 `CancelIoEx` + `GetOverlappedResult(wait=TRUE)` 冲刷 IRP 才能关句柄，否则 0xC0000409 崩溃。
- **写通道**：中断 OUT 的 `WriteFile` 被固件忽略，必须走控制端点 `HidD_SetOutputReport`。
- **报告布局**：用 `HidP_GetValueCaps` 从 preparsed data 拿（注意 x64 下 HIDP_VALUE_CAPS 各字段偏移与 32 位不同）。

## License

MIT
