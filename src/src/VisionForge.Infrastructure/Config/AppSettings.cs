using System.Text.Json;
using System.Text.Json.Serialization;
using VisionForge.Core.Models;
using VisionForge.Core.Services;

namespace VisionForge.Infrastructure.Config;

/// <summary>
/// 应用配置。
///
/// 刻意做成"人可读可手改"的 JSON，而不是注册表或二进制配置：
///   现场工程师要在没有开发人员的情况下改 IP、加地址、换相机，这是常态。
/// </summary>
public sealed class AppSettings
{
    // ---------- 基本 ----------
    public string OperatorName { get; set; } = "操作员";

    /// <summary>数据根目录。配方、历史、NG 图、日志都在它下面。</summary>
    public string DataRoot { get; set; } = "data";

    /// <summary>上次使用的配方 Id，启动时自动加载。</summary>
    public string LastRecipeId { get; set; } = string.Empty;

    /// <summary>上次使用的产品型号，顶栏可直接修改并写回。</summary>
    public string LastProductModel { get; set; } = string.Empty;

    // ---------- 存储策略 ----------
    public bool SaveNgImage { get; set; } = true;

    /// <summary>NG 图保留天数。超期自动清理，避免磁盘被撑爆。</summary>
    public int NgImageRetentionDays { get; set; } = 90;

    /// <summary>检测历史保留条数上限。</summary>
    public int HistoryRetentionCount { get; set; } = 200_000;

    /// <summary>
    /// 界面缩放倍数（1.0 = 100%，窗口整体等比缩放）。
    ///
    /// <para><b>默认就是 100%。</b>界面上那个滑条已经按现场要求撤掉了 ——
    /// 一旦被误拖大，整个界面会超出屏幕，看起来就像"软件坏了"。
    /// 显示器真的换了、觉得字小，改 config\appsettings.json 里的这个值即可（0.8~1.6）。</para>
    /// </summary>
    public double UiScale { get; set; } = 1.0;

    // ---------- 硬件 ----------
    public List<CameraInfo> Cameras { get; set; } = new();

    public PlcConnectionOptions Plc { get; set; } = new();

    public List<PlcAddressItem> PlcAddresses { get; set; } = new();

    /// <summary>声光报警配置。</summary>
    public AlarmOptions Alarm { get; set; } = new();

    /// <summary>动作计时与标准工时配置。</summary>
    public TimingOptions Timing { get; set; } = new();

    /// <summary>多工位联网：本机身份 + 上报给哪个汇总终端。</summary>
    public StationOptions Station { get; set; } = new();

    /// <summary>MES 对接配置。</summary>
    public MesOptions Mes { get; set; } = new();

    /// <summary>AI 自主学习配置（边生产边学，越用越准）。</summary>
    public SelfLearningOptions SelfLearning { get; set; } = new();

    // ---------- 派生路径（不序列化） ----------
    [JsonIgnore] public string RecipeDirectory => Path.Combine(DataRoot, "recipes");
    [JsonIgnore] public string HistoryDirectory => Path.Combine(DataRoot, "history");
    [JsonIgnore] public string NgImageDirectory => Path.Combine(DataRoot, "ng-images");
    [JsonIgnore] public string LogDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// 生成一份"开箱即用"的默认配置：
    /// 一台模拟相机 + 一台模拟 PLC，不接任何硬件就能把界面跑起来。
    /// 这对第一次拿到代码的人是刚需 —— 不能让人先折腾硬件才能看到界面。
    /// </summary>
    public static AppSettings CreateDefault()
    {
        var settings = new AppSettings
        {
            Cameras = new List<CameraInfo>
            {
                new()
                {
                    Id = "CAM-001",
                    DisplayName = "工位1 · 模拟相机",
                    Vendor = "Mock",
                    Model = "Synthetic-1280x1024",
                    SerialNumber = "MOCK000001",
                },
            },
            Plc = new PlcConnectionOptions
            {
                Name = "产线PLC",
                Brand = PlcBrand.Mock,
                IpAddress = "192.168.1.10",
                Port = 502,
            },
        };

        settings.PlcAddresses = new List<PlcAddressItem>
        {
            new() { Name = "DeviceReady",   Address = "D100", DataType = PlcDataType.Int16, Writable = false, Description = "设备就绪" },
            new() { Name = "PartArrived",   Address = "D101", DataType = PlcDataType.Int16, Writable = false, Description = "工件到位信号" },
            new() { Name = "InspectResult", Address = "D200", DataType = PlcDataType.Int16, Writable = true,  Description = "检测结论 1=OK 0=NG" },
            new() { Name = "AllowPass",     Address = "M100", DataType = PlcDataType.Bool,  Writable = true,  Description = "允许放行" },
            new() { Name = "RejectCylinder",Address = "M101", DataType = PlcDataType.Bool,  Writable = true,  Description = "剔除气缸" },

            // 声光报警（三色灯 + 蜂鸣器，占 4 个输出点）
            new() { Name = "LightGreen",    Address = "M110", DataType = PlcDataType.Bool,  Writable = true,  Description = "绿灯：正常" },
            new() { Name = "LightYellow",   Address = "M111", DataType = PlcDataType.Bool,  Writable = true,  Description = "黄灯：警示（不停线）" },
            new() { Name = "LightRed",      Address = "M112", DataType = PlcDataType.Bool,  Writable = true,  Description = "红灯：故障（停线）" },
            new() { Name = "Buzzer",        Address = "M113", DataType = PlcDataType.Bool,  Writable = true,  Description = "蜂鸣器" },
        };

        return settings;
    }
}

