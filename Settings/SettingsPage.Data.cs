// ===== SettingsPage 拆分之一：课程、摄像头、分贝阈值与记录参数 =====
// 职责：从 CI 配置读课程名单并维护「目标课程」列表、摄像头占用检测的设备枚举与勾选、
//       吵闹分段统计的分贝阈值（对数刻度、递增联动）与记录参数（按时长/按大小的清理规则）。
//       文件末尾两个列表项小类（摄像头项、课程项）也在这里。
//       成员与逻辑从原 SettingsPage.axaml.cs 原样搬移。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.Settings;

public partial class SettingsPage
{
    // 课程身份映射：下拉框里显示的是「展示串」（同名课程会带老师/Id 后缀），
    // 真正存进设置的是「科目 Id + 原始名字」，这样同名不同绑定的课才能分开。
    private readonly Dictionary<string, string> _courseDisplayToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _courseDisplayToName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _courseIdToDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _courseNameToDisplay = new(StringComparer.Ordinal);

    private void LoadAllCourseNames()
    {
        _allCourseNames.Clear();
        _courseDisplayToId.Clear();
        _courseDisplayToName.Clear();
        _courseIdToDisplay.Clear();
        _courseNameToDisplay.Clear();
        try
        {
            var dataDir = CiDataDir;
            var settingsPath = Path.Combine(dataDir, "Settings.json");
            var profileFileName = "7.json";
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("SelectedProfile", out var sp))
                    profileFileName = sp.GetString() ?? "7.json";
            }
            var profilePath = Path.Combine(dataDir, "Profiles", profileFileName);
            if (!File.Exists(profilePath)) return;
            using var profileDoc = JsonDocument.Parse(File.ReadAllText(profilePath));
            if (!profileDoc.RootElement.TryGetProperty("Subjects", out var subjectsEl)) return;

            // 先把「科目 Id + 名字 + 老师」收集齐：同名科目要靠 Id 区分，
            // 但下拉框里只能显示字符串，所以同名时用「名字（老师）」/「名字（Id 前 8 位）」消歧。
            var entries = new List<(string Id, string Name, string Teacher)>();
            foreach (var subject in subjectsEl.EnumerateObject())
            {
                if (!subject.Value.TryGetProperty("Name", out var ne)) continue;
                var name = ne.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                var teacher = subject.Value.TryGetProperty("TeacherName", out var te)
                    ? te.GetString() ?? ""
                    : "";
                entries.Add((subject.Name, name!, teacher));
            }

