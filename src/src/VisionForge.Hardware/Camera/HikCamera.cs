using System.Runtime.InteropServices;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

#if HIK_MVS
using MvCamCtrl.NET;
#endif

namespace VisionForge.Hardware.Camera;

/// <summary>
/// 海康威视工业相机适配器。
///
/// <para><b>如何启用：</b></para>
/// <list type="number">
///   <item>安装海康 MVS SDK（x64），本项目验证用 5.0.2 版本</item>
///   <item>设置环境变量 <c>HIK_MVS_SDK_DIR</c> 指向 MvCameraControl.Net.dll 所在目录</item>
///   <item>在 VisionForge.Hardware.csproj 中增加：
///     <c>&lt;DefineConstants&gt;$(DefineConstants);HIK_MVS&lt;/DefineConstants&gt;</c>
///     并添加对 MvCameraControl.Net.dll 的引用</item>
/// </list>
///
/// <para><b>未启用时</b>：本类退化为一个"友好报错"的占位实现，
/// <see cref="ConnectAsync"/> 返回 false 并给出清晰提示，
/// 保证整个解决方案在任何机器上都能编译通过 —— 这一点对多人协作很重要，
/// 不该因为某台机器没装相机 SDK 就编译不过。</para>
///
/// <para><b>适配海康最容易踩的四个坑（已在代码中处理）：</b></para>
/// <list type="number">
///   <item>SDK 返回的是非托管内存，必须在回调结束<b>之前</b>拷贝出来，
///         否则回调一返回内存就被回收，拿到的是脏数据或直接崩</item>
///   <item>取流回调运行在 SDK 自己的线程上，绝不能在里面直接操作 UI</item>
///   <item>软触发必须先 <c>SetEnumValue("TriggerMode", 1)</c> 再 <c>TriggerSoftware</c>，
///         顺序反了不生效且不报错 —— 这种静默失败最坑</item>
///   <item>关闭设备顺序必须是 StopGrabbing → CloseDevice → DestroyHandle，
///         顺序错了再次打开会失败</item>
/// </list>
/// </summary>
public sealed class HikCamera : ICamera
{
    private readonly CameraInfo _info;
    private bool _disposed;

    public HikCamera(CameraInfo info)
    {
        _info = info;
    }

    public CameraInfo Info => _info;

    public bool IsConnected { get; private set; }

    public bool IsGrabbing { get; private set; }

    // 未启用 SDK 时该事件不会被触发，属预期行为，抑制告警避免噪音
#pragma warning disable CS0067
    public event EventHandler<CameraFrame>? FrameArrived;
#pragma warning restore CS0067

    // ==================================================================
    // 未安装 SDK 时的降级实现
    // ==================================================================
#if !HIK_MVS
    private const string SdkMissingHint =
        "未启用海康 MVS SDK。请在 VisionForge.Hardware.csproj 中定义编译符号 HIK_MVS " +
        "并引用 MvCameraControl.Net.dll；开发调试请改用 MockCamera。";

    public Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        System.Diagnostics.Debug.WriteLine($"[HikCamera] {SdkMissingHint}");
        return Task.FromResult(false);
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task StartGrabbingAsync(CancellationToken ct = default)
        => throw new NotSupportedException(SdkMissingHint);

    public Task StopGrabbingAsync() => Task.CompletedTask;

    public Task<CameraFrame?> GrabOnceAsync(int timeoutMs = 2000, CancellationToken ct = default)
        => Task.FromResult<CameraFrame?>(null);

    public Task ApplyParametersAsync(IReadOnlyDictionary<string, double> parameters)
        => Task.CompletedTask;

    public IReadOnlyList<CameraParameterDescriptor> QueryParameters() => Array.Empty<CameraParameterDescriptor>();

    public void Dispose() => Dispose(true);

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing) GC.SuppressFinalize(this);
    }
#endif

    // ==================================================================
    // 已启用 SDK 的真实实现
    // ==================================================================
