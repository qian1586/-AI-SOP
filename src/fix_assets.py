"""在 NuGet 用户配置不可用的机器上，手工生成 project.assets.json。

背景：
    本机 dotnet 构建时报 NuGet.Configuration.ConfigurationDefaults 静态构造失败
    （Value cannot be null. Parameter 'path1'），根因是 NuGet 读取 Windows Known Folder
    （RoamingAppData）时拿到 null，而这是 .NET 内部走 SHGetKnownFolderPath 的结果，
    设置 APPDATA 环境变量无效。

    本项目刻意做到零 NuGet 依赖（不引任何第三方包），
    因此 project.assets.json 的内容本来就只有框架引用 + 项目引用，可以安全地手工生成。

用法：
    python fix_assets.py
    dotnet build --no-restore

注意：这只是在受限环境下的应急手段。
    换到正常机器上，直接 dotnet restore 即可，不需要本脚本。
"""

from __future__ import annotations

import glob
import json
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.abspath(__file__))
SDK_DIR = os.path.join(
    os.environ.get("ProgramFiles", r"C:\Program Files"), "dotnet", "sdk"
)

# 常见目标框架的隐式 using（.NET SDK 会为不同 TFM 注入不同命名空间）
IMPLICIT_USINGS = [
    "System", "System.Collections.Generic", "System.IO", "System.Linq",
    "System.Net.Http", "System.Threading", "System.Threading.Tasks",
]


def find_sdk_version() -> str:
    if not os.path.isdir(SDK_DIR):
        return ""
    versions = [
        d for d in os.listdir(SDK_DIR)
        if os.path.isdir(os.path.join(SDK_DIR, d)) and d[0].isdigit()
    ]
    versions.sort(key=lambda v: [int(x) for x in re.findall(r"\d+", v)], reverse=True)
    return versions[0] if versions else ""


def read_csproj(path: str) -> str:
    with open(path, "r", encoding="utf-8") as f:
        return f.read()


def get_target_framework(content: str) -> str:
    m = re.search(r"<TargetFramework>([^<]+)</TargetFramework>", content)
    if m:
        return m.group(1).strip()
    m = re.search(r"<TargetFrameworks>([^<]+)</TargetFrameworks>", content)
    if m:
        return m.group(1).split(";")[0].strip()
    return "net8.0"


def get_project_references(csproj_path: str, content: str) -> dict[str, str]:
    """返回 {项目名: 绝对路径}。"""
    refs: dict[str, str] = {}
    for m in re.finditer(r'<ProjectReference\s+Include="([^"]+)"', content):
        rel = m.group(1).replace("\\", os.sep)
        abs_path = os.path.normpath(os.path.join(os.path.dirname(csproj_path), rel))
        refs[os.path.splitext(os.path.basename(abs_path))[0]] = abs_path
    return refs


def build_assets(csproj: str, content: str, sdk_version: str) -> dict:
    name = os.path.splitext(os.path.basename(csproj))[0]
    tfm = get_target_framework(content)
    proj_refs = get_project_references(csproj, content)
    obj_dir = os.path.join(os.path.dirname(csproj), "obj")
    os.makedirs(obj_dir, exist_ok=True)

    framework_refs = {"Microsoft.NETCore.App": {"privateAssets": "all"}}
    if "windows" in tfm.lower():
        # WPF 项目需要额外的框架引用
        framework_refs["Microsoft.WindowsDesktop.App.WPF"] = {"privateAssets": "all"}

    runtime_graph = os.path.join(
        SDK_DIR, sdk_version, "PortableRuntimeIdentifierGraph.json"
    ) if sdk_version else ""

    return {
        "version": 3,
        "targets": {tfm: {}},
        "libraries": {},
        "projectFileDependencyGroups": {tfm: []},
        "packageFolders": {os.path.join(ROOT, ".nuget", "packages") + os.sep: {}},
        "project": {
            "version": "1.0.0",
            "restore": {
                "projectUniqueName": csproj,
                "projectName": name,
                "projectPath": csproj,
                "packagesPath": os.path.join(ROOT, ".nuget", "packages") + os.sep,
                "outputPath": obj_dir + os.sep,
                "projectStyle": "PackageReference",
                "configFilePaths": [os.path.join(ROOT, "nuget.config")],
                "originalTargetFrameworks": [tfm],
                "sources": {},
                "frameworks": {
                    tfm: {
                        "targetAlias": tfm,
                        "projectReferences": {
                            rn: {"projectPath": rp} for rn, rp in proj_refs.items()
                        },
                    }
                },
            },
            "frameworks": {
                tfm: {
                    "targetAlias": tfm,
                    "dependencies": {},
                    "imports": [
                        "net461", "net462", "net47", "net471",
                        "net472", "net48", "net481",
                    ],
                    "assetTargetFallback": True,
                    "warn": True,
                    "frameworkReferences": framework_refs,
                    "runtimeIdentifierGraphPath": runtime_graph,
                }
            },
        },
    }


