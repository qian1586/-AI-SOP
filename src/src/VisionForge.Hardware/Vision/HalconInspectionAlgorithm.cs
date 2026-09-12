using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

#if HALCON
using System.Runtime.InteropServices;
using HalconDotNet;
#endif

namespace VisionForge.Hardware.Vision;

/// <summary>
/// Halcon 检测算法插件 —— 把 HDevelop 写的视觉算法接入本框架。
///
/// <para><b>⚠️ 最关键的一条：喂给 HDevEngine 的过程里绝对不能有 while 循环。</b></para>
///
/// 常见错误做法：直接把 HDevelop 里那个"自带相机 + while(true) 主循环"的脚本
/// 拿去给 C# 调。结果就是 <c>Execute()</c> 一调就永不返回，上位机界面直接卡死，
/// 连"停止"按钮都点不动（因为它根本没机会处理消息）。
///
/// <b>正确做法：同一个算法要维护两个文件：</b>
/// <list type="bullet">
///   <item><c>sop_standalone.hdev</c> —— 独立运行版。自带 open_framegrabber、
///         硬件触发配置、while 循环。给现场调试和算法验证用，只在 HDevelop 里跑。</item>
///   <item><c>sop_inspect.hdvp</c> —— 引擎调用版。<b>无相机、无循环</b>，
///         形如 <c>procedure sop_inspect(Image : : MinScore : Part_OK, Tool_OK, ...)</c>，
///         一行图进、几个结果出。给 C# 调。</item>
/// </list>
/// 相机采集、触发等待、循环调度这些"时序"的事，全部交给 C# 上位机来做 ——
/// 那本来就是上位机的职责。Halcon 只管"给我一张图，我告诉你结果"。
///
/// <para><b>另外三点工程注意：</b></para>
/// <list type="number">
///   <item>HDevEngine / HDevProcedure 加载很慢（数百毫秒），必须缓存复用，
///         不能每次检测都 new 一个</item>
///   <item>HDevProcedureCall 不是线程安全的，多工位并发时必须串行化或每线程一个实例</item>
///   <item>HImage 用完必须 Dispose。Halcon 是原生内存，靠 GC 收会很快耗尽非托管内存</item>
/// </list>
/// </summary>
public sealed class HalconInspectionAlgorithm : IInspectionAlgorithm
{
    /// <summary>按 hdev 文件路径缓存已加载的过程，避免重复加载。</summary>
#if HALCON
    private static readonly Dictionary<string, HDevProcedure> ProcedureCache = new();
    private static readonly object CacheGate = new();
    private readonly object _callGate = new();
#endif

    public string Key => "halcon-hdev";

    public string DisplayName => "Halcon 视觉算法（HDevEngine）";

    public string Description =>
        "调用 HDevelop 编写并导出的检测过程（.hdvp）。视觉算法在 Halcon 里迭代，上位机不需要重编译。";

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = new[]
    {
        new ParameterDefinition
        {
            Key = "HdevFile", DisplayName = "HDevelop 过程文件", Type = ParameterType.Text,
            Default = 0,
            Description = "要调用的 HDevelop 过程文件，例如 halcon/sop_inspect.hdvp。" +
                          "必须是「引擎调用版」：文件里不能有开相机和 while 死循环，" +
                          "否则一调用界面就卡死（这是 Halcon 对接最常见的坑）。",
        },
        new ParameterDefinition
        {
            Key = "ProcedureName", DisplayName = "过程名", Type = ParameterType.Text,
            Default = 0,
            Description = "文件里的过程名（如 sop_inspect）。留空就用文件里的主过程。",
        },
        new ParameterDefinition
        {
            Key = "MinScorePart", DisplayName = "零件匹配得分下限", Type = ParameterType.Number,
            Min = 0, Max = 1, Default = 0.7,
            Description = "模板匹配的及格线（0~1）：零件在位时得分通常 0.85 以上，" +
                          "划到 0.7 比较保险。调高 = 抓漏装更严（也更容易误判），调低 = 更容易放行。",
        },
        new ParameterDefinition
        {
            Key = "MinScoreTool", DisplayName = "工具匹配得分下限", Type = ParameterType.Number,
            Min = 0, Max = 1, Default = 0.7,
            Description = "同上，用来判断「工具用完了有没有放回原位」。工具位置固定就沿用 0.7。",
        },
        new ParameterDefinition
        {
            Key = "RoiPart", DisplayName = "零件检测区(x,y,w,h)", Type = ParameterType.Text,
            Default = 0,
            Description = "零件检测区，归一化坐标（0~1），格式 x,y,w,h，例如 0.17,0.21,0.33,0.23。" +
                          "留空 = 用 Halcon 程序里写死的区域。",
        },
        new ParameterDefinition
        {
            Key = "RoiTool", DisplayName = "工具检测区(x,y,w,h)", Type = ParameterType.Text,
            Default = 0,
            Description = "工具归位检测区，格式同上一项。留空 = 用 Halcon 程序里写死的区域。",
        },
    };

