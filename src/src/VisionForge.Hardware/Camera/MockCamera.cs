using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Camera;

/// <summary>
/// 模拟相机 —— 不接任何硬件就能跑通全流程。
///
/// 这东西的价值容易被低估。实际项目里它至少干三件事：
///   1. <b>开发期</b>：开发者在没有相机的笔记本上就能写算法、调界面
///   2. <b>演示期</b>：给领导/客户演示时不用真机，不会被现场环境坑
///   3. <b>回归测试</b>：图像内容可控可重复，改算法后能验证判定逻辑没被改坏
///
/// 有了它，"硬件抽象层"才不是一句口号 —— 因为这里真的存在第二个实现。
/// 只有一种实现的接口不叫抽象。
/// </summary>
public sealed class MockCamera : ISceneCamera
{
    private readonly Random _random;
    private readonly int _frameWidth;
    private readonly int _frameHeight;
    private CancellationTokenSource? _grabCts;
    private Task? _grabLoop;
    private long _frameNumber;

    /// <summary>非 null 表示处于"测试场景"模式：出图内容由测试逻辑指定，不再随机。</summary>
    private IReadOnlyList<SceneBlob>? _scene;

    /// <summary>
    /// 默认场景（没调用 SetScene 时）的亮块位置。
    ///
    /// <para>第一帧算出来就固定下来：<b>画面必须稳定</b>。
    /// 以前是每帧随机撒点，结果是"教 OK 的那一帧"和"识别的那一帧"永远不一样，
    /// 按框学习/教示识别全部判不了 —— 模拟相机反而把"教了就认"这条主流程给堵死了。
    /// 真实相机看的是固定工位，本来就该稳定，这里改成稳定更贴近真实。</para>
    /// </summary>
    private List<SceneBlob>? _defaultScene;

    private int _sceneBlobCount = 1;
    private int _sceneBlobSize = 90;

    /// <summary>当前场景要画几个亮块。改了会重新生成默认布局。</summary>
    public int SceneBlobCount
    {
        get => _sceneBlobCount;
        set
        {
            if (_sceneBlobCount == value) return;
            _sceneBlobCount = value;
            _defaultScene = null;      // 布局跟着改，重新算一次
        }
    }

    /// <summary>亮块边长（像素）。改了会重新生成默认布局。</summary>
    public int SceneBlobSize
    {
        get => _sceneBlobSize;
        set
        {
            if (_sceneBlobSize == value) return;
            _sceneBlobSize = value;
            _defaultScene = null;
        }
    }

    /// <summary>是否叠加椒盐噪声，用来检验算法抗噪能力。</summary>
    public bool AddNoise { get; set; } = true;

    public MockCamera(
        int width = 1280,
        int height = 1024,
        int seed = 42,
        string displayName = "模拟相机(MockCamera)")
    {
        _frameWidth = width;
        _frameHeight = height;
        _random = new Random(seed);

        Info = new CameraInfo
        {
            Id = $"MOCK-{seed}",
            DisplayName = displayName,
            Vendor = "Mock",
            Model = $"Synthetic-{width}x{height}",
            SerialNumber = $"MOCK{seed:D6}",
        };
    }

    public CameraInfo Info { get; }

    public bool IsConnected { get; private set; }

    public bool IsGrabbing { get; private set; }

    // ---------------- ISceneCamera：测试场景 ----------------
    public IReadOnlyList<SceneBlob>? CurrentScene => _scene;

    public void SetScene(IReadOnlyList<SceneBlob> blobs)
    {
        _scene = blobs is null ? null : blobs.ToList();
    }

    public void ClearScene() => _scene = null;

