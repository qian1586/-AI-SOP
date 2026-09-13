namespace VisionForge.Core.Models;

/// <summary>参数控件类型。动态参数面板全靠它决定生成哪种输入控件。</summary>
public enum ParameterType
{
    /// <summary>数值 → TextBox + 范围校验</summary>
    Number = 0,

    /// <summary>布尔 → CheckBox</summary>
    Bool = 1,

    /// <summary>枚举 → ComboBox，选项来自 <see cref="ParameterDefinition.Options"/></summary>
    Enum = 2,

    /// <summary>文本 → TextBox</summary>
    Text = 3,
}

/// <summary>
/// 参数<b>定义</b>。由算法自己声明"我能接受哪些参数"。
///
/// 这正是"动态参数面板"能成立的前提：
///   算法声明 Definition → 配方存 Value → 界面把两者拼成可绑定字段 → DataTemplate 按 Type 选控件。
///   换算法就换一整套参数，主程序一行都不用改。
/// </summary>
public sealed class ParameterDefinition
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ParameterType Type { get; init; } = ParameterType.Number;
    public double Min { get; init; }
    public double Max { get; init; }
    public double Default { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    /// <summary>Type == Enum 时的候选项。</summary>
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();

    public double Clamp(double v) => Math.Clamp(v, Min, Max == 0 ? double.MaxValue : Max);
}

/// <summary>
/// 参数<b>值</b>。统一用字符串存放，理由：
///   1. JSON 序列化不需要类型歧视符，配方文件人可读可手改
///   2. 能和 TextBox 的 Text 属性直接双向绑定，不用写一堆转换器
///   3. 数值合法性在进入算法前统一校验，比在绑定层校验好排查
/// </summary>
public sealed class ParameterValue
{
    public string Key { get; set; } = string.Empty;
    public string RawValue { get; set; } = string.Empty;

    public ParameterValue() { }

    public ParameterValue(string key, string rawValue)
    {
        Key = key;
        RawValue = rawValue;
    }

    public double AsDouble(double fallback = 0) =>
        double.TryParse(RawValue, out var v) ? v : fallback;

    public int AsInt(int fallback = 0) =>
        int.TryParse(RawValue, out var v) ? v : fallback;

    public bool AsBool(bool fallback = false) =>
        bool.TryParse(RawValue, out var v) ? v : fallback;
}

/// <summary>感兴趣区域。归一化坐标（0~1），原点在画面左上角。</summary>
public sealed class RoiRegion
{
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 这一步"做完了"对应框里的哪种状态。
    ///
    /// <para><b>false（默认）= 有东西才算完成</b>：放料、装配、插入这类工序 ——
    /// 框里出现零件，说明这一步做到了。</para>
    ///
    /// <para><b>true = 东西被拿走才算完成</b>：取件、拿工具这类"拿走"工序 ——
    /// 框里的东西消失，才说明这一步做到了。</para>
    ///
    /// <para>为什么必须有这一条：现场第一次试就卡住了 ——
    /// "我从左上角拿一支笔、右上角拿一支笔、放到中间"是个典型的**拿走**流程，
    /// 但按默认规则"有东西=完成"，一开始两支笔都在，三个框全判完成、流程瞬间走完，
    /// 后面拿笔反而被当成违规。现场的原话是"没法跑一套流程"。</para>
    /// </summary>
    public bool DoneWhenAbsent { get; set; }

    /// <summary>按实际分辨率换算成像素矩形 (x, y, w, h)。</summary>
    public (int X, int Y, int W, int H) ToPixels(int imageWidth, int imageHeight) =>
        ((int)(X * imageWidth), (int)(Y * imageHeight),
         (int)(Width * imageWidth), (int)(Height * imageHeight));