    // ==================================================================
    public InspectionResult Run(CameraFrame frame, Recipe recipe)
    {
        var p = new ParameterReader(recipe, Parameters);

        var result = new InspectionResult
        {
            RecipeId = recipe.Id,
            RecipeName = recipe.Name,
            ProductModel = recipe.ProductModel,
            Timestamp = DateTime.Now,
        };

        string hdevFile = p.GetString("HdevFile");
        if (string.IsNullOrWhiteSpace(hdevFile))
            return InspectionResult.Error("未配置 HdevFile 参数，无法调用 Halcon 过程");

        if (!File.Exists(hdevFile))
            return InspectionResult.Error($"找不到 HDevelop 过程文件：{hdevFile}");

#if !HALCON
        return InspectionResult.Error(
            "编译时未启用 HALCON。请在 VisionForge.Hardware.csproj 中定义编译符号 HALCON，" +
            "并引用 halcondotnet.dll（Halcon 安装目录下 bin/dotnet35 或 dotnet-core）。");
#else
        HImage? himg = null;
        HDevProcedureCall? call = null;
        try
        {
            himg = ToHImage(frame);
            call = CreateCall(hdevFile, p.GetString("ProcedureName"));

            // ---------- 传参 ----------
            call.SetInputCtrlParamTuple("MinScorePart", p.GetDouble("MinScorePart", 0.7));
            call.SetInputCtrlParamTuple("MinScoreTool", p.GetDouble("MinScoreTool", 0.7));

            if (TryParseRoi(p.GetString("RoiPart"), out var rp))
            {
                call.SetInputCtrlParamTuple("RoiPartRow1", rp.R1);
                call.SetInputCtrlParamTuple("RoiPartCol1", rp.C1);
                call.SetInputCtrlParamTuple("RoiPartRow2", rp.R2);
                call.SetInputCtrlParamTuple("RoiPartCol2", rp.C2);
            }
            if (TryParseRoi(p.GetString("RoiTool"), out var rt))
            {
                call.SetInputCtrlParamTuple("RoiToolRow1", rt.R1);
                call.SetInputCtrlParamTuple("RoiToolCol1", rt.C1);
                call.SetInputCtrlParamTuple("RoiToolRow2", rt.R2);
                call.SetInputCtrlParamTuple("RoiToolCol2", rt.C2);
            }

            call.SetInputIconicParamObject("Image", himg);

            // ---------- 执行（这一句必须在毫秒级返回，否则说明 hdvp 里有死循环）----------
            lock (_callGate)
            {
                call.Execute();
            }

            // ---------- 取结果 ----------
            int partOk = ReadInt(call, "Part_OK");
            int toolOk = ReadInt(call, "Tool_OK");
            int stepOk = ReadInt(call, "Step_OK");
            double partScore = ReadDouble(call, "Part_Score");
            double toolScore = ReadDouble(call, "Tool_Score");

            result.Measurements.Add(new MeasurementItem
            {
                Name = "零件匹配得分", Value = Math.Round(partScore, 4),
                LowerLimit = (double)p.GetDouble("MinScorePart", 0.7),
            });
            result.Measurements.Add(new MeasurementItem
            {
                Name = "工具匹配得分", Value = Math.Round(toolScore, 4),
                LowerLimit = (double)p.GetDouble("MinScoreTool", 0.7),
            });

            // ---------- 翻译成人话 ----------
            // Halcon 返回的是 0/1 和得分，直接显示"NG"现场不知道错在哪。
            // 这一层翻译就是上位机的价值所在。
            if (stepOk == 1)
            {
                result.Verdict = InspectionVerdict.Pass;
                result.JudgementReason =
                    $"零件在位（得分 {partScore:F3}），工具已归位（得分 {toolScore:F3}），本步骤合规";
            }
            else
            {
                result.Verdict = InspectionVerdict.Fail;
                var reasons = new List<string>();
                if (partOk != 1)
                    reasons.Add($"零件缺失或错位（得分 {partScore:F3}，低于阈值 {p.GetDouble("MinScorePart", 0.7):F2}）");
                if (toolOk != 1)
                    reasons.Add($"工具未归位（得分 {toolScore:F3}，低于阈值 {p.GetDouble("MinScoreTool", 0.7):F2}）");

                result.JudgementReason = string.Join("；", reasons);

                if (partOk != 1)
                    result.Defects.Add(new DefectItem { Type = "零件缺失/错位", Confidence = 1 - partScore });
                if (toolOk != 1)
                    result.Defects.Add(new DefectItem { Type = "工具未归位", Confidence = 1 - toolScore });
            }

            return result;
        }
        catch (Exception ex)
        {
            // Halcon 抛的异常信息通常很有价值（比如"File not found: xxx"），原样带出来
            return InspectionResult.Error($"Halcon 过程执行失败：{ex.Message}");
        }
        finally
        {
            call?.Dispose();
            himg?.Dispose();
        }
#endif
    }

