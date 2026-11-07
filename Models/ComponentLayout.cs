using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace LargeScreenClockRevived.Models;

/// <summary>
/// 「组件」这一层的外观与位置设置：大屏上每个能单独调整的元素（时钟、日期、天气、音量条……）
/// 各自持有一份。
///
/// 全部字段都只做「在自然布局上叠加一点微调」：
/// - <see cref="Enabled"/> 默认开，关掉后这个组件在大屏上不渲染、也不占位；
/// - <see cref="Scale"/> 1 = 不缩放，是原始大小；
/// - <see cref="OffsetX"/> / <see cref="OffsetY"/> 0 = 不动，单位是大屏宽/高的百分比（换分辨率也还是同一个相对位置）；
/// - <see cref="Order"/> 决定同一分区里各组件谁先谁后（null = 按 <see cref="ComponentCatalog"/> 的清单顺序）。
/// 默认值下与「没有这套组件设置」逐像素一致。
///
/// 老配置里没有这些字段时，<see cref="Models.PluginSettings.GetComponentLayout"/> 会补一份默认值出来，
/// 所以升级上来的用户不会出现「组件位置全乱」。
/// </summary>
public sealed class ComponentLayout : INotifyPropertyChanged
{
    /// <summary>缩放倍率下限（10%：留出「几乎看不见但还占着位置」的极端档）。</summary>
    public const double MinScale = 0.1;

    /// <summary>缩放倍率上限。</summary>
    public const double MaxScale = 2.0;

    /// <summary>水平/垂直偏移百分比下限（-100 = 整整挪出一个屏宽/屏高）。</summary>
    public const double MinOffsetPercent = -100;

    /// <summary>水平/垂直偏移百分比上限（100 = 整整挪出一个屏宽/屏高）。</summary>
    public const double MaxOffsetPercent = 100;

    /// <summary>不透明度下限（0 = 完全透明，看不见但还占着布局）。</summary>
    public const double MinOpacity = 0.0;

    /// <summary>不透明度上限（1 = 完全不透明）。</summary>
    public const double MaxOpacity = 1.0;

    private double _scale = 1.0;
    private double _offsetX;
    private double _offsetY;
    private bool _enabled = true;
    private int? _order;
    private double _opacity = 1.0;

    /// <summary>
    /// 整体缩放倍率：1 = 原始大小，0.1 = 缩到一成，2 = 放大一倍。
    /// 通过 RenderTransform 缩放，不参与布局，所以拉大拉小不会把别的组件挤走。
    /// </summary>
    public double Scale
    {
        get => _scale;
        set => Set(ref _scale, Math.Clamp(value, MinScale, MaxScale));
    }

    /// <summary>
    /// 水平偏移：占大屏宽度的百分比（-100 = 往左挪一整个屏宽，100 = 往右挪一整个屏宽）。
    /// 同样用 RenderTransform 做，自然布局（时钟/音量条那套上下限位）一个字节都不用改。
    /// </summary>
    public double OffsetX
    {
        get => _offsetX;
        set => Set(ref _offsetX, Math.Clamp(value, MinOffsetPercent, MaxOffsetPercent));
    }

    /// <summary>垂直偏移：占大屏高度的百分比。</summary>
    public double OffsetY
    {
        get => _offsetY;
        set => Set(ref _offsetY, Math.Clamp(value, MinOffsetPercent, MaxOffsetPercent));
    }

    /// <summary>
    /// 是否在大屏上显示这个组件（默认开）。
    /// 关掉后组件不渲染、也不占位（同一分区里后面的组件会自动补上来）。
    /// 这一项只管「显不显示」，不影响下面的大小/位置微调值——重新打开还是原来那套。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>
    /// 同一分区里的先后顺序：小的在前（排得更靠上/靠左），null = 按组件清单的默认顺序。
    /// 分区是固定的（顶部行 / 主体 / 底部行），顺序只在分区内部生效，不会把组件搬到别的区去。
    /// </summary>
    public int? Order
    {
        get => _order;
        set => Set(ref _order, value);
    }

