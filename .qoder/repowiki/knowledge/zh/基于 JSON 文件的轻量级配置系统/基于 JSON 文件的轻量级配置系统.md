---
kind: configuration_system
name: 基于 JSON 文件的轻量级配置系统
category: configuration_system
scope:
    - '**'
source_files:
    - src/FufuLauncher/Services/ConfigService.cs
    - src/FufuLauncher/App.xaml.cs
    - Start/appmcGAME/config.json
---

该启动器采用极简的 JSON 文件持久化方案管理运行时配置，未引入外部配置框架（如 .NET 内置的 IConfiguration、YAML、TOML 或环境变量注入），所有配置集中存储于用户数据目录下的单个 config.json 文件中。

**加载与存储机制**
- 配置文件路径固定为 `<程序目录>/appmcGAME/config.json`，由 `ConfigService.ConfigPath` 属性通过 `Path.Combine(App.AppDataDir, "config.json")` 计算得出。
- 应用启动时，`App.OnStartup` 中通过 DI 容器获取 `ConfigService` 并调用 `Load()` 方法；`Load()` 使用 `System.Text.Json` 反序列化 JSON 到 `AppConfig` 对象，失败时回退到默认空配置。
- 保存操作通过 `Save()` 方法将内存中的 `AppConfig` 序列化为格式化 JSON（缩进 + 忽略 null 值）写回文件，异常被吞掉以避免崩溃。

**配置模型（AppConfig）**
`AppConfig` 类按功能分块定义所有可配置项：主题（Theme）、下载源（DownloadSource）、Java 路径与版本、JVM 参数（Xms/Xmx/ExtraJvmArgs）、游戏分辨率与全屏、背景与视频覆盖层（BaseImagePath/OverlayType/OverlayPath/OverlayOpacity）、视频播放控制（VideoMuted/VideoSpeed/Fps/Volume）、微软账号登录（MicrosoftClientId/AuthMode/AuthCallbackPort）、Java 镜像与自动扫描、JVM 多核优化与进程优先级、CPU 亲和性、智能内存分配（AutoMemoryMode/Reserve/Max/Min）等。

**向后兼容与迁移策略**
- 支持旧版 `BackgroundPath`/`BackgroundType`/`BackgroundOpacity` 字段自动迁移到新的双层背景结构（BaseImagePath + OverlayType/OverlayPath/OverlayOpacity）。
- 二次迁移逻辑：当 BaseImagePath 有值但 OverlayPath 为空且非 Video 模式时，自动复制并设置 OverlayType 为 Image，同时启用 BackgroundEnabled。
- 迁移失败不影响运行，仅记录日志。

**依赖注入集成**
- `ConfigService` 在 `App.ConfigureServices` 中以单例方式注册到 Microsoft.Extensions.DependencyInjection 容器。
- 其他服务（如 AuthService、GameLaunchService 等）通过构造函数注入 `ConfigService` 读取配置，形成统一的配置访问入口。

**约束与约定**
- 配置文件必须位于程序同级 appmcGAME 目录下，不支持命令行参数或环境变量覆盖。
- 所有配置项均有默认值，缺失字段不会导致解析失败。
- 保存失败静默处理，保证启动器稳定性优先。
- 敏感字段（如 DevPassword、MicrosoftClientId）以明文存储在本地 JSON 中，无加密保护。