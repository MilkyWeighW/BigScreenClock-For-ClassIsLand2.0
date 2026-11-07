// ===== SettingsPage 拆分之一：字段、构造与生命周期、右栏实时预览、属性变更通知 =====
// 职责：唯一的字段声明处、页面构造与 Loaded/Unloaded 订阅配对、「致所有用户的一封信」弹窗时机、
//       右栏实时预览的尺寸与防抖刷新、INotifyPropertyChanged 的通知出口。
//       其余职责见同目录：SettingsPage.Appearance.cs（主题与外观）、SettingsPage.Background.cs（背景图）、
//       SettingsPage.Layout.cs（字号与位置）、SettingsPage.Data.cs（课程/摄像头/分贝与记录参数）。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Timers;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Enums.SettingsWindow;
using LargeScreenClock.Helpers;
using LargeScreenClock.Models;
using LargeScreenClock.Services;
using LargeScreenClock.ViewModels;
using LargeScreenClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LargeScreenClock.Settings;

[SettingsPageInfo("com.bigscreen.clock.settings", "大屏时钟", SettingsPageCategory.External)]
// 全宽页：不加这个，CI 会把页面标题限制在 960 宽并居中，窗口一变大标题就往中间跑，
// 和下面铺满的设置卡片对不齐。加了这个标题固定靠左（左距与卡片一致），缩放窗口不再移动。
[FullWidthPage]
public partial class SettingsPage : SettingsPageBase, INotifyPropertyChanged
{
    // ===== 字段（唯一声明处）=====
    // 拆成 partial 之后所有实例字段都集中在这里，其它部分只使用、不重复声明。

    private readonly PluginSettings _settings;
    private List<string> _allCourseNames = new();

    public ObservableCollection<CourseSelectionItem> SelectedCourses { get; } = new();

    private DispatcherTimer? _previewSyncTimer;

    /// <summary>正在刷新预览。刷新过程本身可能牵动设置（换主题那类），别让它再排一轮、来回刷。</summary>
    private bool _previewSyncing;

    private DispatcherTimer? _layoutAdjustTimer;

    /// <summary>手还按在位置/字号滑块的手柄上。</summary>
    private bool _layoutPinned;

    /// <summary>按下去那一下的指针，兜底轮询靠它问「还抓着没」（见 <see cref="IsLayoutPointerBusy"/>）。</summary>
    private IPointer? _layoutPointer;

    private readonly List<Slider> _layoutSliders = new();
    private DispatcherTimer? _layoutPinWatch;

    /// <summary>大屏时钟的 ViewModel（全屏时钟窗口和这里是同一个实例），拿它的限位。</summary>
    private readonly FullScreenClockViewModel? _clockViewModel;

    private string _cameraHint = "";

    /// <summary>「地震预警」插件装了没（设置页每次打开新建，构造时查一次）。</summary>
    private readonly bool _earthquakePluginAvailable;

    public SettingsPage()
    {
        InitializeComponent();
        _settings = Plugin.Settings!;
        InitializeComponentItems();   // 「组件」页签的每一行（并补全老配置缺的默认值）
        _earthquakePluginAvailable = CheckEarthquakePlugin();
        try { _clockViewModel = Plugin.ServiceProvider?.GetService<FullScreenClockViewModel>(); } catch { }

        // 这几项订阅的都是**页面外面**的对象（设置、ViewModel），页面一收起就必须摘掉：
        // CI 每打开一次设置页都可能新建一个页面，不摘的话旧页面永远活着，还接着干 UI 活。
        Loaded += (_, _) =>
        {
            HookLayoutSliders();
            SubscribeSharedEvents();
        };
        Unloaded += (_, _) => { StopLayoutAdjust(); UnsubscribeSharedEvents(); };

        DataContext = this;

        // 预览按当前生效主题呈现：跟随档下 CI 的亮暗就是这里的亮暗
        ClockThemeApplier.Apply(_settings, CiIsDark);
        ApplyThemeFromSettings();
        // 上面这一步刚换过颜色，而 DataContext 早一步就赋好了、绑定已经按旧颜色求过一次值：
        // 不在这里补通知，几个色块会停在切换前那套颜色上（色块与右边 hex 对不上）
        NotifyAllColorsChanged();

        LoadAllCourseNames();
        RestoreSelectedCourses();
        LoadCameraDevices();
        StartPreviewSync();

        // CI 主题在明亮/黑暗间切换（跟随档）→ 重新套色，预览跟着变
        ActualThemeVariantChanged += (_, _) => ApplyThemeFromSettings();
    }

