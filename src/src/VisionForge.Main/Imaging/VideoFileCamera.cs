using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Main.Imaging;

/// <summary>
/// 把一段录像当相机用 —— 这是"没有现场硬件也能把整套流程跑起来"的关键。
///
/// <para><b>为什么要有它：</b>现场调试最常见的情况是"人不在工位、相机没装好、但手上有一段录像"。
/// 有了它，框 ROI、教 OK/NG、看计时、留证据、看趋势图全都能在录像上先跑通；
/// 真机接上来之后，上层一行都不用改（因为对上层来说它就是一个 <see cref="ICamera"/>）。</para>
///
/// <para><b>实现方式：</b>WPF 的 <see cref="MediaPlayer"/> 负责解码（不引任何第三方库），
/// 用 DispatcherTimer 按 ~10fps 把"当前这一帧"渲染成位图，再转成 Core 层的
/// <see cref="CameraFrame"/> 抛出去 —— 跟真相机的出帧路径完全一致。</para>
///
/// <para>刻意<b>不</b>实现 ISceneCamera：录像的画面不能被"设场景"，
/// 实现了只会让界面多出"模拟放料"这类点了没用的按钮。</para>
/// </summary>
public sealed class VideoFileCamera : ICamera
{
    /// <summary>出帧宽度上限。识别只需要看清框里的东西，不需要原分辨率。</summary>
    private const int MaxWidth = 1280;

    /// <summary>出帧节奏（毫秒）。10fps 与模拟相机一致，识别与界面都够用。</summary>
    private const int FrameIntervalMs = 100;

    private readonly string _path;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private long _frameNumber;
    private bool _disposed;
    private int _outWidth;
    private int _outHeight;

    public VideoFileCamera(string path)
    {
        _path = path;

        Info = new CameraInfo
        {
            Id = "VIDEO-" + Path.GetFileNameWithoutExtension(path),
            DisplayName = "录像 · " + Path.GetFileName(path),
            Vendor = "Video",
            Model = "VideoFile",
            SerialNumber = Path.GetFileName(path),
        };
    }

    public CameraInfo Info { get; }

    public bool IsConnected { get; private set; }

    public bool IsGrabbing { get; private set; }

    /// <summary>录像总时长（秒）。打开成功后才有效。</summary>
    public double DurationSeconds { get; private set; }

    /// <summary>当前播放位置（秒）。</summary>
    public double PositionSeconds => _player?.Position.TotalSeconds ?? 0;

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying { get; private set; }

    public event EventHandler<CameraFrame>? FrameArrived;

    // ------------------------------------------------------------------
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return false;

