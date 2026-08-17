---
kind: external_dependency
name: Adoptium Temurin Java 下载源
slug: adoptium-api
category: external_dependency
category_hints:
    - sdk_real_api
scope:
    - '**'
---

官方 Java 下载源，使用 Adoptium API 获取可用版本列表和二进制包。API 端点：/v3/info/available_releases 获取支持版本，/v3/binary/latest/{major}/ga/windows/{arch}/jre/hotspot/normal/eclipse 下载 JRE。失败时回退到硬编码版本列表。