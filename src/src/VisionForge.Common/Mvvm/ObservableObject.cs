using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VisionForge.Common.Mvvm;

/// <summary>
/// MVVM 的基石：可通知属性变更的对象。
///
/// 文章里用的是 CommunityToolkit.Mvvm。这里手写一份，好处有两个：
///   1. 零 NuGet 依赖，离线可编译
///   2. 逻辑看得见，新人不需要先学一套源生成器语法
/// 等项目稳定了再换 CommunityToolkit.Mvvm 的 [ObservableProperty] 也不迟。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 赋值并在值真正变化时通知。返回是否发生了变化。
    /// 这是减少无效刷新、避免 UI 抖动最常用的手法。
    /// </summary>
    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
