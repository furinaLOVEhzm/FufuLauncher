# 芙芙启动器 · FufuLauncher

> 一个中文的、基于 **.NET 10 + WPF** 的 Minecraft Java 版启动器。

![version](https://img.shields.io/badge/version-1.9.8.6-blue)
![framework](https://img.shields.io/badge/.NET-10%20(win--x64)-512BD4)
![license](https://img.shields.io/badge/license-GPL--3.0-green)
![lang](https://img.shields.io/badge/UI-%E7%AE%80%E4%BD%93%E4%B8%AD%E6%96%87-lightgrey)

官网：**https://fufucraft.top** ｜ 交流 QQ 群：**1047380207**

---

## ⚠️ 先读这个：这是一个「部分开源」仓库

本仓库**有意不包含登录 / 微软账号 / 账号管理相关模块**（`AuthService`、`AccountService`、`AccountsDeck`）。

因此：

- **单独 clone 本仓库无法编译** —— `GameLaunchService` 等文件仍会引用被剔除的 `AuthService`，会产生编译错误。
- 这是**作者有意为之的发行方式**，不是仓库残缺或漏传。完整可构建源码不随本仓库公开。
- 剔除这些模块**不涉及任何安全泄露**：其中出现的 Azure `ClientId` 属于 OAuth **公共客户端标识**，按设计即为公开信息（HMCL 等开源启动器同样公开写在源码里），并非密钥。

如果你只是想了解架构、阅读实现、或在此基础上学习，本仓库完全够用；如果你需要一个能直接 `dotnet build` 的成品，请关注官网发布的安装包。

---

## 功能特性

- **秒开体验**：自包含 + ReadyToRun 预编译、松散部署（非单文件自解压），老机器也能快速启动。
- **全托管下载**：版本 / 资源 / 依赖多源下载，支持 BMCLAPI 等国内镜像加速，带完整性校验与断点续传。
- **多 Loader 支持**：Forge / NeoForge / Fabric / OptiFine 等安装与依赖链处理。
- **整合包导入**：支持导入第三方整合包，含模组元数据解析。
- **多主题界面**：命令式 C# 构建的新一代 `ShellWindow + Deck/Panel` 架构，内置多套风景主题。
- **稳健性**：单实例门禁（按部署根哈希区分）、全局异常三件套、崩溃中文友好提示 + 技术日志分离。
- **AI 能力**：内置本地大模型辅助（随安装包分发，本仓库不含相关闭源部分）。
- **原生加速**：SHA1/SHA256、ZIP 解压/打包等由 C++ 原生 DLL 承担。

界面语言目前仅提供**简体中文**。

---

## 技术栈

| 项 | 说明 |
|---|---|
| 框架 | .NET 10，`net10.0-windows`，win-x64 独占 |
| UI | WPF（绝大多数界面用 C# 命令式构建，仅 `App.xaml` / `Themes/Theme.xaml` 为手写 XAML） |
| 原生 | C++（哈希 / 压缩 / 磁盘探测） |
| 部署 | SelfContained + ReadyToRun，不裁剪，松散部署 |

## 目录结构（本仓库内）

```
FufuLauncher/
├── FufuLauncher.sln
├── nuget.config              # 指向华为云 NuGet 镜像（本机网络对官方源有证书替换问题）
└── src/FufuLauncher/
    ├── App.xaml(.cs)         # 入口与生命周期
    ├── Assets/               # 图标 / 主题图 / loader 图
    ├── Themes/Theme.xaml     # 主题资源字典
    ├── Services/             # 业务服务层（下载 / 启动 / 网络 / 主题 / 模组包 …）
    └── Next/                 # 新一代 UI（Foundation + UI）
```

> 注：安装程序、官网、宣传片、随附工具链、游戏运行数据、以及登录/账号模块均不在本仓库内。

---

## 版权与许可

Copyright © 2026 可爱的芙芙（furinaLOVEhzm）. 保留所有权利（登录/账号等闭源模块）。

本仓库公开的源码部分以 **GNU General Public License v3.0** 授权，见 [LICENSE](./LICENSE)。

## 免责声明

- 本项目为第三方 Minecraft 启动器，与 Mojang / Microsoft 官方无任何隶属或背书关系。
- Minecraft 是 Mojang Studios 的商标。请通过正规渠道购买游戏并使用。
