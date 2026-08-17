# 可爱的芙芙 — Minecraft Java版启动器

> 署名:可爱的芙芙  
> 版本:1.0.0.0  
> Copyright © 可爱的芙芙

一个使用 C# WPF (.NET 8) + C++ 原生 DLL 构建的 Minecraft Java 版启动器,对标 PCL2 全部能力。

---

## 编译环境

| 项目 | 要求 |
|------|------|
| IDE | Visual Studio 2022 (17.8+) |
| .NET | .NET 8 Desktop Runtime + SDK |
| C++ | C++ 桌面开发组件 (v143 工具集) |
| Windows SDK | 10.0 (19041+) |
| 操作系统 | Windows 10 64位 / Windows 11 |

### 安装 VS2022 组件

在 Visual Studio Installer 中勾选:
- ✅ **.NET 桌面开发** (包含 .NET 8 SDK、WPF 工作负载)
- ✅ **使用 C++ 的桌面开发** (包含 v143 生成工具、Windows 10 SDK)

### 构建步骤

1. 打开 `FufuLauncher.sln`
2. 配置管理器选择 `Release | x64`
3. 右键解决方案 → **重新生成解决方案**
4. 输出: `src\FufuLauncher\bin\Release\net8.0-windows\FufuLauncher.exe`

### 架构说明

```
FufuLauncher/
├── FufuLauncher.sln                # VS2022 解决方案
├── src\FufuLauncher\               # C# WPF 主程序 (.NET 8)
│   ├── App.xaml / App.xaml.cs       # 应用入口
│   ├── MainWindow.xaml              # 主窗口
│   ├── Models\                      # 数据模型
│   ├── Services\                    # 业务服务
│   ├── ViewModels\                  # MVVM 视图模型
│   ├── Views\                       # 页面视图
│   └── Themes\                      # 主题资源
├── native\FufuNative\               # C++ 原生 DLL (哈希、ZIP解压)
│   ├── FufuNative.h/.cpp            # DLL 导出入口
│   ├── HashUtil.h/.cpp              # SHA1/SHA256 文件哈希
│   └── ZipUtil.h/.cpp               # ZIP 解压
└── scripts\
    └── prepare_assets.py            # 资源预处理辅助脚本
```

## ⚠️ Windows 智能应用控制拦截说明

编译生成的 `FufuLauncher.exe` 在 Windows 10/11 上运行时,**会被 Windows 智能应用控制 (Smart App Control) / SmartScreen 拦截**,
因为 exe 未经过数字签名(本项目按需求不包含任何数字签名代码)。

### 解除拦截方法

打开 **PowerShell**(管理员),执行:

```powershell
Unblock-File -Path "C:\路径\FufuLauncher.exe"
```

或在文件属性中操作:
1. 右键 `FufuLauncher.exe` → **属性**
2. 勾选底部的 **解除锁定 (Unblock)**
3. 点击 **确定**

解除后即可正常运行。

---

## 运行时依赖

目标机器需安装 **.NET 8 Desktop Runtime (x64)**:  
下载地址: https://dotnet.microsoft.com/download/dotnet/8.0

若未安装,启动器启动环境自检模块会弹窗提示下载地址。

---

Copyright © 可爱的芙芙
