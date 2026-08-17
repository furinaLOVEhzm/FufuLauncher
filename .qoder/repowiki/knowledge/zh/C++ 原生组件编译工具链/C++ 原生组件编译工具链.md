---
kind: external_dependency
name: C++ 原生组件编译工具链
slug: cpp-v143-toolset
category: external_dependency
category_hints:
    - vendor_identity
scope:
    - '**'
---

项目包含 C++ 原生 DLL（FufuNative.dll），用于文件哈希计算和 ZIP 解压。需要使用 Visual Studio 2022 的 C++ 桌面开发组件（v143 工具集）编译。原生 DLL 通过 MSBuild 条件编译复制到输出目录的 runtimes\win-x64\native\ 路径。