    /// <summary>
    /// 整体不透明度：1 = 完全不透明（默认），0 = 完全透明（看不见，但仍占着布局位置）。
    /// 作用在整块组件的外壳上，文字、图标、进度条会一起变淡。
    /// </summary>
    public double Opacity
    {
        get => _opacity;
        set => Set(ref _opacity, Math.Clamp(value, MinOpacity, MaxOpacity));
    }

    /// <summary>这一份是不是全默认值（设置页拿它决定「恢复默认」按钮灰不灰）。不存盘，是算出来的。</summary>
    [JsonIgnore]
    public bool IsDefault =>
        Math.Abs(Scale - 1) < 0.0001 &&
        Math.Abs(OffsetX) < 0.0001 &&
        Math.Abs(OffsetY) < 0.0001 &&
        Math.Abs(Opacity - 1) < 0.0001;

    /// <summary>恢复默认外观（大小 1、位置不动、不透明度 1）。不动启用状态与顺序——那是「显不显示/排第几」，不是外观。</summary>
    public void Reset()
    {
        Scale = 1.0;
        OffsetX = 0;
        OffsetY = 0;
        Opacity = 1.0;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 一个可单独调整的大屏组件的固定信息（不存盘）：内部 id、显示名、说明、图标。
/// 布局数值在 <see cref="ComponentLayout"/> 里，这里只描述「有哪些组件」。
/// </summary>
/// <param name="Id">存盘用的稳定 id（改它会丢用户设置，别改）。</param>
/// <param name="Name">设置页显示的名字。</param>
/// <param name="Description">设置页那行的小字说明。</param>
/// <param name="Glyph">Fluent 图标码点（字形取自 ClassIsland 自带的 FluentSystemIcons 字体）。</param>
public sealed record ComponentDescriptor(string Id, string Name, string Description, string Glyph);

/// <summary>
/// 大屏组件的清单与默认描述。顺序就是设置页「组件」页签里的顺序，按元素在大屏上从上到下排。
///
/// 这里刻意只列「看得见、能单独调」的元素：背景层与右下角的最小化/退出按钮不在其中
/// （前者是整屏底，后者是窗口操作，跟着窗口走）。
/// </summary>
public static class ComponentCatalog
{
    /// <summary>全部组件（顺序 = 设置页显示顺序 = 大屏从上到下）。</summary>
    public static IReadOnlyList<ComponentDescriptor> All { get; } = new ComponentDescriptor[]
    {
        new("clock", "时钟", "大屏中央的时间数字。", "\uF361"),
        new("date", "日期", "右上角的日期与副标题。", "\uE304"),
        new("weather", "天气", "顶部左侧的天气图标、气温与字幕。", "\uF467"),
        new("rainMerge", "降雨提醒（顶部）", "顶部那行里合并显示的「多久下雨」。", "\uF453"),
        new("countdownMerge", "倒计时（顶部）", "顶部那行里合并显示的倒计时。", "\uE161"),
        new("textMerge", "文本提醒（顶部）", "顶部那行里合并显示的文本组件内容。", "\uE3E4"),
        new("alertDetail", "预警详情", "顶部中间横向滚动的预警详情（鼠标悬停或点击预警时出现）。", "\uE9E4"),
        new("reminderPanel", "提醒面板", "左上角整块提醒面板（天气、预警、倒计时、文本、降雨）。", "\uE025"),
        new("volume", "教室音量", "音量标题、刻度条与五档文字。", "\uF00D"),
        new("counter", "吵闹计数", "左下角的吵闹次数与「正在记录」提示。", "\uE3E0"),
        new("course", "课程信息", "底部居中的当前课程与打铃时间。", "\uE215"),
        new("progress", "课程进度条", "屏幕最底部的整宽进度带。", "\uE5DF"),
        new("image", "自定义图片", "自选的图片（班徽、logo 等），大小、位置、不透明度与其它组件一样调。", "\uE9B2"),
        new("earthquake", "地震警报", "地震预警插件报警时整屏正中的提示块。", "\uE7C5"),
    };

    /// <summary>按 id 找组件描述（找不到返回 null）。</summary>
    public static ComponentDescriptor? Find(string id)
        => All.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.Ordinal));
}