    /// <summary>
    /// 挂 / 摘「页面外面那些对象」的订阅。配对调用的地方在 Loaded / Unloaded 里。
    /// 先减后加，免得 CI 复用页面时（Loaded 来了两遍）叠成两份。
    /// </summary>
    private void SubscribeSharedEvents()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void UnsubscribeSharedEvents()
    {
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        // 页面都收起来了，没必要再为「设置变了」去刷新看不见的预览
        _previewSyncTimer?.Stop();
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 位置现在由组件自己的「位置偏移」负责，不再有位置滑块需要跟着大屏回写同步
        SchedulePreviewSync();
    }

    // ===== 右栏的实时预览 =====
    //
    // 预览放的是大屏那面墙本身（ClockSurface），只是等比缩小塞进预览框 —— 布局、字号、
    // 颜色、时钟与音量条的位置全出自同一份 XAML，不存在「预览一套、大屏另一套」。
    // 尺寸按大屏那块屏幕取，预览框整体缩放，所以看到的就是真实比例的观感。

    /// <summary>预览绑的大屏 ViewModel：与大屏用的是 DI 里的同一个单例，
    /// 所以预览里的数据、颜色、字号就是大屏当时真正会显示的那一套。</summary>
    public FullScreenClockViewModel? PreviewVm => _clockViewModel;

    private Size _previewSize = new(1920, 1080);

    /// <summary>预览用的逻辑宽度（= 大屏那块屏幕的逻辑宽度；全屏窗口的 Bounds 就是这个值）。</summary>
    public double PreviewWidth => _previewSize.Width;

    /// <summary>预览用的逻辑高度。</summary>
    public double PreviewHeight => _previewSize.Height;

    /// <summary>
    /// 按大屏所在显示器的长宽比定预览尺寸。用逻辑尺寸（物理 ÷ 缩放）而不是物理像素：
    /// 大屏窗口的实测宽度就是逻辑值，字号、边距全是按它算的，预览要跟它同一把尺子。
    /// </summary>
    private void UpdatePreviewSize()
    {
        try
        {
            // 大屏是开在 CI 主窗口所在那块屏幕上的，跟着它取，多屏时预览才对得上
            var top = TopLevel.GetTopLevel(this);
            var screens = top?.Screens;
            var screen = screens?.Primary;
            if (top is Window window && screens?.ScreenFromWindow(window) is { } own)
                screen = own;

            if (screen is { } s && s.Bounds.Width > 0 && s.Bounds.Height > 0)
            {
                var scale = s.Scaling > 0 ? s.Scaling : 1.0;
                _previewSize = new Size(s.Bounds.Width / scale, s.Bounds.Height / scale);
            }
        }
        catch
        {
            _previewSize = new Size(1920, 1080);   // 取不到就按最常见的班级大屏来
        }

        OnPropertyChanged(nameof(PreviewWidth));
        OnPropertyChanged(nameof(PreviewHeight));
    }

    /// <summary>
    /// 预览的刷新：尺寸取一次 + 让 ViewModel 把「平时只在进大屏时才同步一次」的那几项
    /// （字号、颜色、音量条/课程信息开关）从设置里重读一遍。
    ///
    /// 防抖 200 毫秒：拖字号/颜色滑块会连着触发很多次，每次都去读组件与天气缓存太浪费；
    /// 通知只在 UI 线程上发，所以这里用 DispatcherTimer 而不是 System.Timers.Timer。
    /// </summary>
    private void StartPreviewSync()
    {
        UpdatePreviewSize();

        _previewSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _previewSyncTimer.Tick += (_, _) =>
        {
            _previewSyncTimer!.Stop();
            _previewSyncing = true;
            try { _clockViewModel?.RefreshForPreview(); }
            finally { _previewSyncing = false; }
        };

        Loaded += (_, _) => UpdatePreviewSize();
        // 设置变化的订阅不在这里挂：它跟着页面的 Loaded/Unloaded 走（见 SubscribeSharedEvents）
        _clockViewModel?.RefreshForPreview();   // 首次进来先把数据填上（没进过大屏时预览也有内容）
    }

    private void SchedulePreviewSync()
    {
        if (_previewSyncing) return;
        if (_previewSyncTimer is not { } timer) return;
        timer.Stop();
        timer.Start();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
