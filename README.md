# 无线键鼠电量

轻量 Windows 托盘工具，监控 **VXE R1SE+ 鼠标**和**珂芝 K75 键盘**的无线电量。

![鼠标 65%、键盘 100% 的图标示例](./icon-preview.png)

使用系统自带的 .NET Framework 和 Windows HID API，无需 Python，不安装硬件驱动。EXE 大小约 17 KB；开发机实测工作集约 32 MB，实际占用随系统环境变化。

## 使用

1. 下载仓库中的 [无线键鼠电量.exe](./无线键鼠电量.exe)，保存到固定目录并双击运行。
2. 托盘图标为一个圆环：左半圈红色表示鼠标，右半圈蓝色表示键盘；悬停查看百分比。
3. 右键勾选“开机启动”，即可在当前用户登录时自动运行。首次直接运行 EXE 不会自动开启自启。

需要 Windows 64 位及 .NET Framework 4.x，Windows 10/11 通常已自带该运行环境。

## 功能

- 每 30 分钟查询一次，双击图标或使用右键菜单立即刷新。
- 每个半圈从顶部向下按电量点亮，暗色轨道表示剩余未点亮部分；红蓝颜色固定，便于区分设备。
- 电量 ≤10% 提醒一次；电量恢复后重置提醒。睡眠恢复后自动刷新。
- 读取失败显示“未读取”，不将旧电量当作当前值。
- 单实例运行；退出后不再占用后台资源。
- 当前用户自启，无需管理员权限。

启用自启后请保留 EXE 的位置；移动后重新运行并开启自启以更新路径。卸载前取消自启，再退出并删除文件。

## 支持设备

| 设备 | VID:PID | 电量接口 |
| --- | --- | --- |
| VXE R1SE+ | `373B:1085` / `3554:F58F` | HID usage page `FF02` |
| 珂芝 K75 | `3554:FA09` | HID usage page `FF04`，Feature Report `06` |

鼠标依次尝试匹配接口，某个接收器无响应时继续尝试其他匹配设备。型号或固件不同可能需要调整协议；本项目不支持耳机电量。

## 从源码构建

在项目目录运行 PowerShell：

```powershell
.\build.ps1
```

脚本使用系统的 64 位 .NET Framework C# 编译器，生成 `无线键鼠电量.exe`。

诊断读取（不启动托盘）：

```powershell
Start-Process '.\无线键鼠电量.exe' -ArgumentList '--diagnose' -Wait
Get-Content '.\diagnostic.txt'
```

使用 `--install-startup` 参数可开启当前用户自启并启动程序。自启设置保存在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，名称为 `VxeKzziBattery`。