    // ==================================================================
    // CameraFrame → HImage
    // ==================================================================
#if HALCON
    private static HImage ToHImage(CameraFrame frame)
    {
        var img = new HImage();

        if (frame.Format == PixelFormat.Mono8)
        {
            // 关键：必须 Pin 住托管数组，把指针交给 Halcon。
            // GenImage1 会把数据复制到 Halcon 自己的内存，所以 finally 里释放是安全的。
            var handle = GCHandle.Alloc(frame.Data, GCHandleType.Pinned);
            try
            {
                img.GenImage1("byte", frame.Width, frame.Height, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            return img;
        }

        // Bgr24（交错）→ Halcon 的三平面格式
        int count = frame.Width * frame.Height;
        var b = new byte[count];
        var g = new byte[count];
        var r = new byte[count];

        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                int src = y * frame.Stride + x * 3;
                int dst = y * frame.Width + x;
                b[dst] = frame.Data[src];
                g[dst] = frame.Data[src + 1];
                r[dst] = frame.Data[src + 2];
            }
        }

        var hb = GCHandle.Alloc(b, GCHandleType.Pinned);
        var hg = GCHandle.Alloc(g, GCHandleType.Pinned);
        var hr = GCHandle.Alloc(r, GCHandleType.Pinned);
        try
        {
            img.GenImage3("byte", frame.Width, frame.Height,
                          hb.AddrOfPinnedObject(), hg.AddrOfPinnedObject(), hr.AddrOfPinnedObject());
        }
        finally
        {
            hb.Free(); hg.Free(); hr.Free();
        }
        return img;
    }

    private static HDevProcedureCall CreateCall(string hdevFile, string procedureName)
    {
        var fullPath = Path.GetFullPath(hdevFile);

        lock (CacheGate)
        {
            if (!ProcedureCache.TryGetValue(fullPath, out var proc))
            {
                var engine = new HDevEngine();
                // 让 Halcon 能在过程同级目录找到它内部 include 的其它过程
                engine.SetProcedurePath(Path.GetDirectoryName(fullPath) ?? ".");

                var program = new HDevProgram(fullPath);
                proc = string.IsNullOrWhiteSpace(procedureName)
                    ? program.GetMainProcedure()
                    : program.GetProcedure(procedureName);

                ProcedureCache[fullPath] = proc;
            }
            return new HDevProcedureCall(proc);
        }
    }

    private static int ReadInt(HDevProcedureCall call, string name)
    {
        try
        {
            var v = call.GetOutputCtrlParamTuple(name);
            return v is null ? -1 : Convert.ToInt32(v);
        }
        catch
        {
            // 过程没导出这个变量时不应中断检测
            return -1;
        }
    }

    private static double ReadDouble(HDevProcedureCall call, string name)
    {
        try
        {
            var v = call.GetOutputCtrlParamTuple(name);
            return v is null ? 0 : Convert.ToDouble(v);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryParseRoi(string text, out (double R1, double C1, double R2, double C2) roi)
    {
        roi = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return false;
        if (!parts.All(s => double.TryParse(s, out _))) return false;

        roi = (double.Parse(parts[0]), double.Parse(parts[1]),
               double.Parse(parts[2]), double.Parse(parts[3]));
        return true;
    }
#endif

    /// <summary>清空过程缓存。改了 .hdvp 之后调用，否则会一直用旧版本。</summary>
    public static void ClearCache()
    {
#if HALCON
        lock (CacheGate)
        {
            foreach (var p in ProcedureCache.Values) p.Dispose();
            ProcedureCache.Clear();
        }
#endif
    }
}
