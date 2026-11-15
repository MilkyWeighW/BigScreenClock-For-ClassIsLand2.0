using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Helpers;

/// <summary>
/// 把主题配色落到设置上（需要 PluginSettings，故与纯算法的 <see cref="ClockTheme"/> 分开放）。
/// </summary>
public static class ClockThemeApplier
{
    /// <summary>
    /// 按 ThemeMode + CI 当前亮暗 + 背景图明暗解析出目标调色板，把设置里的 6 个颜色换成该主题那一套。
    /// 只在生效变体与上次套用的不同时才动：先把当前这套颜色存回它自己的主题槽位（用户调过的色不丢），
    /// 再读出新主题槽位里那套。某个主题第一次用到时槽位里就是该主题的默认色。
    /// </summary>
    /// <returns>true 表示本次换过颜色设置</returns>
    public static bool Apply(PluginSettings settings, bool ciIsDark)
    {
        var variant = ClockTheme.ResolveVariant(EffectiveMode(settings), ciIsDark, BackgroundIsDark(settings));
        if (settings.AppliedPaletteVariant == variant) return false;

        Capture(settings, settings.AppliedPaletteVariant);   // 旧主题那套先存回自己的槽位
        Restore(settings, variant);                          // 再读出新主题那套
        settings.AppliedPaletteVariant = variant;
        return true;
    }

    /// <summary>
    /// 当前真正说了算的主题档位：背景开关开着就用背景那一套（外观里的「主题」此时整项失效），
    /// 否则还是用外观里那项。
    /// </summary>
    public static ClockThemeMode EffectiveMode(PluginSettings settings)
        => settings.BackgroundEnabled
            ? ((BackgroundThemeMode)settings.BackgroundThemeMode).ToClockThemeMode()
            : (ClockThemeMode)settings.ThemeMode;

    /// <summary>
    /// 「自动」主题档用的输入：背景图算不算暗。
    /// null = 没选图 / 图读不出来 —— 自动档这时退化成跟随 ClassIsland。
    /// </summary>
    public static bool? BackgroundIsDark(PluginSettings settings)
    {
        var path = BackgroundImageStore.ResolvePath(settings.BackgroundImageFile);
        return BackgroundImageStore.ReadLuminance(path) is { } luminance
            ? BackgroundTone.IsDark(luminance, settings.BackgroundDim)
            : null;
    }

    /// <summary>按设置解析出生效调色板（大屏与设置页预览共用这一条路径）。</summary>
    public static ClockThemePalette ResolvePalette(PluginSettings settings, bool ciIsDark)
        => ClockTheme.Resolve(EffectiveMode(settings), ciIsDark, BackgroundIsDark(settings));

    /// <summary>
    /// 老配置（没有主题功能那版）升级上来时的迁移：它只有一套颜色，就是用户当时在用的那套
    /// （旧版默认就是黑底白字，用户改也是在这套上改）。
    /// 若直接跟随 CI，本机 CI 是浅色时会把外观整个换成明亮那套——用户会以为自己的外观设置没了。
    /// 故：把这套颜色收进黑暗槽、主题项置于「黑暗」，升级后外观与升级前完全一致；
    /// 想换主题在设置里自己选（主题项就在外观区第一行）。
    /// 只在「还没进过主题系统」的配置上跑一次，之后不再触发。
    /// </summary>
    public static void MigrateLegacySettings(PluginSettings settings)
    {
        if (settings.AppliedPaletteVariant != ClockTheme.VariantUnknown) return;

        Capture(settings, ClockTheme.VariantDark);
        settings.ThemeMode = (int)ClockThemeMode.Dark;
        settings.AppliedPaletteVariant = ClockTheme.VariantDark;
    }

    /// <summary>变体 → 它的颜色槽位（未知变体按黑暗：老配置里的颜色本来就是黑暗那套）。</summary>
    public static ThemeColorSet SetOf(PluginSettings settings, int variant)
        => variant == ClockTheme.VariantLight ? settings.LightColors : settings.DarkColors;

    /// <summary>把当前生效的 6 个颜色存进该变体的槽位。</summary>
    public static void Capture(PluginSettings settings, int variant)
    {
        var set = SetOf(settings, variant);
        set.Background = settings.BackgroundColor;
        set.Font = settings.FontColor;
        set.CourseInfo = settings.CourseInfoColor;
        set.NoiseTitle = settings.NoiseTitleColor;
        set.Progress = settings.ProgressColor;
        set.Rain = settings.RainColor;
    }

