using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VisionForge.Infrastructure.Integration;

/// <summary>收到的一次 HTTP 请求（只解析我们需要的那几样）。</summary>
public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public string Body { get; init; } = string.Empty;
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string ClientAddress { get; init; } = string.Empty;
}

/// <summary>要回给浏览器/上位机的响应。</summary>
public sealed class HttpResponse
{
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public string Body { get; set; } = string.Empty;

    public static HttpResponse Json(string json, int status = 200) =>
        new() { StatusCode = status, ContentType = "application/json; charset=utf-8", Body = json };

    public static HttpResponse Html(string html, int status = 200) =>
        new() { StatusCode = status, ContentType = "text/html; charset=utf-8", Body = html };

    public static HttpResponse Text(string text, int status = 200) =>
        new() { StatusCode = status, ContentType = "text/plain; charset=utf-8", Body = text };
}

/// <summary>
/// 极简 HTTP 服务器 —— 汇总终端看板就靠它对外提供页面和数据接口。
///
/// <para><b>为什么不用 <c>HttpListener</c>：</b>HttpListener 监听非 localhost 地址时，
/// Windows 要求先执行 <c>netsh http add urlacl</c>（管理员权限）。
/// 现场几十台机器都要配这个 ACL 太折腾；这里直接用 <see cref="TcpListener"/> 监听 0.0.0.0，
/// 普通用户权限即可，也不需要装任何东西。</para>
///
/// <para>只实现业务需要的部分：只支持 HTTP/1.1 的 GET/POST、按 Content-Length 读体、
/// 处理完就关连接（Connection: close）。够用、可读、没有隐藏行为。</para>
/// </summary>
public sealed class SimpleHttpServer : IDisposable
{
    /// <summary>请求体上限（看板只收小 JSON，2MB 已经很宽裕）。</summary>
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private readonly Func<HttpRequest, HttpResponse> _handler;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimpleHttpServer(Func<HttpRequest, HttpResponse> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>启动。端口被占用会抛异常，由调用方提示现场换端口。</summary>
    public void Start(int port)
    {
        if (_listener is not null) return;

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        _listener = listener;
        Port = port;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }

            // 每个连接独立处理：一个坏请求不能让整个看板停掉
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                using var stream = client.GetStream();

                var request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                if (request is null) return;

                HttpResponse response;
                try
                {
                    response = _handler(request);
                }
                catch (Exception ex)
                {
                    response = HttpResponse.Json(
                        "{\"ok\":false,\"message\":\"" + ex.Message.Replace("\"", "'") + "\"}", 500);
                }

                await WriteResponseAsync(stream, response, ct).ConfigureAwait(false);
            }
            catch
            {
                // 网络层面的偶发异常（对方提前断开等）直接忽略
            }
        }
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new StringBuilder();

        // 先读到空行为止（请求头结束）
        while (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
        {
            int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) return null;

            head.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (head.Length > 64 * 1024) return null;   // 头太大：直接无视
        }

        string headText = head.ToString();
        int headEnd = headText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        string[] lines = headText[..headEnd].Split("\r\n");
        if (lines.Length == 0) return null;

        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2) return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        string body = string.Empty;
        if (headers.TryGetValue("Content-Length", out var lenText) && int.TryParse(lenText, out int len) && len > 0)
        {
            if (len > MaxBodyBytes) return null;

            // 请求体可能有一部分已经在第一次读里带进来了
            string already = headText[(headEnd + 4)..];
            var bodyBytes = new List<byte>(len);
            bodyBytes.AddRange(Encoding.UTF8.GetBytes(already));

            while (bodyBytes.Count < len)
            {
                int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;
                bodyBytes.AddRange(buffer.Take(read));
            }

            body = Encoding.UTF8.GetString(bodyBytes.Take(len).ToArray());
        }

        return new HttpRequest
        {
            Method = parts[0].ToUpperInvariant(),
            Path = parts[1],
            Headers = headers,
            Body = body,
            ClientAddress = "unknown",
        };
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, CancellationToken ct)
    {
        byte[] bodyBytes = Encoding.UTF8.GetBytes(response.Body);

        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ')
          .Append(response.StatusCode == 200 ? "OK" : "ERROR").Append("\r\n");
        sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        // 允许跨域：看板页面可能被放在别的域/端口里打开
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Headers: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Connection: close\r\n\r\n");

        byte[] headBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(headBytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