/// <summary>
/// 动作计时的配置：要不要记、记哪些、每道工序的标准工时是多少。
///
/// <para>标准工时放在配置里而不是写死在代码：每个工位、每个型号都不一样，
/// 现场要能自己调，而且调完立即生效、重启还在。</para>
/// </summary>
public sealed class TimingOptions
{
    /// <summary>是否记录动作计时（关掉就完全不落盘，适合调试或临时工位）。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>NG 的那一次要不要也记（复盘"为什么这一下花了这么久"时有用）。</summary>
    public bool RecordNg { get; set; } = true;

    /// <summary>慢多少算"明显慢"（%）。默认 30% —— 超过就在界面上标红。</summary>
    public double SlowAlertPercent { get; set; } = 30;

    /// <summary>每道工序的标准工时（秒）。StepSeq 对应"第 N 个框"。</summary>
    public List<StepTimeStandard> Standards { get; set; } = new();

    /// <summary>取某一步的标准工时（毫秒）。没配就返回 null。</summary>
    public double? FindStandardMs(int stepSeq)
    {
        var item = Standards.FirstOrDefault(s => s.StepSeq == stepSeq);
        return item is null || item.StandardSec <= 0 ? null : item.StandardSec * 1000.0;
    }

    /// <summary>取/建某一步的标准工时项（设置窗口改它）。</summary>
    public StepTimeStandard Ensure(int stepSeq, string stepName)
    {
        var item = Standards.FirstOrDefault(s => s.StepSeq == stepSeq);
        if (item is null)
        {
            item = new StepTimeStandard { StepSeq = stepSeq, StepName = stepName };
            Standards.Add(item);
        }
        else if (!string.IsNullOrWhiteSpace(stepName))
        {
            item.StepName = stepName;
        }

        return item;
    }
}

/// <summary>
/// 多工位联网配置：本机是谁、要不要把数据上报、要不要当汇总终端。
///
/// <para>同一套程序拷到几十个工位，差别只在这几个字段：
/// 工位端填"编号 + 终端地址"，终端那台电脑勾上"作为汇总终端"即可。</para>
/// </summary>
public sealed class StationOptions
{
    /// <summary>本机是不是这套系统的一部分（关掉就完全单机跑，不上报也不建服务）。</summary>
    public bool Enabled { get; set; }

    /// <summary>工位编号（全厂唯一，建议与 MES 的工位编码一致），如 ST-07。</summary>
    public string StationCode { get; set; } = "ST-01";

    /// <summary>工位名（现场看的），如"主机装配 7 号位"。</summary>
    public string StationName { get; set; } = string.Empty;

    /// <summary>产线名，如 Line-03。</summary>
    public string LineName { get; set; } = string.Empty;

    /// <summary>上报目标：汇总终端的地址，如 http://192.168.1.50:8088。</summary>
    public string HubUrl { get; set; } = string.Empty;

    /// <summary>上报间隔（秒）。3 秒足够让看板看起来是实时的，又不至于把网络压满。</summary>
    public int ReportIntervalSec { get; set; } = 3;

    /// <summary>本机是否作为汇总终端（对外提供看板页面）。</summary>
    public bool IsHub { get; set; }

    /// <summary>汇总终端的监听端口。</summary>
    public int HubPort { get; set; } = 8088;
}

/// <summary>
/// MES 对接配置。
///
/// <para>不同厂家的 MES 接口千差万别，所以这里只放"通用三件套"：
/// 接口地址、令牌、超时；要换成 WebService/中间件时换 <c>IMesClient</c> 实现即可，
/// 配置项不用动。</para>
/// </summary>
public sealed class MesOptions
{
    /// <summary>是否启用上传（关掉则只本地留存）。</summary>
    public bool Enabled { get; set; }

    /// <summary>MES 接收接口地址（HTTP POST，JSON 体）。</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>鉴权令牌（留空则不加 Authorization 头）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>单次上传超时（秒）。</summary>
    public int TimeoutSec { get; set; } = 5;

    /// <summary>同时把同样的 JSON 落一份到本地（对接联调、以及没 MES 时先跑起来）。</summary>
    public bool SaveLocalCopy { get; set; } = true;
}

/// <summary>配置文件读写。</summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },   // 枚举写成字符串，方便手改
    };

    public static AppSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var fresh = AppSettings.CreateDefault();
            Save(fresh, path);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null) return AppSettings.CreateDefault();

            // 兜底：配置文件是老版本时，缺的段补上默认值
            loaded.Cameras ??= new List<CameraInfo>();
            loaded.PlcAddresses ??= new List<PlcAddressItem>();
            loaded.Plc ??= new PlcConnectionOptions();
            loaded.Alarm ??= new AlarmOptions();
            loaded.Timing ??= new TimingOptions();
            loaded.Station ??= new StationOptions();
            loaded.Mes ??= new MesOptions();
            loaded.SelfLearning ??= new SelfLearningOptions();
            return loaded;
        }
        catch (Exception ex)
        {
            // 配置损坏时不要让程序起不来 —— 备份坏文件，用默认值继续
            var backup = path + $".bad-{DateTime.Now:yyyyMMddHHmmss}";
            try { File.Move(path, backup, overwrite: true); } catch { }

            var fallback = AppSettings.CreateDefault();
            fallback.DataRoot = Path.GetDirectoryName(path) ?? "data";
            System.Diagnostics.Debug.WriteLine(
                $"[AppSettings] 配置解析失败，已备份为 {backup}：{ex.Message}");
            return fallback;
        }
    }

    public static void Save(AppSettings settings, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 先写临时文件再替换，避免写一半断电导致配置全丢
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, path, overwrite: true);
    }
}