    /// <summary>归一化坐标是否落在这个框里（界面点选"教哪个框"要用）。</summary>
    public bool Contains(double x, double y) =>
        x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    /// <summary>
    /// 界面上显示的坐标文案（如 "X=0.060 Y=0.100 W=0.240 H=0.330"）。
    ///
    /// 放在模型上而不是在 XAML 里用 StringFormat 拼，是因为
    /// StringFormat 的值里不能出现等号：<c>StringFormat=X={0:F3}</c>
    /// 会被 XAML 解析器当成参数名=值，直接编译报 MC3042。
    /// </summary>
    public string BoxText => $"X={X:F3}  Y={Y:F3}  W={Width:F3}  H={Height:F3}";

    public static RoiRegion FromPixels(int x, int y, int w, int h, int imageWidth, int imageHeight, string name = "")
        => new()
        {
            Name = name,
            X = (double)x / imageWidth,
            Y = (double)y / imageHeight,
            Width = (double)w / imageWidth,
            Height = (double)h / imageHeight,
        };
}

/// <summary>
/// 检测配方 —— 整套系统的核心实体。
///
/// 文章原话："系统以检测配方为核心，不同零件对应不同检测方案，
/// 配方里包含相机参数、算法阈值、ROI 区域，切换零件只需要加载对应配方。"
/// </summary>
public sealed class Recipe
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>配方名，如"W2448 壳体尺寸检测"。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>适用产品型号。</summary>
    public string ProductModel { get; set; } = string.Empty;

    /// <summary>使用哪个检测算法（对应 IInspectionAlgorithm.Key）。</summary>
    public string AlgorithmKey { get; set; } = string.Empty;

    /// <summary>绑定的相机 Id。</summary>
    public string CameraId { get; set; } = string.Empty;

    /// <summary>
    /// 相机绑定方案标识（如"工位1-上视-GigE"）。
    ///
    /// 文章里那条"配方与相机绑定方案一致才能加载，防止参数错配"就是靠这个字段实现的：
    /// 配方里的相机参数、ROI 都是针对某个固定机位标定的，
    /// 如果换个机位打开同一个配方，阈值和 ROI 全部失效 —— 那比报错更危险，会静默误判。
    /// </summary>
    public string CameraBindingKey { get; set; } = string.Empty;

    /// <summary>相机参数（曝光、增益……），键名由 ICamera.QueryParameters 声明。</summary>
    public Dictionary<string, double> CameraParameters { get; set; } = new();

    /// <summary>算法参数值。</summary>
    public List<ParameterValue> AlgorithmParameters { get; set; } = new();

    /// <summary>ROI 区域。</summary>
    public List<RoiRegion> Rois { get; set; } = new();

    /// <summary>
    /// 现场底图路径（可选）。
    ///
    /// <para>用途：没有相机、或者想用现场照片来标位置时，导一张实拍图进软件，
    /// 在上面直接框出"抓取位置"——框完就能当判定区域用。
    /// 存的是路径而不是图片内容，配方文件保持轻量。</para>
    /// </summary>
    public string? SceneImagePath { get; set; }

    public string Version { get; set; } = "1.0.0";
    public string Remark { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public Recipe Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<Recipe>(json)!;
    }

    /// <summary>
    /// 加载前校验。返回空列表表示通过。
    ///
    /// 校验放在这里而不是界面里，是因为配方文件可能被人手工改过、
    /// 也可能是上一版软件存的。必须在进入检测流程之前拦住。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Id))
            errors.Add("配方 Id 为空");
        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("配方名称为空");
        if (string.IsNullOrWhiteSpace(AlgorithmKey))
            errors.Add("未指定检测算法");
        if (string.IsNullOrWhiteSpace(CameraId))
            errors.Add("未绑定相机");

        foreach (var roi in Rois)
        {
            if (roi.Width <= 0 || roi.Height <= 0)
                errors.Add($"ROI「{roi.Name}」宽或高为 0");
            if (roi.X < 0 || roi.Y < 0 || roi.X + roi.Width > 1.0001 || roi.Y + roi.Height > 1.0001)
                errors.Add($"ROI「{roi.Name}」超出画面范围（坐标为归一化值，应为 0~1）");
        }

        return errors;
    }

    public override string ToString() => $"{Name} [{ProductModel}]";
}
