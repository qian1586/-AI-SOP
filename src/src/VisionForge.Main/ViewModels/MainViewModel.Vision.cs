using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VisionForge.Common.Events;
using VisionForge.Common.Mvvm;
using VisionForge.Core.Interfaces;
using VisionForge.Core.Models;
using VisionForge.Core.Services;
using VisionForge.Hardware.Camera;
using VisionForge.Hardware.Simulation;
using VisionForge.Hardware.Vision;
using VisionForge.Infrastructure.Config;
using VisionForge.Infrastructure.Storage;
using VisionForge.Main.Imaging;

namespace VisionForge.Main.ViewModels;

public sealed partial class MainViewModel
{
    private ProcessViolation? _selectedViolation;
    private BitmapSource? _selectedEvidenceImage;

    /// <summary>画面抓拍：把当前画面存一张图（现场取证 / 存档用）。</summary>
    private void TakeSnapshot()
    {
        var preview = PreviewImage;
        if (preview is null)
        {
            StatusMessage = "没有画面可抓拍：先点「连接相机」";
            return;
        }

        try
        {
            string dir = Path.Combine(_settings.Current.DataRoot, "snapshots");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"SNAP-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using (var fs = File.Create(path)) encoder.Save(fs);

            StatusMessage = "已抓拍：" + path;
            _log.Info("画面抓拍：" + path);
        }
        catch (Exception ex)
        {
            StatusMessage = "抓拍失败：" + ex.Message;
        }
    }

    /// <summary>当前选中的异常条目。选中即调出它当时的抓帧图。</summary>
    public ProcessViolation? SelectedViolation
    {
        get => _selectedViolation;
        set
        {
            if (!SetProperty(ref _selectedViolation, value)) return;

            // 优先用这条违规自己的保持图；没有就退回最近一张
            var image = LoadEvidenceImage(value?.EvidenceImagePath);
            SelectedEvidenceImage = image ?? EvidenceImages.FirstOrDefault()?.Image;
            OnPropertyChanged(nameof(SelectedEvidenceCaption));
        }
    }

    /// <summary>当前显示的那张保持图。</summary>
    public BitmapSource? SelectedEvidenceImage
    {
        get => _selectedEvidenceImage;
        private set
        {
            if (SetProperty(ref _selectedEvidenceImage, value))
                OnPropertyChanged(nameof(HasEvidenceImage));
        }
    }

    /// <summary>保持图区域的说明文字（没图时给出原因，而不是留一片空白）。</summary>
    public string EvidenceHint
    {
        get
        {
            if (SelectedEvidenceImage is not null) return string.Empty;
            if (EvidenceImages.Count > 0 && _selectedViolation?.EvidenceImagePath is null)
                return "这条异常没有抓帧图（记录里没存路径）";
            return "暂无保持图：出违规时若相机未连接，就没有画面可存";
        }
    }

    public string SelectedEvidenceCaption
    {
        get
        {
            if (_selectedViolation is null) return EvidenceImages.Count == 0 ? string.Empty : "最近一次违规";
            return _selectedViolation.CodeText + " · 第 " + _selectedViolation.StepSeq + " 道 · "
                   + _selectedViolation.Timestamp.ToString("HH:mm:ss");
        }
    }

    /// <summary>把当前画面存成保持图，最多留 3 张。</summary>
    private EvidenceItem? CaptureEvidence(string caption)
    {
        var frame = PreviewImage;
        if (frame is null) return null;      // 相机没连，没画面可存 —— 不编造

        try
        {
            string dir = _history.NgImageDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"PROC-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using (var fs = File.Create(path)) encoder.Save(fs);

            var item = new EvidenceItem { Path = path, Caption = caption, Image = frame };
            EvidenceImages.Insert(0, item);

            // 只留 3 张：基恩士也是这个思路，够复盘且不堆磁盘
            while (EvidenceImages.Count > 3) EvidenceImages.RemoveAt(EvidenceImages.Count - 1);

            SelectedEvidenceImage = frame;
            OnPropertyChanged(nameof(EvidenceHint));
            OnPropertyChanged(nameof(SelectedEvidenceCaption));
            return item;
        }
        catch (Exception ex)
        {
            _log.Warn("保持图保存失败：" + ex.Message);
            return null;
        }
    }

    /// <summary>把磁盘上的保持图读成位图。用 OnLoad 载入，不占着文件句柄。</summary>
    private static BitmapSource? LoadEvidenceImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