    /// <summary>把该变体槽位里的颜色读成当前生效颜色。</summary>
    public static void Restore(PluginSettings settings, int variant)
    {
        var set = SetOf(settings, variant);
        settings.BackgroundColor = set.Background;
        settings.FontColor = set.Font;
        settings.CourseInfoColor = set.CourseInfo;
        settings.NoiseTitleColor = set.NoiseTitle;
        settings.ProgressColor = set.Progress;
        settings.RainColor = set.Rain;
    }

    // ===== 1.4.0.0：主题色跟随 ClassIsland =====

    /// <summary>
    /// 把 CI 强调色单向同步进设置：跟随时写进 <see cref="PluginSettings.ThemeAccentColor"/>，
    /// 关闭跟随或 CI 拿不到强调色时置空（= 完全回退到本插件主题自带的配色）。
    ///
    /// 只写本插件自己的设置，**绝不回写 CI**（CI 那边爱什么颜色是它的事，我们只抄一份过来）。
    /// 返回 true 表示值变了，调用方需要刷新配色；值没变就不动，避免每秒刷新都触发保存与重绘。
    /// </summary>
    public static bool SyncAccent(PluginSettings settings, string? ciAccent)
    {
        var next = settings.FollowClassIslandAccent && !string.IsNullOrWhiteSpace(ciAccent)
            ? ciAccent.Trim()
            : "";
        if (string.Equals(settings.ThemeAccentColor, next, StringComparison.OrdinalIgnoreCase)) return false;
        settings.ThemeAccentColor = next;
        return true;
    }

    /// <summary>
    /// 进度条真正该用的颜色。
    ///
    /// 跟随开关打开且 CI 有强调色时，用强调色接管进度条——但**只在用户没手动改过进度条色**时：
    /// 判据是当前值与这张调色板的默认进度色一致（切主题会把该主题的默认值写进设置，
    /// 用户只要动过颜色选择器，值就会与默认值不同，此后永远保留用户自己选的颜色）。
    /// 关闭跟随 / CI 无强调色 / 用户自定义过 → 原样返回设置里的颜色，与 1.3.1.1 行为一致。
    /// </summary>
    public static string ResolveProgressColor(PluginSettings settings, ClockThemePalette palette)
    {
        var userColor = settings.ProgressColor;
        if (!settings.FollowClassIslandAccent) return userColor;
        if (string.IsNullOrWhiteSpace(settings.ThemeAccentColor)) return userColor;
        return IsPaletteDefault(userColor, palette.ProgressColor) ? settings.ThemeAccentColor : userColor;
    }

    /// <summary>
    /// 用当前已套用的主题变体解析进度条色（不读盘、不碰 CI）。
    /// 已套用变体 = 设置里 6 个颜色此刻归属的那套调色板，正好用来判断「这个值是不是默认值」。
    /// </summary>
    public static string ResolveProgressColor(PluginSettings settings)
        => ResolveProgressColor(settings, ClockTheme.PaletteOf(settings.AppliedPaletteVariant));

    /// <summary>两个 HEX 是否指同一个颜色（忽略大小写；格式不同（#RGB/#RRGGBB）时交给解析比较）。</summary>
    private static bool IsPaletteDefault(string value, string paletteDefault)
    {
        if (string.Equals(value, paletteDefault, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            return ColorEquals(value, paletteDefault);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析两个颜色串再比较：用户/主题里可能一个写 #RRGGBB、一个写 #AARRGGBB。</summary>
    private static bool ColorEquals(string a, string b)
    {
        var ca = ParseRgb(a);
        var cb = ParseRgb(b);
        return ca == cb;
    }

    /// <summary>把颜色串解析成可比较的 32 位 RGB（不依赖 Avalonia，保持本类可单测）。</summary>
    private static uint ParseRgb(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s.Substring(2);   // 去掉 AA
        if (s.Length == 3)
        {
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        }
        if (s.Length != 6) throw new FormatException($"无法解析颜色：{hex}");
        return Convert.ToUInt32(s, 16);
    }
}
