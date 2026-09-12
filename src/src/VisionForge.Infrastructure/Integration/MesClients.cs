using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Infrastructure.Config;

namespace VisionForge.Infrastructure.Integration;

/// <summary>
/// HTTP/JSON 的 MES 客户端 —— 覆盖绝大多数 MES 的接入方式。
///
/// <para>上传格式就是 <see cref="MesUploadPayload"/> 的 JSON（字段固定、时间字符串），
/// 请求头带 <c>Authorization: Bearer &lt;token&gt;</c>（配置里可留空）。
/// MES 侧只要能接一个 POST 接口，就能把数据收下。</para>
///
/// <para><b>失败分类很重要：</b>网络错误、超时、5xx 值得重试（进队列）；
/// 4xx 说明是我们发的东西对方不认（字段/权限问题），重试一百次也没用 ——
/// 直接标记失败并写日志，让人去查，而不是把队列堵死。</para>
/// </summary>
public sealed class HttpMesClient : IMesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MesOptions _options;
    private readonly ILogger? _log;
    private readonly HttpClient _http;

    public HttpMesClient(MesOptions options, ILogger? log = null)
    {
        _options = options;
        _log = log;

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSec, 1, 60)),
        };
    }

    public string Name => "HTTP/JSON · " + _options.EndpointUrl;

    public bool IsEnabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.EndpointUrl);

    public async Task<MesUploadResult> UploadAsync(MesUploadPayload payload, CancellationToken ct = default)
    {
        if (!IsEnabled) return MesUploadResult.Fail(0, "MES 上传未启用", retry: false);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            };

            if (!string.IsNullOrWhiteSpace(_options.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _log?.Info($"MES 上传成功（{code}）：件号 {payload.PieceId} {payload.Verdict}");
                return MesUploadResult.Ok($"上传成功（HTTP {code}）");
            }

            // 4xx：参数/权限问题，重试无意义；5xx：对方系统故障，值得重试
            bool retry = code >= 500;
            string detail = body.Length > 200 ? body[..200] : body;
            _log?.Warn($"MES 上传失败（HTTP {code}，{(retry ? "将重试" : "不重试")}）：" +
                       detail);
            return MesUploadResult.Fail(code, $"HTTP {code}", retry);
        }
        catch (OperationCanceledException)
        {
            return MesUploadResult.Fail(0, "上传超时", retry: true);
        }
        catch (Exception ex)
        {
            _log?.Warn("MES 上传异常（将重试）：" + ex.Message);
            return MesUploadResult.Fail(0, ex.Message, retry: true);
        }
    }

    /// <summary>连通性自检：发一条明显的探测数据（MES 侧应能识别并忽略）。</summary>
    public Task<MesUploadResult> TestAsync(CancellationToken ct = default)
        => UploadAsync(new MesUploadPayload
        {
            StationCode = "TEST",
            PieceId = "TEST-" + DateTime.Now.ToString("HHmmss"),
            Verdict = "OK",
            JudgementReason = "VisionForge 连通性探测（可忽略）",
        }, ct);

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 落盘版 MES 客户端：把完全一样的 JSON 写到本地目录。
///
/// <para>用在两个阶段：① 还没拿到 MES 接口地址时，先让数据落盘不丢；
/// ② 对接联调时，把文件直接发给 MES 供应商确认字段。</para>
/// </summary>
public sealed class FileMesClient : IMesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _directory;
    private readonly ILogger? _log;

    public FileMesClient(string directory, ILogger? log = null)
    {
        _directory = directory;
        _log = log;
    }

    public string Name => "本地文件 · " + _directory;

    public bool IsEnabled => true;

    public Task<MesUploadResult> UploadAsync(MesUploadPayload payload, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            string path = Path.Combine(_directory, $"{payload.FinishedAt.Replace(":", "").Replace(" ", "-")}" +
                                                  $"-{payload.StationCode}-{payload.PieceId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions),
                              new UTF8Encoding(false));
            return Task.FromResult(MesUploadResult.Ok("已写入本地文件"));
        }
        catch (Exception ex)
        {
            _log?.Warn("MES 本地落盘失败：" + ex.Message);
            return Task.FromResult(MesUploadResult.Fail(0, ex.Message, retry: true));
        }
    }

    public Task<MesUploadResult> TestAsync(CancellationToken ct = default) =>
        Task.FromResult(MesUploadResult.Ok("本地文件模式：" + _directory));
}
