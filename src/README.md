# VisionForge · 工业零件视觉检测上位机

> WPF + MVVM 上位机框架，硬件抽象 + 插件化算法 + 动态参数面板 + 历史追溯闭环。
> 适配 HC-500-10GM（GigE 黑白全局快门）等工业相机，可对接 Halcon 算法。

**状态：完整可编译，0 错误 0 警告，零 NuGet 第三方依赖（离线可构建）。**

---

## 一、快速开始

### 构建

```bash
cd vision-forge
dotnet build --no-restore
```

> ⚠️ **本机有个特殊环境问题**：宿主进程启动的子进程**缺失 `APPDATA` 环境变量**，
> 会导致 `dotnet` 命令崩溃在 `NuGet.Configuration.ConfigurationDefaults`，
> 报错形如 `NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`。
>
> 解决：**在同一条命令里**先补上它再跑：
>
> ```bash
> export APPDATA='C:\Users\qiantao\AppData\Roaming' && dotnet build
> ```
>
> 注意两点：① shell 状态不跨命令保留，必须写在同一行；
> ② 路径**必须用单引号**，双引号加反斜杠会被 Git Bash 转成 `C://Users//...` 畸形路径。
>
> 如果 `dotnet restore` 同样崩溃，先跑 `python fix_assets.py` 手工生成资产文件，
> 再用 `dotnet build --no-restore`。换到正常机器上不需要这些，直接 `dotnet build` 即可。

### 运行

```bash
cd src/VisionForge.Main
dotnet run
```

**不接任何硬件也能跑起来**：默认配置里自带一台模拟相机 + 模拟 PLC，
启动后点「连接相机」就能看到实时画面，点「执行检测」就能走完整个判定流程
（含状态机流转、NG 图落盘、历史写入）。

---

## 二、架构

```
VisionForge.Main            WPF 界面 + ViewModel（组合根在这里）
      ↓
VisionForge.Hardware        硬件实现：相机 / PLC / 视觉算法
VisionForge.Infrastructure  配置 / 日志 / 存储
      ↓
VisionForge.Core            接口 + 模型 + 业务服务（不依赖任何具体实现）
      ↓
VisionForge.Common          MVVM 基础类 / 事件聚合器
```

**依赖方向严格单向**，Core 层不知道 WPF、不知道海康 SDK、不知道 Halcon 的存在。

| 项目 | 职责 | 关键内容 |
|---|---|---|
| `Common` | 基础设施 | `ObservableObject` `RelayCommand` `AsyncRelayCommand` `EventAggregator` |
| `Core` | 契约与业务规则 | `ICamera` `IPlcClient` `IInspectionAlgorithm` 等接口；`Recipe` `SopDefinition` 等模型；`SopProcess` 状态机 |
| `Hardware` | 硬件实现 | `MockCamera` `HikCamera` `ModbusTcpPlcClient` `MockPlcClient`；算法插件 `ThresholdBlob` `DimensionMeasure` `Halcon` |
| `Infrastructure` | 外围能力 | `AppSettings` `FileLogger` `JsonRecipeRepository` `JsonLineHistoryStore` `BmpEncoder` |
| `Main` | 界面 | `MainViewModel` `ParameterFieldViewModel` `ParameterTemplateSelector` |

---

## 三、五大设计特点

### 1. MVVM 分层
View 层（XAML）只有声明式绑定，没有任何业务逻辑。换主题、调布局、改控件样式都不碰业务代码。

### 2. 插件化检测方案
```csharp
public interface IInspectionAlgorithm
{
    string Key { get; }
    IReadOnlyList<ParameterDefinition> Parameters { get; }
    InspectionResult Run(CameraFrame frame, Recipe recipe);
}
```
新增一种检测方案 = 写一个实现类 + 在 `AlgorithmRegistry.CreateDefault()` 里注册一行。**主程序不用改。**

### 3. 动态参数面板
算法自己声明需要哪些参数（名称/类型/范围/默认值/说明），界面据此**自动生成控件**：

| 参数类型 | 自动生成的控件 |
|---|---|
| `Number` | 输入框 + 范围提示 + 越界红字 |
| `Bool` | 复选框 |
| `Enum` | 下拉框 |
| `Text` | 输入框 + 说明文字 |

全程**没有 `switch(参数名)` 的硬编码**。换算法 → 参数面板自动变。

### 4. 历史数据闭环
- 每次检测写入 `data/history/yyyy-MM-dd.jsonl`（append-only，不锁表、不阻塞产线）
- NG 图用自研 `BmpEncoder` 落盘，按日期分目录，带保留天数清理策略
- 支持多条件筛选 + **导出 CSV（强制写 BOM，防 Excel 中文乱码）**
- 按产品型号/配方/批次/日期/操作员统计合格率

### 5. 硬件抽象层
换相机（海康 → 大恒 → Basler）或换 PLC（Modbus → 欧姆龙 FINS）**只需新增一个实现类**，
上层业务代码一行不动。`MockCamera` / `MockPlcClient` 的存在让算法开发完全不依赖硬件。

---

## 四、SOP 状态机（核心业务价值）

`Core/Services/SopProcess.cs`

