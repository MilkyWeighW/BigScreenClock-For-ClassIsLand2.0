using ClassIsland.Core.Icons;

namespace LargeScreenClock.Helpers;

/// <summary>
/// 矢量图标字形转换：把 Lucide 图标名/枚举换成 ClassIsland 自带的 Fluent 图标字形。
///
/// 为什么还保留 LucideIconKind 入参：天气码、预警、摄像头等动态图标一直是按
/// LucideIconKind 成员名（如 "CloudRain"）传下来的，这里只换了「最终画出来的字形」，
/// 上游调用点（CiWeatherReader / ReminderData / NoiseLevelSlots）都不用动。
///
/// 用法：需要动态选图标时把返回的字形字符串绑到 XAML 的
/// &lt;controls1:FluentIcon Glyph="{Binding XxxIcon}" /&gt;；写死的图标直接在 XAML 里
/// 用 &lt;controls1:FluentIcon Glyph="&#xE713;" /&gt;，不必经过本类。
/// </summary>
public static class IconGlyph
{
    /// <summary>Lucide 图标（枚举）→ Fluent 图标字形。</summary>
    public static string Of(LucideIconKind kind) => kind switch
    {
        LucideIconKind.Sun => "\uF467",            // weather_sunny（带光芒）
        LucideIconKind.CloudSun => "\uF44F",       // weather_partly_cloudy_day
        LucideIconKind.Cloud => "\uF43D",          // weather_cloudy
        LucideIconKind.CloudDrizzle => "\uF43F",   // weather_drizzle
        LucideIconKind.CloudLightning => "\uF46B", // weather_thunderstorm
        LucideIconKind.CloudSnow => "\uF45B",      // weather_snow
        LucideIconKind.CloudRain => "\uF453",      // weather_rain
        LucideIconKind.Snowflake => "\uF461",      // weather_snowflake
        LucideIconKind.CloudFog => "\uF443",       // weather_fog
        LucideIconKind.Wind => "\uF441",           // weather_duststorm（Fluent 没有单独的风图标）
        LucideIconKind.Droplets => "\uE795",       // drop
        LucideIconKind.Flame => "\uE83D",          // fire
        LucideIconKind.Hourglass => "\uE9A8",      // hourglass
        LucideIconKind.Thermometer => "\uF1B4",    // temperature
        LucideIconKind.CircleCheck => "\uE426",    // checkmark_circle
        LucideIconKind.Webcam => "\uE392",         // camera（摄像头占用提醒）
        _ => "\uF431"                              // warning（兜底，保证界面上不出现空白）
    };

    /// <summary>
    /// 图标名（<see cref="LucideIconKind"/> 的成员名，如 "Sun"）→ 图标字形。
    /// 名字不认识时返回兜底图标（默认警告三角），保证界面上不会出现空白。
    /// </summary>
    public static string Of(string? name, LucideIconKind fallback = LucideIconKind.TriangleAlert)
        => !string.IsNullOrEmpty(name) && Enum.TryParse<LucideIconKind>(name, out var kind)
            ? Of(kind)
            : Of(fallback);
}
