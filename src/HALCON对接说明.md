# Halcon 对接说明 & 原版代码问题清单

> 这是把 HDevelop 视觉算法接入 C# 上位机时必须搞清楚的几件事。
> 你手上那套 Halcon 代码（硬件触发 + 分目录存图 + 水印 + 计数 + 日志）
> 作为**独立运行版**是完整可用的，但**直接拿去给 C# 调会出问题**。

---

## 一、先说最严重的：`while(true)` 会让上位机卡死

这是必须改的第一件事，不改后面全白搭。

原代码的结构是：

```halcon
open_framegrabber(...)        * 自己开相机
while(true)                   * 自己死循环
    grab_image(...)           * 自己等硬件触发
    ...检测、存图、写日志...
endwhile
```

**这套逻辑在 HDevelop 里跑没问题，但用 HDevEngine 调用时会永久阻塞。**

原因：`HDevProcedureCall.Execute()` 是**同步**的 —— 它会一直执行到过程返回为止。
过程里有 `while(true)`，那它永远不返回。C# 那一侧的后果是：

- 调用线程被占死
- 如果在 UI 线程调用 → 界面直接冻住，**连"停止"按钮都点不动**
- 因为它根本没机会回到消息循环去处理鼠标点击

### 正确做法：一个算法，两个文件

| 文件 | 用途 | 形态 |
|---|---|---|
| `halcon/train_templates.hdev` | 训练模板，跑一次 | 读标准样品图 → 建模板 → 存 .shm |
| `halcon/sop_standalone.hdev` | 现场调试用（你可自行保留原版） | 自带相机 + while 循环，**只在 HDevelop 里跑** |
| `halcon/sop_inspect.hdvp` | **给 C# 调** | 无相机、无循环，一帧进、结果出 |

引擎版的签名长这样：

```halcon
procedure sop_inspect (Image : : MinScorePart, MinScoreTool, ... : Part_OK, Tool_OK, Step_OK, ...)
```

**相机采集、触发等待、循环调度，全部交给 C# 做。** 那本来就是上位机的职责。
Halcon 只负责"给我一张图，我告诉你结果"—— 职责一分开，两边都清爽。

对应到本项目的代码：`HalconInspectionAlgorithm.Run()` 每次调用就是执行一次过程，毫秒级返回。

---

## 二、第二个坑：训练代码注释掉后，`ModelID_Part` 就没有了

原代码这么写：

```halcon
* 【仅首次训练模板使用，训练完成后，注释掉这一段！！】
* reduce_domain (Image, ROI_Part, ImageROI_Part)
* create_shape_model (ImageROI_Part, ..., ModelID_Part)
```

**问题：注释掉之后 `ModelID_Part` 这个变量根本不存在。**
后面 `find_shape_model(..., ModelID_Part, ...)` 会直接报「变量未初始化」。

这不是小瑕疵，是**跑不起来**。很多人第一次跑会遇到，然后一脸茫然。

### 正确做法：训练与匹配彻底分离

```halcon
* 训练阶段（train_templates.hdev，跑一次）
create_shape_model (ImageROI_Part, ..., ModelID_Part)
write_shape_model (ModelID_Part, 'part.shm')      * ← 存盘，这是关键

* 运行阶段（sop_inspect.hdvp，每次调用）
read_shape_model ('part.shm', ModelID_Part)       * ← 从盘里读
find_shape_model (Image_Part, ModelID_Part, ...)
```

顺带一提，**现场每次触发都重新训练模板是不可接受的** —— 建模板要几十到几百毫秒，
而且同一批产品每次建的模板还略有差异，判定结果会漂。

---

## 三、其他六个值得改的地方（前三个会直接报错）

### 1. `get_system_time (DateTime)` —— 参数个数错误，直接报错

Halcon 的签名是固定的 8 个输出参数：

```halcon
get_system_time ( : : : MSecond, Second, Minute, Hour, Day, YDay, Month, Year)
```

**没有"返回一个字符串"的单参数版本。** `get_system_time (DateTime)` 会直接报参数不匹配。

正解是拆开再自己拼（顺带把冒号问题一起解决了）：

```halcon
get_system_time (MSecond, Second, Minute, Hour, Day, YDay, Month, Year)
tuple_string (Year, '04d', SYear)
tuple_string (Month, '02d', SMonth)
tuple_string (Day, '02d', SDay)
tuple_string (Hour, '02d', SHour)
tuple_string (Minute, '02d', SMinute)
tuple_string (Second, '02d', SSecond)

FileName := SYear + SMonth + SDay + '_' + SHour + SMinute + SSecond + '.png'
```

