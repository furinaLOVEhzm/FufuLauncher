---
kind: external_dependency
name: 华为云 OpenJDK 国内镜像
slug: huaweicloud-openjdk-mirror
category: external_dependency
category_hints:
    - client_constraint
scope:
    - '**'
---

国内优先的 Java 下载镜像源，仅支持 LTS 版本（8/11/17/21）。目录结构为 mirrors.huaweicloud.com/openjdk/{major}/，需要动态解析 HTML 页面获取最新版本文件名。目前仅支持 x64 架构。