            var nameCount = entries.GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.Count());
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in entries)
            {
                var display = e.Name;
                if (nameCount[e.Name] > 1)
                {
                    display = string.IsNullOrWhiteSpace(e.Teacher)
                        ? $"{e.Name}（{e.Id[..Math.Min(8, e.Id.Length)]}）"
                        : $"{e.Name}（{e.Teacher}）";
                }

                var unique = display;
                var suffix = 2;
                while (!used.Add(unique)) unique = $"{display}#{suffix++}";

                _allCourseNames.Add(unique);
                _courseDisplayToId[unique] = e.Id;
                _courseDisplayToName[unique] = e.Name;
                _courseIdToDisplay[e.Id] = unique;
                _courseNameToDisplay.TryAdd(e.Name, unique);
            }
        }
        catch { }
    }

    private void RestoreSelectedCourses()
    {
        SelectedCourses.Clear();
        for (var i = 0; i < _settings.TargetCourseNames.Count; i++)
        {
            var name = _settings.TargetCourseNames[i];
            var id = i < _settings.TargetCourseIds.Count ? _settings.TargetCourseIds[i] : "";

            // 有 Id 就按 Id 找回下拉框里的展示串（同名课程靠这个精确还原）；
            // 老配置只有名字：按名字找，找不到就原样显示，让用户自己重选。
            string? initial = null;
            if (!string.IsNullOrWhiteSpace(id) && _courseIdToDisplay.TryGetValue(id, out var byId))
                initial = byId;
            else if (_courseNameToDisplay.TryGetValue(name, out var byName))
                initial = byName;
            else if (!string.IsNullOrWhiteSpace(name))
                initial = name;

            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, initial, this));
        }
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
    }

    public void AddCourse_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    internal void OnCourseSelectionChanged() => SaveCourses();

    internal void RemoveCourse(CourseSelectionItem item)
    {
        SelectedCourses.Remove(item);
        if (SelectedCourses.Count == 0)
            SelectedCourses.Add(new CourseSelectionItem(_allCourseNames, null, this));
        SaveCourses();
    }

    private void SaveCourses()
    {
        var names = new List<string>();
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in SelectedCourses)
        {
            var display = item.SelectedName;
            if (string.IsNullOrWhiteSpace(display)) continue;

            _courseDisplayToId.TryGetValue(display, out var id);
            _courseDisplayToName.TryGetValue(display, out var name);
            name = string.IsNullOrWhiteSpace(name) ? display : name;
            id ??= "";

            // 同一个科目只留一条（同名不同 Id 的两门课各自保留）
            var key = string.IsNullOrWhiteSpace(id) ? "n:" + name : "i:" + id;
            if (!seen.Add(key)) continue;

            names.Add(name);
            ids.Add(id);
        }

        _settings.TargetCourseNames.Clear();
        foreach (var n in names) _settings.TargetCourseNames.Add(n);
        _settings.TargetCourseIds.Clear();
        foreach (var id in ids) _settings.TargetCourseIds.Add(id);
        Plugin.SaveSettings(); // 强制写盘
    }

    // ===== 摄像头占用 =====

    /// <summary>已枚举到的摄像头（勾选状态写回设置，见 <see cref="SaveCameraSelection"/>）。</summary>
    public ObservableCollection<CameraDeviceItem> CameraDevices { get; } = new();

    /// <summary>设备列表下方的提示（枚举出错/没找到设备时才有内容）。</summary>
    public string CameraHint
    {
        get => _cameraHint;
        private set
        {
            _cameraHint = value;
            OnPropertyChanged(nameof(CameraHint));
            OnPropertyChanged(nameof(HasCameraHint));
        }
    }

    public bool HasCameraHint => _cameraHint.Length > 0;

    private CameraActivityService? CameraService
    {
        get
        {
            try { return Plugin.ServiceProvider?.GetService<CameraActivityService>(); }
            catch { return null; }
        }
    }

    /// <summary>
    /// 枚举本机摄像头填进列表。首次使用（还没填过默认值）时全部勾上——开箱即用，
    /// 用户之后取消勾选就真的不再提醒。
    /// </summary>
    private void LoadCameraDevices()
    {
        var service = CameraService;
        var devices = service?.EnumerateDevices() ?? new List<CameraDevice>();

        CameraDevices.Clear();
        if (devices.Count == 0)
        {
            CameraHint = "没有找到摄像头设备（外接摄像头插上后点「刷新设备列表」）。";
            return;
        }

        if (!_settings.CameraListFilled)
        {
            _settings.MonitoredCameras.Clear();
            foreach (var d in devices)
                _settings.MonitoredCameras.Add(new MonitoredCamera(d.Name, d.SymbolicLink).ToSettingValue());
            _settings.CameraListFilled = true;
            Plugin.SaveSettings();
        }

        var displayNames = CameraDeviceNaming.DisplayNames(devices);
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            CameraDevices.Add(new CameraDeviceItem(d, displayNames[i], IsMonitored(d.Name, d.SymbolicLink), this));
        }

        CameraHint = service?.LastError ?? "";
    }

    /// <summary>设置里勾选的项里有没有这一台（按路径认，认不出才退回按名字，见 <see cref="MonitoredCamera.Matches"/>）。</summary>
    private bool IsMonitored(string name, string link)
        => _settings.MonitoredCameras
            .Select(MonitoredCamera.Parse)
            .Any(m => m.Matches(name, link));

    /// <summary>勾选/取消勾选设备后，把当前勾选写回设置（供 <see cref="CameraDeviceItem"/> 调用）。</summary>
    internal void SaveCameraSelection()
    {
        _settings.MonitoredCameras.Clear();
        foreach (var d in CameraDevices.Where(d => d.IsSelected))
            _settings.MonitoredCameras.Add(d.SettingValue);
        _settings.CameraListFilled = true;
        Plugin.SaveSettings(); // 强制写盘
    }

    public void RefreshCameras_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => LoadCameraDevices();

    public void TestButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { Plugin.ServiceProvider?.GetRequiredService<FullScreenClockViewModel>().Show(); }
        catch { }
    }

    // ===== 分贝阈值（滑块用对数刻度 0.001~1.0，也支持直接填数字） =====

    // 对数刻度：Slider 范围 -3~0 映射阈值 0.001~1.0（阈值 = 10^index）。
    // 低值区（0.04~0.14）在线性滑块上挤成一团，对数刻度才能精细拖动。
    // 递增联动：安静 < 良好 < 一般 < 吵闹 必须依次递增——后一级滑块的最小值
    // 就是前一级的当前值（对数刻度），保证永远有序。
    private const double ThresholdSliderMin = -3.0;
    private const double ThresholdSliderMax = 0.0;
    private const double ThresholdMin = 0.001;
    private const double ThresholdMax = 1.0;

    // 动态滑块最小值（对数刻度），供 XAML 上 良好/一般/吵闹 Slider 的 Minimum 绑定。
    // 例如安静设为 0.01，良好的可拖范围就变成 0.01~1.00。
    public double GoodThresholdSliderMin => Math.Log10(_settings.DecibelQuietThreshold);
    public double NormalThresholdSliderMin => Math.Log10(_settings.DecibelGoodThreshold);
    public double NoisyThresholdSliderMin => Math.Log10(_settings.DecibelNormalThreshold);

    public double DecibelQuietThresholdIndex
    {
        get => Math.Log10(_settings.DecibelQuietThreshold);
        set
        {
            _settings.DecibelQuietThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelGoodThresholdIndex
    {
        get => Math.Log10(_settings.DecibelGoodThreshold);
        set
        {
            _settings.DecibelGoodThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelNormalThresholdIndex
    {
        get => Math.Log10(_settings.DecibelNormalThreshold);
        set
        {
            _settings.DecibelNormalThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }
    public double DecibelNoisyThresholdIndex
    {
        get => Math.Log10(_settings.DecibelNoisyThreshold);
        set
        {
            _settings.DecibelNoisyThreshold = Math.Clamp(Math.Pow(10, value), ThresholdMin, ThresholdMax);
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }

    public string DecibelQuietThresholdText
    {
        get => _settings.DecibelQuietThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelQuietThresholdText), value, v => _settings.DecibelQuietThreshold = v); }
    }
    public string DecibelGoodThresholdText
    {
        get => _settings.DecibelGoodThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelGoodThresholdText), value, v => _settings.DecibelGoodThreshold = v); }
    }
    public string DecibelNormalThresholdText
    {
        get => _settings.DecibelNormalThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelNormalThresholdText), value, v => _settings.DecibelNormalThreshold = v); }
    }
    public string DecibelNoisyThresholdText
    {
        get => _settings.DecibelNoisyThreshold.ToString("F4");
        set { SetThreshold(nameof(DecibelNoisyThresholdText), value, v => _settings.DecibelNoisyThreshold = v); }
    }

    private void SetThreshold(string propName, string value, Action<double> setter)
    {
        if (double.TryParse(value, out var v))
        {
            setter(Math.Clamp(v, ThresholdMin, ThresholdMax));
            ClampThresholds();
            NotifyThresholdsChanged();
        }
    }

    /// <summary>
    /// 链式约束：强制 安静 ≤ 良好 ≤ 一般 ≤ 吵闹。后一级若低于前一级则被钳回前一级的值，
    /// 保证拖动/填数后阈值永远递增有序。
    /// </summary>
    private void ClampThresholds()
    {
        _settings.DecibelGoodThreshold = Math.Max(_settings.DecibelGoodThreshold, _settings.DecibelQuietThreshold);
        _settings.DecibelNormalThreshold = Math.Max(_settings.DecibelNormalThreshold, _settings.DecibelGoodThreshold);
        _settings.DecibelNoisyThreshold = Math.Max(_settings.DecibelNoisyThreshold, _settings.DecibelNormalThreshold);
    }

    /// <summary>
    /// 阈值相关属性统一通知：四个文本框、四个滑块位置、以及后一级滑块的 Minimum。
    /// </summary>
    private void NotifyThresholdsChanged()
    {
        OnPropertyChanged(nameof(DecibelQuietThresholdText));
        OnPropertyChanged(nameof(DecibelGoodThresholdText));
        OnPropertyChanged(nameof(DecibelNormalThresholdText));
        OnPropertyChanged(nameof(DecibelNoisyThresholdText));
        OnPropertyChanged(nameof(DecibelQuietThresholdIndex));
        OnPropertyChanged(nameof(DecibelGoodThresholdIndex));
        OnPropertyChanged(nameof(DecibelNormalThresholdIndex));
        OnPropertyChanged(nameof(DecibelNoisyThresholdIndex));
        OnPropertyChanged(nameof(GoodThresholdSliderMin));
        OnPropertyChanged(nameof(NormalThresholdSliderMin));
        OnPropertyChanged(nameof(NoisyThresholdSliderMin));
    }

    // ===== 记录参数 =====
    public double NoisySustainSeconds
    {
        get => _settings.NoisySustainSeconds;
        set
        {
            _settings.NoisySustainSeconds = value;
            OnPropertyChanged(nameof(NoisySustainSeconds));
            OnPropertyChanged(nameof(NoisySustainSecondsText));
        }
    }
    public string NoisySustainSecondsText => $"{_settings.NoisySustainSeconds:0.#} 秒";

    public double FallWindowSeconds
    {
        get => _settings.FallWindowSeconds;
        set
        {
            _settings.FallWindowSeconds = value;
            OnPropertyChanged(nameof(FallWindowSeconds));
            OnPropertyChanged(nameof(FallWindowSecondsText));
        }
    }
    public string FallWindowSecondsText => $"{_settings.FallWindowSeconds:0.#} 秒";

    /// <summary>音量采样间隔（秒）。</summary>
    public double SamplingIntervalSeconds
    {
        get => _settings.SamplingIntervalSeconds;
        set
        {
            _settings.SamplingIntervalSeconds = value;
            OnPropertyChanged(nameof(SamplingIntervalSeconds));
            OnPropertyChanged(nameof(SamplingIntervalSecondsText));
        }
    }
    public string SamplingIntervalSecondsText => $"{_settings.SamplingIntervalSeconds:0.##} 秒";

    public bool SkipFirst3Min
    {
        get => _settings.SkipFirst3Min;
        set
        {
            _settings.SkipFirst3Min = value;
            OnPropertyChanged(nameof(SkipFirst3Min));
            OnPropertyChanged(nameof(SkipFirstMinutes));
        }
    }

    public int SkipFirstMinutes
    {
        get => _settings.SkipFirstMinutes;
        set
        {
            _settings.SkipFirstMinutes = value;
            OnPropertyChanged(nameof(SkipFirstMinutes));
            OnPropertyChanged(nameof(SkipFirstMinutesText));
        }
    }
    public string SkipFirstMinutesText => $"{_settings.SkipFirstMinutes} 分钟";

    public bool EnableNoiseDebugLog
    {
        get => _settings.EnableNoiseDebugLog;
        set
        {
            _settings.EnableNoiseDebugLog = value;
            OnPropertyChanged(nameof(EnableNoiseDebugLog));
        }
    }

    public int LogRetentionDays
    {
        get => _settings.LogRetentionDays;
        set
        {
            _settings.LogRetentionDays = value;
            OnPropertyChanged(nameof(LogRetentionDays));
            OnPropertyChanged(nameof(LogRetentionDaysText));
        }
    }
    public string LogRetentionDaysText => $"{_settings.LogRetentionDays} 天";

    // 保留规则：两个单选按钮写同一个设置的两种取值（Avalonia 的 RadioButton 只能双向绑 bool，
    // 所以把「按大小」做成「非按时长」的取反视图，点未选中的那个才真正生效）
    public bool LogKeepByDuration
    {
        get => _settings.LogKeepByDuration;
        set
        {
            if (!value) return;
            _settings.LogKeepByDuration = true;
            NotifyLogKeepChanged();
        }
    }

    public bool LogKeepBySize
    {
        get => !_settings.LogKeepByDuration;
        set
        {
            if (!value) return;
            _settings.LogKeepByDuration = false;
            NotifyLogKeepChanged();
        }
    }

    private void NotifyLogKeepChanged()
    {
        OnPropertyChanged(nameof(LogKeepByDuration));
        OnPropertyChanged(nameof(LogKeepBySize));
        OnPropertyChanged(nameof(LogKeepLabel));
        OnPropertyChanged(nameof(LogKeepValue));
        OnPropertyChanged(nameof(LogKeepMinimum));
        OnPropertyChanged(nameof(LogKeepMaximum));
        OnPropertyChanged(nameof(LogKeepTick));
        OnPropertyChanged(nameof(LogKeepValueText));
    }

    /// <summary>两种规则共用一个滑块，滑的分别是「分钟」或「KB」。</summary>
    public double LogKeepValue
    {
        get => _settings.LogKeepByDuration ? _settings.LogKeepMinutes : _settings.LogKeepMinKb;
        set
        {
            if (_settings.LogKeepByDuration) _settings.LogKeepMinutes = (int)value;
            else _settings.LogKeepMinKb = (int)value;
            OnPropertyChanged(nameof(LogKeepValue));
            OnPropertyChanged(nameof(LogKeepValueText));
        }
    }

    public string LogKeepLabel => _settings.LogKeepByDuration ? "不足（分钟）" : "小于（KB）";
    public double LogKeepMinimum => _settings.LogKeepByDuration ? 1 : 10;
    public double LogKeepMaximum => _settings.LogKeepByDuration ? 120 : 3072;
    public double LogKeepTick => _settings.LogKeepByDuration ? 1 : 16;
    public string LogKeepValueText => _settings.LogKeepByDuration
        ? $"{_settings.LogKeepMinutes} 分钟" : $"{_settings.LogKeepMinKb} KB";

    /// <summary>打开存放调试日志的目录（就是插件配置目录）。</summary>
    public void OpenLogFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var dir = Plugin.ConfigFolder;
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch { }
    }
}

