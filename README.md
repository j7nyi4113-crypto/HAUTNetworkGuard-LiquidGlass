<div align="center">

# HAUT Network Guard · Liquid Glass

**河南工业大学校园网自动登录工具，液态玻璃界面**

Windows 桌面程序 · 单文件下载 · 无需安装运行时

![License](https://img.shields.io/badge/license-MIT-blue)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![Size](https://img.shields.io/badge/download-390%20KB-2ea44f)

**[⬇️ 下载](https://github.com/j7nyi4113-crypto/HAUTNetworkGuard-LiquidGlass/releases/latest)** · **[🔗 网页预览](https://j7nyi4113-crypto.github.io/HAUTNetworkGuard-LiquidGlass/)** · **[原项目](https://github.com/yellowpeachxgp/HAUTNetworkGuard)**

</div>

<p align="center">
  <img src="preview/app-01-status.webp" width="360" alt="主界面">
</p>

---

## 下载

| | 下载 | 说明 |
| :--- | :--- | :--- |
| **程序包** | [HAUTNetworkGuard-LiquidGlass-v1.4.0.zip](https://github.com/j7nyi4113-crypto/HAUTNetworkGuard-LiquidGlass/releases/download/v1.4.0/HAUTNetworkGuard-LiquidGlass-v1.4.0.zip)<br>`390 KB` | 解压后双击 `HAUTNetworkGuard.exe` 即可 |
| **界面包** | [HAUTNetworkGuard-UI-v1.4.0.zip](https://github.com/j7nyi4113-crypto/HAUTNetworkGuard-LiquidGlass/releases/download/v1.4.0/HAUTNetworkGuard-UI-v1.4.0.zip)<br>`20 KB` | 只有 `index.html`，浏览器里打开看效果 |

程序包基于系统自带的 .NET Framework 4.8，不需要安装 .NET、Node 或 Python。
唯一前置条件是 Microsoft Edge WebView2 Runtime —— Windows 11 和较新的 Windows 10 默认自带。

---

## 功能

- **自动登录**：对接校园网网关（SRUN3K），支持记住密码与开机自启
- **断线重连**：默认 30 秒检测一次，可调 30 – 300 秒，掉线自动恢复
- **实时读数**：IP 地址、已用流量、在线时长
- **系统托盘**：关闭窗口不退出，继续在后台保持连接
- **外观**：浅色 / 深色 / 跟随系统，窄窗口与宽窗口两套布局
- **凭据安全**：密码用 Windows DPAPI 加密，绑定当前用户

---

## 关于界面

界面采用 Apple Liquid Glass 风格：半透明、可折射、带流动感。核心是把一块玻璃拆成三层——

| 层 | 做法 |
| :--- | :--- |
| 本体 | 低不透明度着色 + `backdrop-filter` 模糊 + `feTurbulence` 磨砂颗粒 |
| 边缘 | 用 `mask-composite: exclude` 把全尺寸伪元素裁成 1.6px 环带，单独做一次高饱和提亮 |
| 高光 | 斜向渐变 + `blur` 柔化后横向扫过，各面板用延迟错开相位 |

边缘还叠了一层 `feDisplacementMap` 位移滤镜做真实折射。关键在于伪元素本身是全尺寸的、
只是被遮罩裁成环带，所以滤镜区域覆盖整个窗口，有完整背景可供采样。

<p align="center">
  <img src="preview/app-02-account.webp" width="32%" alt="账号">
  <img src="preview/app-03-settings.webp" width="32%" alt="设置">
  <img src="preview/05-dark.webp" width="32%" alt="深色模式">
</p>

---

## 技术栈

| | |
| :--- | :--- |
| 界面 | HTML + CSS + 原生 JS，无框架无构建 |
| 宿主 | C# / WinForms + WebView2（.NET Framework 4.8） |
| 通信 | `postMessage` 双向桥接 |
| 协议 | SRUN3K，移植自原项目 C++ 实现 |

`index.html` 只有一份：构建时复制进 `wwwroot` 由 WebView2 渲染；
直接在浏览器打开则退化为纯前端演示，不发起真实请求。

---

## 从源码构建

需要 [.NET SDK 8](https://dotnet.microsoft.com/download)（仅编译需要，运行不需要）。

```bash
git clone https://github.com/j7nyi4113-crypto/HAUTNetworkGuard-LiquidGlass.git
cd HAUTNetworkGuard-LiquidGlass
dotnet build app/LiquidGlass.csproj -c Release
```

产物在 `app/bin/Release/net48/`，整个目录拷走即可运行。

```
app/
├── MainForm.cs       # 无边框窗口、WebView2、托盘、轮询
├── SrunClient.cs     # SRUN3K 协议
├── AppConfig.cs      # 配置 + DPAPI
└── Program.cs        # 入口、单实例、--selftest
```

`HAUTNetworkGuard.exe --selftest` 可离屏加载界面并跑一遍协议校验，结果写到 `%TEMP%\haut-selftest.txt`。

---

## 说明

- 仅适用于**河南工业大学**校园网（网关 `172.16.154.130`）
- 未做代码签名，首次运行 SmartScreen 可能提示「未知发布者」，选「仍要运行」即可
- 配置存于 `%APPDATA%\HAUTNetworkGuard\`，程序不收集、不上传任何数据

---

## 致谢

本项目是 [yellowpeachxgp/HAUTNetworkGuard](https://github.com/yellowpeachxgp/HAUTNetworkGuard) 的界面重设计分支，
校园网协议移植自其 Windows 端实现。原项目作者 **YellowPeach**（QQ 群 `789860526`）。

设计语言参考 Apple Liquid Glass。

## 许可证

[MIT](LICENSE)。原始版权归 YellowPeach 所有。
