# 追觅洗地机 AI-SOP 监测系统

面向洗地机装配工位的人工标准化操作监测系统（Windows / .NET 8 / WPF）。

## 这套系统做什么

| 能力 | 说明 |
|---|---|
| **动作判定** | 在画面上框出抓取位置，按框教 OK / NG 样本，之后自动判"东西在不在位" |
| **过程监测** | 手部动作合规（跳步 / 错序 / 漏做 / 超时），NG 实时锁线、留图、写 PLC |
| **动作计时** | 每道工序的开始/结束时刻与用时，实时显示"比上次快了还是慢了"，落盘可回看 |
| **证据追溯** | 每一步留一张判定当时的抓拍；NG 保持图点开就能看 |
| **多工位联网** | 内置汇总终端看板（浏览器打开即看全场），工位定时上报 |
| **一键下发** | 终端选一份模板 → 一键下发 → 所有工位 15 秒内自动切换 |
| **MES 对接** | 固定 JSON 契约 + 可替换客户端 + 断网续传队列（详见《MES对接说明.md》） |

## 目录结构

```
VisionForge-部署包-v1.0.0/
├─ 0-一键部署.cmd / 1-安装.NET8桌面运行时.cmd / 2-环境自检.cmd
├─ 3-启动VisionForge.cmd / 4-部署到D盘.cmd / 5-修复并重启.cmd
├─ 上传到GitHub.cmd            ← 一键上传到 GitHub（自动递增版本号）
├─ 部署说明.md                 ← 现场怎么部署、每个功能怎么用（含历次改动记录）
├─ MES对接说明.md              ← 给 MES 供应商看的接口契约
├─ scripts/                    ← 部署、自检、重建、上传脚本
├─ src/src/                    ← 源码（5 个工程）
│   ├─ VisionForge.Core        模型 / 接口 / 规则引擎（不依赖 WPF）
│   ├─ VisionForge.Hardware    相机、PLC、报警、视觉算法（硬件抽象层实现）
│   ├─ VisionForge.Infrastructure 存储、配置、日志、联网（汇总终端 / MES）
│   ├─ VisionForge.Common      MVVM 基础设施
│   └─ VisionForge.Main        WPF 界面 + 自检
└─ app/                        ← 已编译好的可运行版本（部署到 D:\VisionForge\app）
```

## 怎么跑起来

1. 双击 `1-安装.NET8桌面运行时.cmd`（只需一次）
2. 双击 `5-修复并重启.cmd`：从源码编译 → 自检 → 部署到 `D:\VisionForge\app` → 启动
3. 以后日常使用双击桌面「追觅洗地机AI-SOP监测系统」

每次重建都会先跑一遍自检（判定逻辑、看板、MES 契约、断网续传、连续试跑等 60+ 项），
报告在 `D:\VisionForge\app\data\selftest\report.txt`，日志在 `D:\VisionForge\app\data\logs\`。

## 改完代码怎么上传

双击 `上传到GitHub.cmd` 即可：它会自动把版本号 +1（V1.0 → V1.1 → V1.2 …），
提交并推送到 `https://github.com/qian1586/-AI-SOP`。

也可以在命令行里带上备注：

```
scripts\upload-to-github.ps1 -Note "修好了画框和相似度"
```

首次使用需要先登录一次 GitHub（本机已装 GitHub CLI）：

```
gh auth login
```

## 版本

版本号写在本文件同级目录的 `.version` 文件里，由上传脚本自动递增。
