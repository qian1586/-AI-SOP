using System.Globalization;
using System.Text;
using System.Text.Json;
using VisionForge.Core.Models;

namespace VisionForge.Infrastructure.Storage;

/// <summary>
/// 过程层规则配置的读写。
///
/// <para><b>为什么内部以 JSON 为准、同时支持 YAML：</b>方案（R10）要求规则用 YAML 承载
/// 并做加载校验 —— 这个要求的实质是"配置错误要暴露成一条明确的错误，
/// 而不是运行期莫名不动作"。本项目一直坚持零 NuGet 依赖（用内置的 System.Text.Json），
/// 所以做法是：JSON 作为唯一真相源，另外提供 YAML 子集的导出 / 导入，
/// 两种格式都走同一套 <see cref="ProcessRuleConfig.Validate"/>。</para>
///
/// <para>YAML 支持的范围就是导出时会产生的那一套：嵌套映射、<c>- </c> 列表项、
/// <c>[a, b]</c> 行内列表、布尔/数字/字符串标量、<c>#</c> 注释。超出范围会明确报错，
/// 不会静默忽略 —— 静默忽略才是配置类事故的根源。</para>
/// </summary>
public static class ProcessConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ==================================================================
    public static void SaveJson(ProcessRuleConfig config, string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static ProcessRuleConfig LoadJson(string path)
    {
        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ProcessRuleConfig>(json, JsonOptions)
                     ?? throw new InvalidDataException("规则文件内容为空");
        EnsureValid(config);
        return config;
    }

    /// <summary>按扩展名选择解析方式。</summary>
    public static ProcessRuleConfig Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("规则文件不存在：" + path, path);

        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".yaml" or ".yml" ? FromYaml(File.ReadAllText(path)) : LoadJson(path);
    }

    private static void EnsureValid(ProcessRuleConfig config)
    {
        var errors = config.Validate();
        if (errors.Count > 0)
            throw new InvalidDataException("规则校验未通过：" + string.Join("；", errors));
    }

    // ==================================================================
    // YAML 导出（格式与下面 FromYaml 支持的子集严格对应）
    // ==================================================================
    public static string ToYaml(ProcessRuleConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# VisionForge 过程层规则（方案 v1.3）");
        sb.AppendLine("# 坐标空间：归一化 (0~1)，相对画面；界面拖框即得，无需物理标定");
        sb.AppendLine("station:");
        sb.AppendLine("  name: " + Quote(c.StationName));
        sb.AppendLine("  camera_model: " + Quote(c.CameraModel));
        sb.AppendLine("  fps: " + Num(c.Fps));
        sb.AppendLine("  roi_space: normalized");
        sb.AppendLine();

        sb.AppendLine("targets:");
        foreach (var t in c.Targets)
        {
            sb.AppendLine("  - id: " + Quote(t.Id));
            sb.AppendLine("    name: " + Quote(t.Name));
            sb.AppendLine($"    box: [{Num(t.X)}, {Num(t.Y)}, {Num(t.Width)}, {Num(t.Height)}]");
            sb.AppendLine("    expect_count: " + t.ExpectedCount);
        }
        sb.AppendLine();

        sb.AppendLine("steps:");
        foreach (var s in c.Steps)
        {
            sb.AppendLine("  - seq: " + s.Seq);
            sb.AppendLine("    name: " + Quote(s.Name));
            sb.AppendLine("    targets: [" + string.Join(", ", s.Targets.Select(Quote)) + "]");
            sb.AppendLine("    nominal_time_ms: " + Num(s.NominalTimeMs));
            sb.AppendLine("    k_ms: " + Num(s.Kms));
        }
        sb.AppendLine();

        sb.AppendLine("rule:");
        sb.AppendLine("  required: [" + string.Join(", ", c.Required.Select(Quote)) + "]");
        sb.AppendLine("  order_enforced: " + Bool(c.OrderEnforced));
        sb.AppendLine("  K_ms: " + Num(c.Kms));
        sb.AppendLine("  T_ms: " + Num(c.TimeWindowMs));
        sb.AppendLine("  min_confidence: " + Num(c.MinConfidence));
        sb.AppendLine("  max_unreliable_ms: " + Num(c.MaxUnreliableMs));
        sb.AppendLine("  show_confidence: " + Bool(c.ShowConfidence));
        sb.AppendLine("  object_state:");
        sb.AppendLine("    model: " + Bool(c.ObjectState.Model));
        sb.AppendLine("    count: " + Bool(c.ObjectState.Count));
        sb.AppendLine("    orientation: " + Bool(c.ObjectState.Orientation));

        return sb.ToString();
    }

    // ==================================================================
    // YAML 导入（只支持本文件导出的那套子集）
    // ==================================================================
    public static ProcessRuleConfig FromYaml(string yaml)
    {
        var lines = SplitLines(yaml);
        int index = 0;
        object root = ParseBlock(lines, ref index, 0);

        if (root is not Dictionary<string, object> map)
            throw new InvalidDataException("YAML 根节点必须是映射（key: value）");

        var errors = new List<string>();
        var config = new ProcessRuleConfig();

        if (map.TryGetValue("station", out var stationObj) && stationObj is Dictionary<string, object> station)
        {
            config.StationName = Str(station, "name", config.StationName);
            config.CameraModel = Str(station, "camera_model", config.CameraModel);
            config.Fps = Dbl(station, "fps", config.Fps);
        }

        if (map.TryGetValue("targets", out var targetsObj) && targetsObj is List<object> targets)
        {
            config.Targets.Clear();
            foreach (var item in targets)
            {
                if (item is not Dictionary<string, object> t)
                {
                    errors.Add("targets 列表项必须是映射");
                    continue;
                }

                var target = new ProcessTarget
                {
                    Id = Str(t, "id", string.Empty),
                    Name = Str(t, "name", string.Empty),
                    ExpectedCount = (int)Dbl(t, "expect_count", 0),
                };

                if (t.TryGetValue("box", out var boxObj) && boxObj is List<object> box && box.Count == 4)
                {
                    target.X = ToDouble(box[0]);
                    target.Y = ToDouble(box[1]);
                    target.Width = ToDouble(box[2]);
                    target.Height = ToDouble(box[3]);
                }

                config.Targets.Add(target);
            }
        }

        if (map.TryGetValue("steps", out var stepsObj) && stepsObj is List<object> steps)
        {
            config.Steps.Clear();
            foreach (var item in steps)
            {
                if (item is not Dictionary<string, object> s)
                {
                    errors.Add("steps 列表项必须是映射");
                    continue;
                }

                var step = new ProcessStepDefinition
                {
                    Seq = (int)Dbl(s, "seq", 0),
                    Name = Str(s, "name", string.Empty),
                    NominalTimeMs = Dbl(s, "nominal_time_ms", 0),
                    Kms = Dbl(s, "k_ms", 0),
                };

                if (s.TryGetValue("targets", out var listObj) && listObj is List<object> list)
                    step.Targets = list.Select(ToStr).ToList();

                config.Steps.Add(step);
            }
        }

        if (map.TryGetValue("rule", out var ruleObj) && ruleObj is Dictionary<string, object> rule)
        {
            if (rule.TryGetValue("required", out var reqObj) && reqObj is List<object> req)
                config.Required = req.Select(ToStr).ToList();

            config.OrderEnforced = Bool(rule, "order_enforced", config.OrderEnforced);
            config.Kms = Dbl(rule, "K_ms", config.Kms);
            config.TimeWindowMs = Dbl(rule, "T_ms", config.TimeWindowMs);
            config.MinConfidence = Dbl(rule, "min_confidence", config.MinConfidence);
            config.MaxUnreliableMs = Dbl(rule, "max_unreliable_ms", config.MaxUnreliableMs);
            config.ShowConfidence = Bool(rule, "show_confidence", config.ShowConfidence);

            if (rule.TryGetValue("object_state", out var osObj) && osObj is Dictionary<string, object> os)
            {
                config.ObjectState.Model = Bool(os, "model", false);
                config.ObjectState.Count = Bool(os, "count", true);
                config.ObjectState.Orientation = Bool(os, "orientation", false);
            }
        }

        errors.AddRange(config.Validate());
        if (errors.Count > 0)
            throw new InvalidDataException("YAML 规则校验未通过：" + string.Join("；", errors));

        return config;
    }

    // ------------------------------------------------------------------
    private sealed class YamlLine
    {
        public int Indent { get; init; }
        public string Text { get; init; } = string.Empty;
    }

    private static List<YamlLine> SplitLines(string yaml)
    {
        var result = new List<YamlLine>();
        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw;

            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);

            if (string.IsNullOrWhiteSpace(line)) continue;

            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;

            result.Add(new YamlLine { Indent = indent, Text = line.Trim() });
        }
        return result;
    }

    /// <summary>按缩进递归解析一个块：是列表（以 "- " 开头）还是映射。</summary>
    private static object ParseBlock(List<YamlLine> lines, ref int index, int indent)
    {
        if (index >= lines.Count) return new Dictionary<string, object>();

        bool isList = lines[index].Text.StartsWith("- ");
        if (isList)
        {
            var list = new List<object>();
            while (index < lines.Count && lines[index].Indent >= indent && lines[index].Text.StartsWith("- "))
            {
                int itemIndent = lines[index].Indent;
                string itemText = lines[index].Text.Substring(2).Trim();
                index++;

                int colon = itemText.IndexOf(':');
                if (colon < 0)
                {
                    list.Add(ToScalar(itemText));
                    continue;
                }

                // "- key: value" 形式：把这一项后面的更深缩进行合并成一个映射
                var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                string key = itemText.Substring(0, colon).Trim();
                string valueText = itemText.Substring(colon + 1).Trim();
                map[key] = valueText.Length == 0 ? string.Empty : ToScalar(valueText);

                while (index < lines.Count && lines[index].Indent > itemIndent)
                {
                    var child = lines[index];
                    int childColon = child.Text.IndexOf(':');
                    if (childColon < 0) { index++; continue; }

                    string childKey = child.Text.Substring(0, childColon).Trim();
                    string childValue = child.Text.Substring(childColon + 1).Trim();
                    index++;

                    map[childKey] = childValue.Length == 0
                        ? ParseBlock(lines, ref index, lines.Count > index ? lines[index].Indent : child.Indent + 2)
                        : ToScalar(childValue);
                }

                list.Add(map);
            }
            return list;
        }

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (index < lines.Count && lines[index].Indent >= indent)
        {
            var line = lines[index];
            if (line.Indent > indent) break;      // 属于上层，交回去

            int colon = line.Text.IndexOf(':');
            if (colon < 0) { index++; continue; }

            string key = line.Text.Substring(0, colon).Trim();
            string valueText = line.Text.Substring(colon + 1).Trim();
            index++;

            if (valueText.Length > 0)
            {
                result[key] = ToScalar(valueText);
                continue;
            }

            int childIndent = index < lines.Count ? lines[index].Indent : indent + 2;
            result[key] = childIndent > indent ? ParseBlock(lines, ref index, childIndent)
                                               : new Dictionary<string, object>();
        }
        return result;
    }

    private static object ToScalar(string text)
    {
        text = text.Trim();

        if (text.StartsWith("[") && text.EndsWith("]"))
        {
            string inner = text.Substring(1, text.Length - 2).Trim();
            if (inner.Length == 0) return new List<object>();
            return inner.Split(',').Select(p => (object)ToScalar(p.Trim())).ToList();
        }

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '\'' && text[^1] == '\'')))
            return text.Substring(1, text.Length - 2);

        if (bool.TryParse(text, out bool b)) return b;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;

        return text;
    }

    private static string ToStr(object value) => value switch
    {
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => value?.ToString() ?? string.Empty,
    };

    private static double ToDouble(object value)
    {
        if (value is double d) return d;
        if (value is bool b) return b ? 1 : 0;
        return double.TryParse(ToStr(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : 0;
    }

    private static string Str(Dictionary<string, object> map, string key, string fallback) =>
        map.TryGetValue(key, out var v) ? ToStr(v) : fallback;

    private static double Dbl(Dictionary<string, object> map, string key, double fallback) =>
        map.TryGetValue(key, out var v) ? ToDouble(v) : fallback;

    private static bool Bool(Dictionary<string, object> map, string key, bool fallback)
    {
        if (!map.TryGetValue(key, out var v)) return fallback;
        if (v is bool b) return b;
        return bool.TryParse(ToStr(v), out bool r) ? r : fallback;
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Num(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";
}