        var player = new MediaPlayer { ScrubbingEnabled = true, Volume = 0 };
        var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaFailed += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine("[VideoFileCamera] 打开失败：" + e.ErrorException?.Message);
            opened.TrySetResult(false);
        };

        player.Open(new Uri(_path));
        _player = player;

        // 等它打开：最多 15 秒（网络盘上的大文件可能慢）
        var completed = await Task.WhenAny(opened.Task, Task.Delay(15000, ct));
        if (completed != opened.Task || !opened.Task.Result)
        {
            Dispose();
            return false;
        }

        int naturalW = player.NaturalVideoWidth;
        int naturalH = player.NaturalVideoHeight;
        if (naturalW <= 0 || naturalH <= 0)
        {
            Dispose();
            return false;
        }

        // 等比缩到 MaxWidth 以内
        double scale = naturalW > MaxWidth ? (double)MaxWidth / naturalW : 1.0;
        _outWidth = Math.Max(2, (int)(naturalW * scale));
        _outHeight = Math.Max(2, (int)(naturalH * scale));

        var duration = player.NaturalDuration;
        DurationSeconds = duration.HasTimeSpan ? duration.TimeSpan.TotalSeconds : 0;

        IsConnected = true;
        return true;
    }

    /// <summary>断开：停止出帧、暂停播放（MediaPlayer 留着，允许再连）。</summary>
    public Task DisconnectAsync()
    {
        _timer?.Stop();
        _timer = null;
        _player?.Pause();

        IsConnected = false;
        IsGrabbing = false;
        IsPlaying = false;
        return Task.CompletedTask;
    }

    public Task StartGrabbingAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _player is null) throw new InvalidOperationException("录像未打开");
        if (_timer is not null) return Task.CompletedTask;

        IsGrabbing = true;
        _player.Play();
        IsPlaying = true;

        // DispatcherTimer：出帧必须在 UI 线程上（MediaPlayer 渲染到 DrawingVisual 要 UI 线程）
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(FrameIntervalMs),
        };
        _timer.Tick += (_, _) => PumpFrame();
        _timer.Start();

        return Task.CompletedTask;
    }

    public Task StopGrabbingAsync()
    {
        _timer?.Stop();
        _timer = null;
        _player?.Pause();
        IsPlaying = false;
        IsGrabbing = false;
        return Task.CompletedTask;
    }

    public Task<CameraFrame?> GrabOnceAsync(int timeoutMs = 2000, CancellationToken ct = default)
        => Task.FromResult(IsConnected ? RenderFrame() : null);

    public Task ApplyParametersAsync(IReadOnlyDictionary<string, double> parameters) => Task.CompletedTask;

    public IReadOnlyList<CameraParameterDescriptor> QueryParameters() => Array.Empty<CameraParameterDescriptor>();

    // ---- 播放控制（界面上"播放/暂停/重开"用） ----
    public void Play()
    {
        _player?.Play();
        IsPlaying = true;
    }

    public void Pause()
    {
        _player?.Pause();
        IsPlaying = false;
    }

    public void Restart()
    {
        if (_player is null) return;
        _player.Position = TimeSpan.Zero;
        _player.Play();
        IsPlaying = true;
    }

    /// <summary>
    /// 停止播放并回到开头（**不关闭文件**，仍然是这台"相机"）。
    ///
    /// <para>和 <see cref="Pause"/> 的区别：暂停是"停在这一帧"，
    /// 停止是"停下来并倒回第 1 帧"，下一次播放从头发起。
    /// 现场要的是"点一下停住、再点一下从头来"，所以给了独立的停止。</para>
    /// </summary>
    public void Stop()
    {
        if (_player is null) return;
        _player.Pause();
        _player.Position = TimeSpan.Zero;
        IsPlaying = false;
    }

    // ------------------------------------------------------------------
    private void PumpFrame()
    {
        var frame = RenderFrame();
        if (frame is null) return;

        try { FrameArrived?.Invoke(this, frame); }
        catch { /* 界面侧的问题不该影响出帧 */ }

        // 播完了就停住（不自动循环：现场要的是"看一遍这一件"，循环反而看不清）
        if (DurationSeconds > 0 && PositionSeconds >= DurationSeconds - 0.05)
        {
            _player?.Pause();
            IsPlaying = false;
        }
    }

    /// <summary>
    /// 把"当前这一帧"渲染成 <see cref="CameraFrame"/>。
    ///
    /// <para>用 DrawingVisual + RenderTargetBitmap 取帧，再用 FormatConvertedBitmap
    /// 转成 Bgr24 —— 全程原生实现，没有逐像素的 C# 循环（那在 10fps 下会明显吃 CPU）。</para>
    /// </summary>
    private CameraFrame? RenderFrame()
    {
        var player = _player;
        if (player is null || _outWidth <= 0) return null;

        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawVideo(player, new System.Windows.Rect(0, 0, _outWidth, _outHeight));
            }

            var rtb = new RenderTargetBitmap(_outWidth, _outHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);

            var converted = new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0);
            converted.Freeze();

            int stride = _outWidth * 3;
            var data = new byte[stride * _outHeight];
            converted.CopyPixels(data, stride, 0);

            // 这里必须写全限定名：本文件同时 using 了 System.Windows.Media（WPF 也有个
            // 同名的 PixelFormat 结构体），不写全就是 CS0104 二义性错误。
            return new CameraFrame(_outWidth, _outHeight, stride,
                                   VisionForge.Core.Models.PixelFormat.Bgr24, data)
            {
                Timestamp = DateTime.Now,
                FrameNumber = ++_frameNumber,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[VideoFileCamera] 取帧失败：" + ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Stop();
        _timer = null;

        // MediaPlayer.Close 必须回到它创建时所在的线程；这里通常在 UI 线程上，直接关。
        try
        {
            if (_player is not null)
            {
                _player.Pause();
                _player.Close();
            }
        }
        catch { /* 退出时忽略 */ }

        _player = null;
        IsConnected = false;
        IsGrabbing = false;
        IsPlaying = false;
    }
}