def main() -> None:
    sdk_version = find_sdk_version()
    if not sdk_version:
        print(f"[警告] 未在 {SDK_DIR} 找到 SDK 版本目录，runtimeIdentifierGraphPath 将为空")

    csprojs = sorted(glob.glob(os.path.join(ROOT, "src", "*", "*.csproj")))
    if not csprojs:
        print("未找到任何 csproj，检查目录结构")
        sys.exit(1)

    pkg_root = os.path.join(ROOT, ".nuget", "packages") + os.sep
    os.makedirs(pkg_root, exist_ok=True)

    for csproj in csprojs:
        content = read_csproj(csproj)
        name = os.path.splitext(os.path.basename(csproj))[0]
        obj_dir = os.path.join(os.path.dirname(csproj), "obj")
        os.makedirs(obj_dir, exist_ok=True)

        # 1) project.assets.json
        assets = build_assets(csproj, content, sdk_version)
        with open(os.path.join(obj_dir, "project.assets.json"), "w", encoding="utf-8") as f:
            json.dump(assets, f, indent=2, ensure_ascii=False)

        # 2) nuget.g.props —— MSBuild 靠它拿到 NuGetPackageRoot 等属性。
        #    这个文件缺失时，后续 Path.Combine(null, ...) 就是崩溃的直接来源。
        g_props = (
            '<?xml version="1.0" encoding="utf-8" standalone="no"?>\n'
            '<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">\n'
            '  <PropertyGroup Condition=" \'$(ExcludeRestorePackageImports)\' != \'true\' ">\n'
            '    <RestoreSuccess Condition=" \'$(RestoreSuccess)\' == \'\' ">True</RestoreSuccess>\n'
            '    <RestoreTool Condition=" \'$(RestoreTool)\' == \'\' ">NuGet</RestoreTool>\n'
            '    <ProjectAssetsFile Condition=" \'$(ProjectAssetsFile)\' == \'\' ">'
            '$(MSBuildThisFileDirectory)project.assets.json</ProjectAssetsFile>\n'
            f'    <NuGetPackageRoot Condition=" \'$(NuGetPackageRoot)\' == \'\' ">{pkg_root}</NuGetPackageRoot>\n'
            f'    <NuGetPackageFolders Condition=" \'$(NuGetPackageFolders)\' == \'\' ">{pkg_root}</NuGetPackageFolders>\n'
            '    <NuGetProjectStyle Condition=" \'$(NuGetProjectStyle)\' == \'\' ">PackageReference</NuGetProjectStyle>\n'
            '    <NuGetToolVersion Condition=" \'$(NuGetToolVersion)\' == \'\' ">6.11.0</NuGetToolVersion>\n'
            '  </PropertyGroup>\n'
            '</Project>\n'
        )
        with open(os.path.join(obj_dir, f"{name}.csproj.nuget.g.props"), "w", encoding="utf-8") as f:
            f.write(g_props)

        # 3) nuget.g.targets —— 通常为空模板，但 MSBuild 会尝试 import，缺了会有警告
        g_targets = (
            '<?xml version="1.0" encoding="utf-8" standalone="no"?>\n'
            '<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">\n'
            '</Project>\n'
        )
        with open(os.path.join(obj_dir, f"{name}.csproj.nuget.g.targets"), "w", encoding="utf-8") as f:
            f.write(g_targets)

        # 4) dgspec —— WPF 的 WinFX.targets 会尝试复制它，缺失时报 MSB3030
        tfm = get_target_framework(content)
        proj_refs = get_project_references(csproj, content)
        framework_refs = {"Microsoft.NETCore.App": {"privateAssets": "all"}}
        if "windows" in tfm.lower():
            framework_refs["Microsoft.WindowsDesktop.App.WPF"] = {"privateAssets": "all"}

        dgspec = {
            "format": 1,
            "restore": {csproj: {}},
            "projects": {
                csproj: {
                    "version": "1.0.0",
                    "restore": {
                        "projectUniqueName": csproj,
                        "projectName": name,
                        "projectPath": csproj,
                        "packagesPath": pkg_root,
                        "outputPath": obj_dir + os.sep,
                        "projectStyle": "PackageReference",
                        "configFilePaths": [os.path.join(ROOT, "nuget.config")],
                        "originalTargetFrameworks": [tfm],
                        "sources": {},
                        "frameworks": {
                            tfm: {
                                "targetAlias": tfm,
                                "projectReferences": {
                                    rn: {"projectPath": rp} for rn, rp in proj_refs.items()
                                },
                            }
                        },
                    },
                    "frameworks": {
                        tfm: {
                            "targetAlias": tfm,
                            "dependencies": {},
                            "imports": [
                                "net461", "net462", "net47", "net471",
                                "net472", "net48", "net481",
                            ],
                            "assetTargetFallback": True,
                            "warn": True,
                            "frameworkReferences": framework_refs,
                            "runtimeIdentifierGraphPath": os.path.join(
                                SDK_DIR, sdk_version, "PortableRuntimeIdentifierGraph.json"
                            ) if sdk_version else "",
                        }
                    },
                }
            },
        }
        with open(os.path.join(obj_dir, f"{name}.csproj.nuget.dgspec.json"), "w", encoding="utf-8") as f:
            json.dump(dgspec, f, indent=2, ensure_ascii=False)

        print(f"  ✓ {os.path.basename(csproj):<32} -> {tfm}")

    print(f"\n已生成 {len(csprojs)} 个 project.assets.json")
    print("接着执行： dotnet build --no-restore")


if __name__ == "__main__":
    main()