/// <summary>设置页里一个摄像头的勾选项。勾选变化立刻写回设置（大屏时钟那边每秒会重算提醒）。</summary>
public class CameraDeviceItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private readonly CameraDevice _device;
    private bool _isSelected;

    /// <summary>列表上显示的名字。同名设备（一体机 + 展台）各带一小段设备路径，
    /// 免得两行同样文字分不出哪个是哪个。</summary>
    public string Name { get; }

    /// <summary>悬停提示：设备名 + 完整设备路径。</summary>
    public string PathTip { get; }

    /// <summary>写回设置的一行（名字 + 接口路径，见 <see cref="MonitoredCamera.ToSettingValue"/>）。</summary>
    internal string SettingValue => new MonitoredCamera(_device.Name, _device.SymbolicLink).ToSettingValue();

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _parent.SaveCameraSelection();
        }
    }

    public CameraDeviceItem(CameraDevice device, string displayName, bool isSelected, SettingsPage parent)
    {
        _device = device;
        Name = displayName;
        PathTip = device.SymbolicLink.Length == 0 ? device.Name : device.SymbolicLink;
        _isSelected = isSelected;
        _parent = parent;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class CourseSelectionItem : INotifyPropertyChanged
{
    private readonly SettingsPage _parent;
    private string? _selectedName;
    public List<string> AllCourses { get; }
    public ICommand RemoveCommand { get; }

    public string? SelectedName
    {
        get => _selectedName;
        set
        {
            if (_selectedName == value) return;
            _selectedName = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedName)));
            if (!string.IsNullOrWhiteSpace(value)) _parent.OnCourseSelectionChanged();
        }
    }

    public CourseSelectionItem(List<string> allCourses, string? initial, SettingsPage parent)
    {
        _parent = parent; AllCourses = allCourses; _selectedName = initial;
        RemoveCommand = new RelayCommand(() => parent.RemoveCourse(this));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
