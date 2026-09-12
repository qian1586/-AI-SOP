using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;

namespace VisionForge.Hardware.Camera;

/// <summary>
/// 相机工厂 —— 按品牌创建对应实现。
///
/// 上层只认 <see cref="ICamera"/>，永远不知道具体是海康还是大恒。
/// 新增一个相机品牌 = 写一个 ICamera 实现 + 在下面的 switch 里加一行。
/// </summary>
public static class CameraFactory
{
    /// <summary>当前进程内已创建的相机实例（按 Id 索引），避免重复打开同一台设备。</summary>
    private static readonly Dictionary<string, ICamera> Instances = new();
    private static readonly object Gate = new();

    public static ICamera Create(CameraInfo info)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));

        lock (Gate)
        {
            if (Instances.TryGetValue(info.Id, out var existing))
                return existing;

            ICamera camera = info.Vendor?.ToLowerInvariant() switch
            {
                "mock" => new MockCamera(
                    seed: Math.Abs(info.Id.GetHashCode() % 1000),
                    displayName: info.DisplayName),

                "hikvision" or "hik" or "海康" => new HikCamera(info),

                // 预留：大恒、Basler、映美精……
                // "daheng" => new DahengCamera(info),

                _ => throw new NotSupportedException(
                    $"不支持的相机品牌「{info.Vendor}」。可选：Mock / Hikvision。"),
            };

            Instances[info.Id] = camera;
            return camera;
        }
    }

    /// <summary>释放并移除某个相机实例。</summary>
    public static void Release(string cameraId)
    {
        lock (Gate)
        {
            if (Instances.Remove(cameraId, out var cam))
                cam.Dispose();
        }
    }

    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (var cam in Instances.Values) cam.Dispose();
            Instances.Clear();
        }
    }
}