```
Pending → Active → Passed
                 ↘ Failed（停留在原步骤，不前进）
```

**三条设计原则（都是现场逼出来的）：**

1. **NG 后停留在原步骤**，不上自动前进。员工修正后重新触发复检，通过了才走下一步。
2. **是否锁线由配方决定**（`BlockNextOnFail`）。安规/关键工序锁死；辅助工序只告警不锁线——
   全锁会让产线动不动就停，反而被绕过。
3. **必须留带权限的人工放行口子**。视觉会误判，产线不能因为一次误判停到下班。
   但每次放行都计数，「人工干预率」是评估算法是否合格的核心指标。

---

## 五、Halcon 对接

见 **`HALCON对接说明.md`**（含你原版代码的 8 个问题分析与修正）。

三个 Halcon 文件的分工：

| 文件 | 用途 | 能否给 C# 调 |
|---|---|---|
| `halcon/train_templates.hdev` | 训练模板，跑一次生成 `.shm` | — |
| `halcon/sop_standalone.hdev` | **独立运行版**：自带相机 + 循环，现场调试用 | ❌ 有死循环会卡死 |
| `halcon/sop_inspect.hdvp` | **引擎调用版**：无相机、无循环，一帧进结果出 | ✅ |

启用 HALCON 编译符号后，`HalconInspectionAlgorithm` 就会真正调用 Halcon；
不启用时走降级分支，保证任何人 clone 下来都能编译。

---

## 六、目录结构

```
vision-forge/
├── nuget.config                    零依赖配置（离线可构建）
├── fix_assets.py                   APPDATA 缺失时的应急脚本
├── HALCON对接说明.md                Halcon 问题清单与对接方式
├── halcon/
│   ├── train_templates.hdev        模板训练
│   ├── sop_standalone.hdev         独立运行版（现场调试）
│   └── sop_inspect.hdvp            引擎调用版（给 C#）
└── src/
    ├── VisionForge.Common/
    │   ├── Mvvm/{ObservableObject, RelayCommand}.cs
    │   └── Events/EventAggregator.cs
    ├── VisionForge.Core/
    │   ├── Interfaces/{ICamera, IPlcClient, IInspectionAlgorithm, IStores, ILogger}.cs
    │   ├── Models/{CameraModels, PlcModels, Recipe, SopDefinition, InspectionModels}.cs
    │   └── Services/SopProcess.cs
    ├── VisionForge.Hardware/
    │   ├── Camera/{MockCamera, HikCamera, CameraFactory}.cs
    │   ├── Plc/{MockPlcClient, ModbusTcpPlcClient, PlcFactory}.cs
    │   └── Vision/{AlgorithmRegistry, ParameterReader,
    │               ThresholdBlobAlgorithm, DimensionMeasureAlgorithm,
    │               HalconInspectionAlgorithm}.cs
    ├── VisionForge.Infrastructure/
    │   ├── Config/{AppSettings, AppSettingsProvider}.cs
    │   ├── Logging/FileLogger.cs
    │   └── Storage/{JsonRecipeRepository, JsonLineHistoryStore, BmpEncoder, SopDefinitionStore}.cs
    └── VisionForge.Main/
        ├── App.xaml(.cs)            组合根：所有依赖在这里装配
        ├── MainWindow.xaml(.cs)     界面
        ├── ViewModels/{MainViewModel, ParameterFieldViewModel}.cs
        ├── Converters/ParameterTemplateSelector.cs
        └── Imaging/FrameConverter.cs
```

---

## 七、为什么不引第三方包

本项目**刻意做到零 NuGet 依赖**：

| 常见选择 | 本项目做法 | 原因 |
|---|---|---|
| CommunityToolkit.Mvvm | 自研 100 行 MVVM 基础类 | 离线可编译；逻辑看得见，新人不用先学一套源生成器 |
| Newtonsoft.Json | 内置 `System.Text.Json` | 框架自带，无需引包 |
| NModbus | 自研 Modbus TCP（约 200 行） | 排障时能直接看原始字节，比调试黑盒库快 |
| System.Drawing / SkiaSharp | 自研 BMP 编码器 | 避免 Infrastructure 层依赖 UI 框架；BMP 格式简单到能测透 |
| Serilog / NLog | 自研 FileLogger | 需求就三点：落盘、带毫秒、不阻塞主流程 |

**收益是实打实的**：厂区内网机器不用配 NuGet 源、没有版本冲突和漏洞升级压力、
出问题时排查范围完全可控。真需要引包时（如 OpenCVSharp），去掉 `nuget.config` 里的 `<clear />` 即可。

---

## 八、后续可扩展

- **多步骤 SOP 界面**：目前状态机已支持完整流程，界面可再加"当前步骤大屏提示 + 工序切换"
- **声光报警**：接三色灯 / 蜂鸣器（走 PLC 输出点即可，`IPlcClient.WriteAsync` 已具备）
- **报表模块**：目前有 CSV 导出，可加日报/周报自动生成
- **用户权限**：人工放行加密码；操作员登录记录
- **数据库升级**：历史记录量大时，把 `JsonLineHistoryStore` 换成 SQLite 实现即可（上层只依赖接口）
