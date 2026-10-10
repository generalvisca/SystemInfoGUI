# SystemInfoGUI

一款 Windows 桌面端的**系统信息查询工具**：以 WinForms 表格的形式，把分散在 WMI、注册表、COM 组件中的本机关键信息一次性采集并分组成表。

![平台](https://img.shields.io/badge/Platform-Windows_x64-0078D6?logo=windows)
![.NET](https://img.shields.io/badge/.NET-net10.0--windows-512BD4?logo=dotnet)
![界面](https://img.shields.io/badge/UI-Windows_Forms-0078D6)
![权限](https://img.shields.io/badge/Privilege-requireAdministrator-orange)
![许可证](https://img.shields.io/badge/License-MIT-yellow)

---

## 简介

面向终端资产盘点、安全合规核查、现场运维等场景——双击、提权、看结果，不写配置文件、不留后台服务。

程序启动即自动采集一次，结果按分组自上而下展示：

| 分组    | 采集内容                                |
| ----- | ----------------------------------- |
| 基础信息  | 本机序列号、本机用户名                         |
| 电源设置  | 使用电池 / 连接电源下的屏幕关闭与睡眠超时              |
| 防火墙状态 | 域网络、专用网络、公用网络三档开关状态                 |
| IP配置  | 每张网卡的 IPv4 / IPv6 地址、网关、DNS 地址      |
| 网络适配器 | 各网卡型号、物理网卡总数                        |
| 磁盘分区  | 各盘容量、介质类型（SSD / HDD）、BitLocker 加密状态 |

两个值得一提的设计：

- **单文件自包含发布**，约 111 MiB，拷贝到其他 Windows x64 机器可直接运行，目标机无需预装 .NET 运行时
- **BitLocker 严格区分「无权查询」与「未加密」**，权限不足时如实报「无权查询（需管理员权限）」，不会谎报未加密

---

## 安装

### 方式一：直接运行已发布程序

从 [Releases 页面](https://github.com/generalvisca/SystemInfoGUI/releases) 下载，三个包任选其一（区别见 Release 说明），右键「以管理员身份运行」即可。

### 方式二：从源码构建

需要 **Windows x64** 与 **.NET 10 SDK**（实测 10.0.401）：

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release
```

产物路径：`publish\SystemInfoGUI.exe`

> 项目已配置 `<PublishDir>publish\</PublishDir>`，产物直接落在仓库根目录的 `publish\` 下，而非默认的多层输出路径。

---

## 使用

1. 双击 `SystemInfoGUI.exe`
2. 在 UAC 弹窗中点**「是」**——程序声明了 `requireAdministrator`，拒绝提升权限则不会启动
3. 启动后自动采集一次并直接展示结果，可点「刷新信息」重新采集

注意事项：

- **BitLocker 真实状态必须在管理员权限下才能读到**。非提权运行时该字段显示「无权查询（需管理员权限）」；提权后才能看到「已加密 / 未加密」等真实值
- 提权后因 **UAC 令牌分离**，映射的网络驱动器通常不可见，属预期行为
- 台式机（无电池）的电源「使用电池」项可能显示「读取失败」，属预期降级，不是采集故障
- 非提权运行时每个需检测的盘符都会走一次 BitLocker 失败路径，**每次约 5 秒**，盘符越多等待越久；提权运行会快得多

---

## 许可证

本项目采用 **MIT 许可证**，完整条款见 [LICENSE](LICENSE) 文件。

Copyright (c) 2026 generalvisca
