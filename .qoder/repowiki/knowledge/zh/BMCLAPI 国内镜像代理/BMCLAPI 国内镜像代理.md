---
kind: external_dependency
name: BMCLAPI 国内镜像代理
slug: bmclapi
category: external_dependency
category_hints:
    - framework_behavior
scope:
    - '**'
---

Minecraft 资源国内镜像服务，自动将 Mojang 官方域名替换为 bmclapi2.bangbang93.com。支持的域名映射包括 piston-meta.mojang.com、launchermeta.mojang.com、piston-data.mojang.com、libraries.minecraft.net、resources.download.minecraft.net。当官方源失败时自动降级到此镜像。