    public event EventHandler<CameraFrame>? FrameArrived;

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        // 真实相机连接要 1~3 秒，模拟时也稍微延迟一下，
        // 免得开发者忘了连接是异步的、写出依赖"立即就绪"的代码。
        IsConnected = true;
        return Task.FromResult(true);
    }

    // 注意：这里以前写成同步方法里 StopGrabbingAsync().GetAwaiter().GetResult()，
    // 在 UI 线程上调用时会死锁（续体要回 UI 线程，而 UI 线程正卡在 GetResult 上）。
    // 改成真正的异步方法后，调用方 await 它，UI 线程始终不被阻塞。
    public async Task DisconnectAsync()
    {
        await StopGrabbingAsync().ConfigureAwait(false);
        IsConnected = false;
    }

    public async Task StartGrabbingAsync(CancellationToken ct = default)
    {
        if (!IsConnected) throw new InvalidOperationException("相机未连接");
        if (IsGrabbing) return;

        IsGrabbing = true;
        _grabCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _grabCts.Token;

        _grabLoop = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var frame = RenderFrame();
                    if (!token.IsCancellationRequested)
                    {
                        // 订阅方（界面）抛异常不能把取流循环带崩 ——
                        // 采集中断比少显示一帧严重得多
                        try { FrameArrived?.Invoke(this, frame); }
                        catch { /* 界面侧的问题不该影响取流 */ }
                    }
                    await Task.Delay(100, token);      // 模拟 10fps
                }
            }
            catch (OperationCanceledException) { /* 正常停止 */ }
            finally
            {
                IsGrabbing = false;
            }
        }, token);

        await Task.CompletedTask;
    }

    public async Task StopGrabbingAsync()
    {
        if (_grabCts is not null)
        {
            await _grabCts.CancelAsync().ConfigureAwait(false);
            _grabCts.Dispose();
            _grabCts = null;
        }

        if (_grabLoop is not null)
        {
            try { await _grabLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _grabLoop = null;
        }

        IsGrabbing = false;
    }

    public Task<CameraFrame?> GrabOnceAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (!IsConnected) return Task.FromResult<CameraFrame?>(null);

        // 模拟触发延时，让"软触发"这条路径在开发期也能被测到
        return Task.FromResult<CameraFrame?>(RenderFrame());
    }

    public Task ApplyParametersAsync(IReadOnlyDictionary<string, double> parameters)
    {
        // 模拟相机把参数记下来即可，不实际生效
        LastAppliedParameters = new Dictionary<string, double>(parameters);
        return Task.CompletedTask;
    }

    /// <summary>最近一次下发的相机参数，便于断言"配方参数有没有真的下到相机"。</summary>
    public IReadOnlyDictionary<string, double> LastAppliedParameters { get; private set; }
        = new Dictionary<string, double>();

    public IReadOnlyList<CameraParameterDescriptor> QueryParameters() => new[]
    {
        new CameraParameterDescriptor
        {
            Key = "ExposureTime", DisplayName = "曝光时间",
            Min = 10, Max = 100000, Default = 5000, Unit = "μs",
        },
        new CameraParameterDescriptor
        {
            Key = "Gain", DisplayName = "增益",
            Min = 0, Max = 24, Default = 0, Unit = "dB",
        },
        new CameraParameterDescriptor
        {
            Key = "FrameRate", DisplayName = "帧率",
            Min = 1, Max = 60, Default = 10, Unit = "fps",
        },
    };

    // ------------------------------------------------------------------
    private CameraFrame RenderFrame()
    {
        int stride = _frameWidth;                       // Mono8：每行字节数 == 宽度
        var data = new byte[stride * _frameHeight];

        // 背景：偏暗的均匀灰（模拟背光/暗场）
        Array.Fill(data, (byte)35);

        // 画一个"工件轮廓"边框，方便肉眼确认 ROI 位置
        for (int x = 0; x < _frameWidth; x++)
        {
            data[10 * stride + x] = 90;
            data[(_frameHeight - 11) * stride + x] = 90;
        }
        for (int y = 0; y < _frameHeight; y++)
        {
            data[y * stride + 10] = 90;
            data[y * stride + _frameWidth - 11] = 90;
        }

        if (_scene is not null)
        {
            // 测试场景模式：亮块按给定归一化坐标落位，画面完全确定 ——
            // 同一个场景跑一百次，判定结果都一样，测试才有意义。
            foreach (var blob in _scene)
            {
                int bx = (int)(blob.X * _frameWidth);
                int by = (int)(blob.Y * _frameHeight);
                int bw = (int)(blob.Width * _frameWidth);
                int bh = (int)(blob.Height * _frameHeight);

                int yEnd = Math.Min(_frameHeight, by + bh);
                int xEnd = Math.Min(_frameWidth, bx + bw);
                for (int y = Math.Max(0, by); y < yEnd; y++)
                {
                    for (int x = Math.Max(0, bx); x < xEnd; x++)
                    {
                        data[y * stride + x] = blob.Intensity;
                    }
                }
            }
        }
        else
        {
            // 默认：撒 N 个亮块（模拟工件/异物），给"随便看看画面"用。
            // 位置只在第一帧算一次，之后每帧画在同一个地方 —— 画面稳定，学习和识别才对得上。
            if (_defaultScene is null)
            {
                int margin = 120;
                _defaultScene = new List<SceneBlob>();

                for (int i = 0; i < SceneBlobCount; i++)
                {
                    int size = SceneBlobSize;
                    int bx = _random.Next(margin, Math.Max(margin + 1, _frameWidth - margin - size));
                    int by = _random.Next(margin, Math.Max(margin + 1, _frameHeight - margin - size));

                    _defaultScene.Add(new SceneBlob
                    {
                        X = (double)bx / _frameWidth,
                        Y = (double)by / _frameHeight,
                        Width = (double)size / _frameWidth,
                        Height = (double)size / _frameHeight,
                    });
                }
            }

            foreach (var blob in _defaultScene)
            {
                int bx = (int)(blob.X * _frameWidth);
                int by = (int)(blob.Y * _frameHeight);
                int bw = (int)(blob.Width * _frameWidth);
                int bh = (int)(blob.Height * _frameHeight);

                for (int y = Math.Max(0, by); y < Math.Min(_frameHeight, by + bh); y++)
                {
                    for (int x = Math.Max(0, bx); x < Math.Min(_frameWidth, bx + bw); x++)
                    {
                        data[y * stride + x] = blob.Intensity;   // 高亮
                    }
                }
            }
        }

        // 椒盐噪声：线扫/低照度场景的真实困扰
        if (AddNoise && _scene is null)
        {
            int noiseCount = _frameWidth * _frameHeight / 500;
            for (int i = 0; i < noiseCount; i++)
            {
                int idx = _random.Next(data.Length);
                data[idx] = (byte)(_random.Next(2) == 0 ? 0 : 255);
            }
        }

        return new CameraFrame(_frameWidth, _frameHeight, stride, PixelFormat.Mono8, data)
        {
            Timestamp = DateTime.Now,
            FrameNumber = ++_frameNumber,
        };
    }

    public void Dispose()
    {
        // Dispose 是同步接口，没法 await；但也不能在 UI 线程上直接等 ——
        // 关窗口时走的就是这条路，直接等会让窗口关不掉。
        // 丢到线程池上等，那里没有 SynchronizationContext，不会死锁。
        try { Task.Run(StopGrabbingAsync).GetAwaiter().GetResult(); } catch { /* 退出时忽略 */ }
        IsConnected = false;
    }
}
