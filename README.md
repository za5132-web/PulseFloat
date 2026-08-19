# PulseFloat

> 中文 | [English](#english)

PulseFloat 是一款极致轻量的 Windows 性能监控浮窗。它以单个 EXE 运行，无需安装、无需联网、没有后台服务，可实时显示 CPU、内存、进程数以及网络上下行速度。

## 主要功能

- CPU 与内存使用率实时监控
- 实时上传、下载速度与进程数
- 左右屏幕边缘自动吸附和收纳动画
- 透明度、刷新频率、点击穿透、开机启动设置
- 单实例运行；设置仅保存在当前用户注册表
- 字体和绘图资源复用，本机复测工作集约 3.8 MB

## 使用

直接运行 `PulseFloat.exe`。右键浮窗或托盘图标打开设置；双击浮窗切换点击穿透，穿透后按 `Ctrl + Alt + M` 恢复操作。

支持 Windows 10/11，依赖系统自带的 .NET Framework 4.x。程序尚未进行代码签名，因此 Windows 首次运行时可能显示未知发布者提示。

## 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## 许可

当前未授予开源许可。代码仅供查看；如需使用、修改或再发布，请先联系作者取得授权。

---

## English

PulseFloat is an ultra-lightweight floating performance monitor for Windows. It runs as a single executable with no installer, network access, or background service, and displays CPU usage, memory usage, process count, and live network throughput.

### Highlights

- Live CPU and memory monitoring
- Upload/download speed and process count
- Automatic docking and animated collapse on either screen edge
- Configurable opacity, refresh rate, click-through mode, and startup behavior
- Single-instance operation; settings stored only in the current user's registry
- Reused font and drawing resources; about 3.8 MB working set in a local test

### Usage

Run `PulseFloat.exe`. Right-click the widget or tray icon for settings. Double-click the widget to toggle click-through mode; press `Ctrl + Alt + M` to regain control.

Windows 10/11 and .NET Framework 4.x are required. The executable is currently unsigned, so Windows may show an unknown-publisher warning on first launch.

### Build from source

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

### License

No open-source license is currently granted. The source is available for viewing only; contact the author before using, modifying, or redistributing it.