### 2. `disp_message` 的第一个参数必须是窗口句柄 —— 编译期就过不了

原代码：

```halcon
copy_image(Image, ImageWithWaterMark)
disp_message (ImageWithWaterMark, 'Time:'+DateTime, 'image', 10, 10, 'black', 'true')
                                                 ↑ 第一个参数传了图像
```

`disp_message` 的签名是：

```halcon
disp_message (WindowHandle, String, CoordSystem, Row, Column, Color, Box)
```

第一位是 **WindowHandle**（窗口句柄），不是图像对象。
**Halcon 里没有"直接在图像对象上写字"的 disp_message**，所以这段是编译期就过不去的类型错误。

**水印要真的落在保存的图片上，正确链路是：**

```halcon
dev_display (Image)                          * 1. 先把图画到窗口
disp_message (WindowHandle, '...', 'window', ...)   * 2. 文字画到窗口
dump_window_image (ImageToSave, WindowHandle)       * 3. 把窗口内容抓成图像 ← 关键这一步
write_image (ImageToSave, 'png', 0, FileName)       * 4. 再存盘
```

`dump_window_image` 就是"截图"——把窗口上叠加的图和文字一起抓下来变成图像对象。

### 3. `make_dir` 在目录已存在时会报错

原版注释写的是"已存在不会报错"，**实际上 Halcon 的 `make_dir` 在目录已存在时会抛异常**。
程序第二次运行就会中断，而且这个错误挺难一眼看出原因。

正解是先判断再建：

```halcon
file_exists (Dir, Exists)
if (not Exists)
    make_dir (Dir)
endif
```

### 4. `create_shape_model` 的 MinContrast 参数类型不对

原代码：

```halcon
create_shape_model (ImageROI_Part, 'auto', 0, rad(360), 'auto', 'auto', 'use_polarity', 30, 0.8, ModelID_Part)
                                                                                              ↑这里
```

参数顺序是：`NumLevels, AngleStart, AngleExtent, AngleStep, Optimization, Metric, Contrast, MinContrast, ModelID`。

**`MinContrast` 是整数**（最小对比度，按灰度级），传 `0.8` 是无效值。
Halcon 有时会静默容忍、有时给出意外结果 —— 这种"不报错的错"最难查。

改成 `'auto'` 或一个正常整数（如 `10`）即可。

### 5. `AngleExtent = rad(360)` 对"有无检测"是过度的

全角度搜索有两个代价：
- **慢 3~5 倍**（模板要在 360° 里逐格试）
- **误匹配率明显升高**：圆形或近似圆形的零件，在 360° 下几乎处处都能匹配上，"零件被取走"也会被判成还在

如果工件方向固定（工装定位销一般都固定），写 `rad(5)` 就够；
上下料可能翻转就写 `rad(180)`。**这一条改完，速度和准确率同时改善。**

### 6. `g_count_ok` / `g_count_ng` 是 HDevelop 全局变量 —— HDevEngine 取不到

HDevEngine 的 `GetOutputCtrlParamTuple` 只能拿到**过程的输出参数**，
拿不到 HDevelop 里的全局变量。

所以计数这件事**本来就该在 C# 侧做**（本项目就是 `MainViewModel.OkCount / NgCount`）。
好处还多一条：C# 的计数能存数据库、能按班次/型号分组，HDevelop 的变量一关程序就没了。

### 7. 时间戳带冒号 + 日志编码

- `get_system_time` 生成的时间戳形如 `2026-09-11 18:52:04:123`，**Windows 文件名不允许 `:`**。
  你自己也提到了这一点 —— 要在 Halcon 里用 `tuple_split` + 拼接替换掉。本项目直接用 C# 生成文件名，绕开了。
- `write_string_file('record_log.txt', log_str, 'append')` 写出来的是**无 BOM 的 UTF-8**，
  Excel 直接打开中文会乱码。加 BOM 或用 CSV 更省事（本项目导出 CSV 时强制写 BOM）。

---

## 四、接线与相机配置（原文档里的部分是对的，补充三点）

原文档说的这些没问题，照做：
- 光电传感器接相机 **Line0** 输入引脚，优先选 NPN 型（与相机 IO 电平匹配）
- MstarViewer 里设 `TriggerMode = Hardware`、`TriggerSource = Line0`、`TriggerActivation = RisingEdge`
- 参数**保存到相机非易失内存**，断电不丢

**补充第一点 — 触发信号要去抖。**
"人手离开 → 光电导通"这个边沿在快速动作时可能抖动产生多个脉冲，
相机就会连拍几张，把一张产品的图判成好几件。

