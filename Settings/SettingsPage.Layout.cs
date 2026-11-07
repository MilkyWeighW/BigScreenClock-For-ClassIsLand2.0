// ===== SettingsPage 拆分之一：字号、位置与「调布局时让玻璃歇一会儿」=====
// 职责：时钟/课程信息/计数三处字号与两个位置滑块、按真实指针事件 pin 住玻璃重绘的机制
//       （含兜底轮询）、以及位置滑块的上下限（来自大屏 ViewModel）。
//       成员与逻辑从原 SettingsPage.axaml.cs 原样搬移。

using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LargeScreenClock.ViewModels;

namespace LargeScreenClock.Settings;

public partial class SettingsPage
{

    // 滑块绑定需要 double
    public double ClockFontSize
    {
        get => _settings.ClockFontSize;
        set { BeginLayoutAdjust(); _settings.ClockFontSize = (int)value; OnPropertyChanged(nameof(ClockFontSize)); OnPropertyChanged(nameof(ClockFontSizeText)); }
    }

    /// <summary>底部居中的课程信息字号（改完进大屏时钟生效）。</summary>
    public double CourseInfoFontSize
    {
        get => _settings.CourseInfoFontSize;
        set { BeginLayoutAdjust(); _settings.CourseInfoFontSize = (int)value; OnPropertyChanged(nameof(CourseInfoFontSize)); OnPropertyChanged(nameof(CourseInfoFontSizeText)); }
    }

    /// <summary>左下角计数字号（改完进大屏时钟生效）。</summary>
    public double CountFontSize
    {
        get => _settings.CountFontSize;
        set { BeginLayoutAdjust(); _settings.CountFontSize = (int)value; OnPropertyChanged(nameof(CountFontSize)); OnPropertyChanged(nameof(CountFontSizeText)); }
    }

    // 位置滑块已移除（位置改为组件自己的「位置偏移」，见组件页）。
    // 这里只保留字号滑块对应的「调布局时让玻璃歇一会儿」机制。

    // ===== 调位置/字号的「让玻璃歇一会儿」=====
    //
    // 这几个滑块一动，大屏和预览每一帧都要重排；而玻璃那一层每帧要画十二块圆角投影 + 各块折射图 +
    // 整屏清晰图，Skia 每帧出的好几个大中间面排着队释放不掉，内存顶着 CI 的看门狗上限走
    // （现象就是「一调位置大小就闪退」），画面本身也只是一片拖影。
    //
    // 「什么时候把手松开」以前是拿 300 毫秒没有新值来猜的，于是**按住不动停 300 毫秒**就被当成松手、
    // 玻璃按中间态烘了出来，接着手一拖又是错的 —— 用户在大屏上看到的就是「玻璃比文字慢一拍」。
    // 现在按的是真事件：按下置 pin，松开/丢捕获/页面收起清 pin，外加一条兜底轮询。

    private void BeginLayoutAdjust()
    {
        _settings.AdjustingLayout = true;
        _layoutAdjustTimer ??= CreateLayoutAdjustTimer();
        _layoutAdjustTimer.Stop();
        _layoutAdjustTimer.Start();
    }

    private DispatcherTimer CreateLayoutAdjustTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            // 手还没松开就什么都不做（也不停表，下一拍再看一眼）
            if (_layoutPinned)
            {
                StartLayoutPinWatch();
                return;
            }

            timer.Stop();
            _settings.AdjustingLayout = false;
        };
        return timer;
    }

    /// <summary>
    /// 给「改布局」的那几根滑块接上真按下/松开。
    ///
    /// 必须走**隧道**且 <c>handledEventsToo</c>：Slider 模板里的 Thumb 会把 PointerPressed 标成
    /// Handled，冒泡那一遍根本收不到；而且事件源是 Thumb 不是 Slider，按在滑块空白处更是什么都不发。
    /// </summary>
    private void HookLayoutSliders()
    {
        if (_layoutSliders.Count > 0) return;

        _layoutSliders.AddRange(new[]
        {
            ClockFontSlider, CourseInfoFontSlider, CountFontSlider,
            CameraAlertFontSlider,
        });

        foreach (var slider in _layoutSliders)
        {
            slider.AddHandler(PointerPressedEvent, OnLayoutSliderPressed, RoutingStrategies.Tunnel, true);
            slider.AddHandler(PointerReleasedEvent, OnLayoutSliderReleased, RoutingStrategies.Tunnel, true);
            slider.AddHandler(PointerCaptureLostEvent, OnLayoutSliderReleased, RoutingStrategies.Tunnel, true);
        }
    }

    private void OnLayoutSliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _layoutPointer = e.Pointer;
        _layoutPinned = true;
        BeginLayoutAdjust();
        StartLayoutPinWatch();
    }

    private void OnLayoutSliderReleased(object? sender, RoutedEventArgs e)
    {
        if (!_layoutPinned) return;
        ReleaseLayoutPin();
        BeginLayoutAdjust();   // 松手：再等 300 毫秒让最后的布局落定，然后放玻璃出来
    }

    /// <summary>
    /// 兜底轮询：手明明松了、松手事件却没送到（鼠标在窗口外松开、拖到别的窗口上、窗口失焦……），
    /// pin 就会一直挂着，大屏和预览的玻璃会**永久**不画。所以按着期间每 300 毫秒核一次。
    /// </summary>
    private void StartLayoutPinWatch()
    {
        if (!_layoutPinned) return;

        _layoutPinWatch ??= CreateLayoutPinWatch();
        if (!_layoutPinWatch.IsEnabled) _layoutPinWatch.Start();
    }

    private DispatcherTimer CreateLayoutPinWatch()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            if (IsLayoutPointerBusy()) return;

            ReleaseLayoutPin();
            BeginLayoutAdjust();
        };
        return timer;
    }

    /// <summary>
    /// 指针是不是还被这几根滑块（或它们模板里的手柄）抓着。滑块按下手柄时会 Capture 住指针、
    /// 松手就还回来，所以「还抓着」等价于「这一拖还没结束」。
    ///
    /// 只认这几根滑块：被别的控件抓着跟这次拖动无关，那就当它已经结束了 ——
    /// 兜底轮询宁可早放不能晚放，晚放就是玻璃永久不画。
    /// </summary>
    private bool IsLayoutPointerBusy()
    {
        if (_layoutPointer?.Captured is not Visual captured) return false;

        foreach (var slider in _layoutSliders)
            if (ReferenceEquals(captured, slider) || slider.GetVisualDescendants().Contains(captured))
                return true;

        return false;
    }

    /// <summary>清 pin 并停下兜底轮询。</summary>
    private void ReleaseLayoutPin()
    {
        _layoutPinned = false;
        _layoutPointer = null;
        _layoutPinWatch?.Stop();
    }

    /// <summary>
    /// 页面收起：pin 与两个定时器一起收掉，并把冻结点解开 —— 定时器是 DispatcherTimer，
    /// 攥着这一页（连带里面的预览、绑定）不放就是一份泄漏；而冻结不解开的话，
    /// 下次进设置页预览的玻璃会一直不画。
    /// </summary>
    private void StopLayoutAdjust()
    {
        ReleaseLayoutPin();
        _layoutAdjustTimer?.Stop();
        _settings.AdjustingLayout = false;
    }

    public string ClockFontSizeText => $"{_settings.ClockFontSize}px";

    public string CourseInfoFontSizeText => $"{_settings.CourseInfoFontSize}px";

    public string CountFontSizeText => $"{_settings.CountFontSize}px";
}