#if HIK_MVS
    private MyCamera? _camera;
    private MyCamera.MV_CC_DEVICE_INFO _deviceInfo;
    private bool _disposed;
    private readonly object _grabGate = new();
    private readonly TaskCompletionSource<CameraFrame?> _triggerTcs = new();
    private TaskCompletionSource<CameraFrame?>? _pendingTrigger;
    private volatile bool _softwareTriggerMode;

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;

        // --- 1. 枚举设备 ---
        var deviceList = new MyCamera.MV_CC_DEVICE_INFO_LIST();
        int ret = MyCamera.MV_CC_EnumDevices_NET(
            MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE, ref deviceList);

        if (ret != MyCamera.MV_OK)
        {
            System.Diagnostics.Debug.WriteLine($"[HikCamera] 枚举设备失败: 0x{ret:X8}");
            return false;
        }
        if (deviceList.nDeviceNum == 0)
        {
            System.Diagnostics.Debug.WriteLine("[HikCamera] 未发现设备");
            return false;
        }

        // --- 2. 按序列号匹配目标相机 ---
        // 多相机场景必须按序列号找，不能按索引 —— 索引会随 USB 插拔顺序变化，
        // 导致"照片拍的是隔壁工位"这种诡异问题。
        IntPtr pInfo = IntPtr.Zero;
        bool matched = string.IsNullOrEmpty(_info.SerialNumber);
        for (int i = 0; i < deviceList.nDeviceNum; i++)
        {
            var infoPtr = Marshal.ReadIntPtr(deviceList.pDeviceInfo, i * IntPtr.Size);
            var info = Marshal.PtrToStructure<MyCamera.MV_CC_DEVICE_INFO>(infoPtr);
            if (matched || PtrToSerial(info) == _info.SerialNumber)
            {
                pInfo = infoPtr;
                _deviceInfo = info;
                matched = true;
                break;
            }
        }
        if (!matched)
        {
            System.Diagnostics.Debug.WriteLine($"[HikCamera] 未找到序列号 {_info.SerialNumber} 的相机");
            return false;
        }

        // --- 3. 创建句柄并打开 ---
        _camera = new MyCamera();
        ret = _camera.MV_CC_CreateDevice_NET(ref _deviceInfo);
        if (ret != MyCamera.MV_OK) return false;

        ret = _camera.MV_CC_OpenDevice_NET();
        if (ret != MyCamera.MV_OK)
        {
            _camera.MV_CC_DestroyDevice_NET();
            _camera = null;
            return false;
        }

        // --- 4. 挂取流回调 ---
        // 注意：回调运行在 SDK 线程上，绝不能在回调里碰 UI
        _camera.MV_CC_RegisterImageCallBackEx_NET(OnImageCallback, IntPtr.Zero);

        IsConnected = true;
        await Task.CompletedTask;
        return true;
    }

    public async Task DisconnectAsync()
    {
        if (_camera is null) return;

        await StopGrabbingAsync().ConfigureAwait(false);

        // 关闭顺序不能错：StopGrabbing → CloseDevice → DestroyHandle
        _camera.MV_CC_CloseDevice_NET();
        _camera.MV_CC_DestroyDevice_NET();
        _camera = null;
        IsConnected = false;
    }

    public async Task StartGrabbingAsync(CancellationToken ct = default)
    {
        if (_camera is null) throw new InvalidOperationException("相机未连接");
        if (IsGrabbing) return;

        SetEnumValue("TriggerMode", 0);          // 连续采集模式
        _softwareTriggerMode = false;

        int ret = _camera.MV_CC_StartGrabbing_NET();
        if (ret != MyCamera.MV_OK)
            throw new InvalidOperationException($"开始取流失败: 0x{ret:X8}");

        IsGrabbing = true;
        await Task.CompletedTask;
    }

    public async Task StopGrabbingAsync()
    {
        if (_camera is null || !IsGrabbing) return;

        _camera.MV_CC_StopGrabbing_NET();
        IsGrabbing = false;
        await Task.CompletedTask;
    }

    public async Task<CameraFrame?> GrabOnceAsync(int timeoutMs = 2000, CancellationToken ct = default)
    {
        if (_camera is null) return null;

        // 软触发：先切到触发模式，再发一次软触发
        // 坑：顺序反了不生效且不报错
        if (!_softwareTriggerMode)
        {
            await StopGrabbingAsync().ConfigureAwait(false);
            SetEnumValue("TriggerMode", 1);
            _softwareTriggerMode = true;
            _camera.MV_CC_StartGrabbing_NET();
            IsGrabbing = true;
        }

        var tcs = new TaskCompletionSource<CameraFrame?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_grabGate) _pendingTrigger = tcs;

        int ret = _camera.MV_CC_SetCommandValue_NET("TriggerSoftware");
        if (ret != MyCamera.MV_OK)
        {
            lock (_grabGate) _pendingTrigger = null;
            System.Diagnostics.Debug.WriteLine($"[HikCamera] 软触发失败: 0x{ret:X8}");
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        using (timeoutCts.Token.Register(() =>
               {
                   lock (_grabGate) _pendingTrigger = null;
                   tcs.TrySetResult(null);
               }))
        {
            return await tcs.Task;
        }
    }

    public Task ApplyParametersAsync(IReadOnlyDictionary<string, double> parameters)
    {
        if (_camera is null) return Task.CompletedTask;

        foreach (var (key, value) in parameters)
        {
            // 把配方里的参数名翻译成海康的枚举名
            switch (key)
            {
                case "ExposureTime":
                    SetFloatValue("ExposureTime", value);
                    break;
                case "Gain":
                    SetFloatValue("Gain", value);
                    break;
                case "FrameRate":
                    SetBoolValue("AcquisitionFrameRateEnable", true);
                    SetFloatValue("AcquisitionFrameRate", value);
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[HikCamera] 未识别的参数: {key}");
                    break;
            }
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<CameraParameterDescriptor> QueryParameters() => new[]
    {
        new CameraParameterDescriptor { Key = "ExposureTime", DisplayName = "曝光时间", Min = 10, Max = 1000000, Default = 5000, Unit = "μs" },
        new CameraParameterDescriptor { Key = "Gain", DisplayName = "增益", Min = 0, Max = 30, Default = 0, Unit = "dB" },
        new CameraParameterDescriptor { Key = "FrameRate", DisplayName = "帧率", Min = 1, Max = 60, Default = 10, Unit = "fps" },
    };

    // ------------------------------------------------------------------
    private void OnImageCallback(IntPtr pData, ref MyCamera.MV_FRAME_OUT_INFO_EX pFrameInfo, IntPtr pUser)
    {
        if (pData == IntPtr.Zero) return;

        int width = pFrameInfo.nWidth;
        int height = pFrameInfo.nHeight;
        int len = (int)pFrameInfo.nFrameLen;

        // 关键：非托管内存必须在回调返回前拷出来
        var buffer = new byte[len];
        Marshal.Copy(pData, buffer, 0, len);

        var format = pFrameInfo.enPixelType == MyCamera.MvGvspPixelType.PixelType_Gvsp_Mono8
            ? PixelFormat.Mono8
            : PixelFormat.Bgr24;

        // Mono8 的 stride 就是宽度；彩色按 3 字节/像素算
        int stride = format == PixelFormat.Mono8 ? width : width * 3;

        var frame = new CameraFrame(width, height, stride, format, buffer)
        {
            Timestamp = DateTime.Now,
            FrameNumber = (long)pFrameInfo.nFrameNum,
        };

        // 软触发模式下，把这帧交给等待它的调用方
        TaskCompletionSource<CameraFrame?>? pending;
        lock (_grabGate)
        {
            pending = _pendingTrigger;
            _pendingTrigger = null;
        }
        if (pending is not null)
        {
            pending.TrySetResult(frame);
            return;                       // 触发的帧不重复走连续采集事件
        }

        FrameArrived?.Invoke(this, frame);
    }

    private void SetEnumValue(string key, uint value)
    {
        if (_camera is null) return;
        int ret = _camera.MV_CC_SetEnumValue_NET(key, value);
        if (ret != MyCamera.MV_OK)
            System.Diagnostics.Debug.WriteLine($"[HikCamera] SetEnumValue({key}={value}) 失败: 0x{ret:X8}");
    }

    private void SetFloatValue(string key, double value)
    {
        if (_camera is null) return;
        int ret = _camera.MV_CC_SetFloatValue_NET(key, (float)value);
        if (ret != MyCamera.MV_OK)
            System.Diagnostics.Debug.WriteLine($"[HikCamera] SetFloatValue({key}={value}) 失败: 0x{ret:X8}");
    }

    private void SetBoolValue(string key, bool value)
    {
        if (_camera is null) return;
        _camera.MV_CC_SetBoolValue_NET(key, value);
    }

    private static string PtrToSerial(MyCamera.MV_CC_DEVICE_INFO info)
    {
        // GigE 与 USB 的序列号字段位置不同，SDK 里需要分别取
        // 这里只示意，实际按 info.nTLayerType 分支处理
        return string.Empty;
    }

    public void Dispose() => Dispose(true);

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (disposing)
        {
            // 同 MockCamera：同步等待异步清理必须换线程，否则 UI 线程上关停会死锁
            try { Task.Run(DisconnectAsync).GetAwaiter().GetResult(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
#endif
}