对策二选一：
- 相机侧：设置触发延时（`TriggerDelay`）或触发滤波
- 上位机侧：两次检测之间加最小间隔（本项目用 `IsBusy` 标志天然挡住了重复触发）

**补充第二点 — NPN 还是 PNP 要现场确认。**
选错了不是"不灵敏"，而是**完全不触发或一直触发**。用万用表量一下光电输出对 0V 的电压更靠谱。

**补充第三点 — 上升沿还是下降沿，取决于你要"抓住哪个瞬间"。**
- `RisingEdge`（手离开时触发）：抓的是**作业完成后**的静态画面 —— 你现在的方案，**推荐**
- `FallingEdge`（手伸入时触发）：抓的是**作业开始前**的画面

你要判"零件在位 + 工具归位"，应该用 RisingEdge，因为那是"干完活了"的状态。选对了。

---

## 五、这套东西是怎么接进本项目的

Halcon 算法在本项目里就是一个**插件**（`IInspectionAlgorithm` 的实现），
主程序完全不知道 Halcon 的存在：

```
配方(Recipe) 选算法 Key = "halcon-hdev"
        ↓
AlgorithmRegistry 找到 HalconInspectionAlgorithm
        ↓
Run(frame, recipe)
        ├─ CameraFrame → HImage（本项目已实现转换）
        ├─ HDevEngine 调用 halcon/sop_inspect.hdvp
        └─ 读回 Part_OK / Tool_OK / Step_OK / Score
        ↓
返回标准 InspectionResult（OK/NG + 人话理由 + 测量值）
        ↓
SOP 状态机判定是否放行 → 写 PLC → 存 NG 图 → 写历史
```

### 启用 HALCON 编译符号

默认**不启用**（因为本机没装 Halcon），代码走降级分支，保证任何人 clone 下来都能编译。
要用真 Halcon 时：

1. 安装 Halcon（需要 `halcondotnet.dll`）
2. 在 `VisionForge.Hardware.csproj` 里加：

```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);HALCON</DefineConstants>
</PropertyGroup>
<ItemGroup>
  <Reference Include="halcondotnet">
    <HintPath>C:\Program Files\MVTec\HALCON-XX.X\bin\dotnet35\halcondotnet.dll</HintPath>
  </Reference>
</ItemGroup>
```

3. 把 `halcondotnet.dll` 及 Halcon 依赖复制到输出目录

> 注：HDevEngine 是**商用的独立授权**（比 Halcon 运行时便宜，但需要单独买）。
> 如果只是想把视觉结果给 C# 用，也可以不走 HDevEngine ——
> 用 HDevEngine 的替代方案：Halcon 侧把结果写成文件/共享内存，C# 侧读。
> 但那样跨进程同步会更麻烦，**除非有授权成本压力，否则 HDevEngine 是更干净的路**。

---

## 六、一句话总结

| 你原来手上的 | 问题 | 严重度 | 现在 |
|---|---|---|---|
| 一个 .hdev 打天下 | 死循环，HDevEngine 调了卡死界面 | 🔴 阻塞 | 拆成 训练版 / 引擎版 两个文件 |
| 模板训练代码注释掉 | `ModelID_Part` 未定义，直接跑不起来 | 🔴 报错 | 训练完 `write_shape_model` 存 .shm，运行版 `read_shape_model` |
| `get_system_time (DateTime)` | 参数个数不对，直接报错 | 🔴 报错 | 8 个输出参数拆开重组 |
| `disp_message (Image, ...)` | 第一位要窗口句柄，编译期类型错误 | 🔴 报错 | `dump_window_image` 抓窗口存图 |
| `make_dir` 重复调用 | 目录已存在会抛异常，第二次运行就断 | 🟠 中断 | `file_exists` 先判断 |
| `MinContrast` 传 0.8 | 该参数是整数，属静默无效值 | 🟡 隐患 | 改 `'auto'` 或整数 |
| `AngleExtent = rad(360)` | 慢 3~5 倍，圆件易误匹配 | 🟡 效果 | 方向固定写 `rad(5)` |
| 计数在 Halcon 全局变量 | HDevEngine 取不到 | 🟡 功能 | 移到 C#，还能进数据库 |
| 时间戳直接做文件名 | 内含冒号，Windows 非法 | 🟡 报错 | 重组格式避开冒号 |
| 日志无 BOM | Excel 打开中文乱码 | 🟢 体验 | 加 `utf8` 选项 |

**核心原则：Halcon 负责"看懂图"，上位机负责"什么时候看图、看完怎么办"。**
这条线划清楚，两边都简单。
