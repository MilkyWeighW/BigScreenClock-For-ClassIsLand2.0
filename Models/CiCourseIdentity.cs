using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Enums;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 从 ClassIsland 的课程服务里取「当前这节课」的科目 Id。
///
/// 为什么要这一层：CI 的 <see cref="IPublicLessonsService"/> 只给 <c>CurrentSubject</c>（一个值对象，
/// 只有 Name/Initial/TeacherName），两个同名科目在它眼里完全一样。真正的身份是课表里
/// <c>ClassInfo.SubjectId</c>（Subjects 字典的 Guid 键）。课表又是按「第几个上课时段」顺序对 Classes 的，
/// 所以这里用「当前时间点对象」去 Classes 里反查那条记录，拿到 Id。
/// </summary>
public static class CiCourseIdentity
{
    /// <summary>
    /// 当前正在上的那节课的科目 Id（Guid 字符串）。拿不到（没上课 / 课表没加载 / API 形状不符）返回 null。
    /// </summary>
    public static string? TryGetCurrentClassSubjectId(ILessonsService? lessons)
    {
        try
        {
            if (lessons is null) return null;
            if (lessons.CurrentState != TimeState.OnClass) return null;
            if (lessons.CurrentClassPlan is not { } plan) return null;

            var current = lessons.CurrentTimeLayoutItem;
            if (current is null) return null;

            foreach (var info in plan.Classes)
            {
                if (info.SubjectId == Guid.Empty) continue;

                var item = info.CurrentTimeLayoutItem;
                if (item is null) continue;

                // 正常情况下两边是同一个对象；万一 CI 内部重新生成过时间点，就按起止时刻再对一次。
                var same = ReferenceEquals(item, current)
                           || (item.StartTime == current.StartTime && item.EndTime == current.EndTime);
                if (same) return info.SubjectId.ToString();
            }
        }
        catch
        {
            // CI 版本差异导致 API 形状变化时不该把调用方（自动触发/计数）拖下水
        }

        return null;
    }
}
