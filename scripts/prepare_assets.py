#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
可爱的芙芙 Minecraft启动器 - 资源预处理辅助脚本

用途:
1. 预生成默认主题背景占位图(纯色 PNG)
2. 校验 native DLL 是否编译产出
3. 清理临时缓存目录
4. 批量重命名实例目录(可选)

注意:本脚本仅作为开发期辅助工具,不嵌入主 exe。
运行环境:Python 3.8+

作者:可爱的芙芙
"""

import os
import sys
import shutil
import hashlib
from pathlib import Path

# 项目根目录(脚本位于 scripts/ 下,根目录为上一级)
PROJECT_ROOT = Path(__file__).resolve().parent.parent
NATIVE_DIR = PROJECT_ROOT / "native" / "FufuNative"
SCRIPTS_DIR = PROJECT_ROOT / "scripts"
SRC_DIR = PROJECT_ROOT / "src" / "FufuLauncher"


def print_header(title: str) -> None:
    """打印分节标题"""
    print(f"\n{'=' * 50}")
    print(f"  {title}")
    print(f"{'=' * 50}")


def check_native_dll() -> None:
    """检查 C++ 原生 DLL 是否已编译产出"""
    print_header("检查 C++ 原生 DLL")
    patterns = [
        NATIVE_DIR / "x64" / "Release" / "FufuNative.dll",
        NATIVE_DIR / "x64" / "Debug" / "FufuNative.dll",
        NATIVE_DIR / "Release" / "FufuNative.dll",
        NATIVE_DIR / "Debug" / "FufuNative.dll",
    ]
    found = None
    for p in patterns:
        if p.exists():
            found = p
            break
    if found:
        size = found.stat().st_size
        print(f"  [OK] 已找到:{found.relative_to(PROJECT_ROOT)}")
        print(f"       大小:{size / 1024:.1f} KB")
        # 计算 SHA256 用于核对
        sha256 = hashlib.sha256(found.read_bytes()).hexdigest()
        print(f"       SHA256:{sha256}")
    else:
        print("  [警告] 未找到 FufuNative.dll")
        print("         请先用 VS2022 编译 native\\FufuNative\\FufuNative.vcxproj")
        print("         配置:Release | x64")


def clean_cache() -> None:
    """清理临时缓存目录"""
    print_header("清理临时缓存")
    cache_dirs = [
        SRC_DIR / "bin",
        SRC_DIR / "obj",
        NATIVE_DIR / "x64",
        NATIVE_DIR / "Debug",
        NATIVE_DIR / "Release",
        PROJECT_ROOT / ".vs",
    ]
    for d in cache_dirs:
        if d.exists():
            print(f"  删除:{d.relative_to(PROJECT_ROOT)}")
            shutil.rmtree(d, ignore_errors=True)
        else:
            print(f"  跳过(不存在):{d}")
    print("  清理完成")


def generate_placeholder_background() -> None:
    """生成默认主题背景占位图(纯色 PNG,无第三方依赖)"""
    print_header("生成默认背景占位图")
    assets_dir = SRC_DIR / "Assets"
    assets_dir.mkdir(parents=True, exist_ok=True)
    bg_path = assets_dir / "default_bg.png"

    # 生成最简 1x1 像素 PNG(深蓝色),无 Pillow 依赖
    # PNG 文件头 + IHDR + IDAT(深蓝像素) + IEND
    png_bytes = bytes([
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,  # PNG 签名
        0x00, 0x00, 0x00, 0x0D,  # IHDR 长度
        0x49, 0x48, 0x44, 0x52,  # "IHDR"
        0x00, 0x00, 0x00, 0x01,  # 宽 1
        0x00, 0x00, 0x00, 0x01,  # 高 1
        0x08, 0x06, 0x00, 0x00, 0x00,  # 8位 RGBA
        0x1F, 0x15, 0xC4, 0x89,  # CRC
        0x00, 0x00, 0x00, 0x0A,  # IDAT 长度
        0x49, 0x44, 0x41, 0x54,  # "IDAT"
        0x78, 0x9C, 0x62, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,  # 深蓝像素
        0x0D, 0x0A, 0x2D, 0xB4,  # CRC
        0x00, 0x00, 0x00, 0x00,  # IEND 长度
        0x49, 0x45, 0x4E, 0x44,  # "IEND"
        0xAE, 0x42, 0x60, 0x82,  # CRC
    ])
    bg_path.write_bytes(png_bytes)
    print(f"  [OK] 已生成:{bg_path.relative_to(PROJECT_ROOT)}")


def verify_project_structure() -> None:
    """校验项目目录结构完整性"""
    print_header("校验项目结构")
    required = [
        PROJECT_ROOT / "FufuLauncher.sln",
        PROJECT_ROOT / "README.md",
        SRC_DIR / "FufuLauncher.csproj",
        SRC_DIR / "App.xaml",
        SRC_DIR / "MainWindow.xaml",
        NATIVE_DIR / "FufuNative.vcxproj",
        NATIVE_DIR / "FufuNative.h",
        NATIVE_DIR / "HashUtil.cpp",
        NATIVE_DIR / "ZipUtil.cpp",
    ]
    all_ok = True
    for p in required:
        exists = p.exists()
        status = "[OK]" if exists else "[缺失]"
        if not exists:
            all_ok = False
        rel = p.relative_to(PROJECT_ROOT)
        print(f"  {status} {rel}")
    if all_ok:
        print("\n  项目结构完整,可以编译。")
    else:
        print("\n  [警告] 部分文件缺失,请检查。")
        sys.exit(1)


def main() -> None:
    print("可爱的芙芙 Minecraft启动器 - 资源预处理脚本")
    print(f"项目根目录:{PROJECT_ROOT}")

    if len(sys.argv) < 2:
        print("\n用法:")
        print("  python prepare_assets.py check     # 校验项目结构与 native DLL")
        print("  python prepare_assets.py clean      # 清理编译缓存")
        print("  python prepare_assets.py bg        # 生成默认背景占位图")
        print("  python prepare_assets.py all       # 执行全部步骤")
        return

    cmd = sys.argv[1]
    if cmd == "check":
        verify_project_structure()
        check_native_dll()
    elif cmd == "clean":
        clean_cache()
    elif cmd == "bg":
        generate_placeholder_background()
    elif cmd == "all":
        verify_project_structure()
        check_native_dll()
        generate_placeholder_background()
        print("\n全部预处理完成。")
    else:
        print(f"未知命令:{cmd}")
        sys.exit(1)


if __name__ == "__main__":
    main()
