using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoursePlayer.Data;

/// <summary>
/// Stores a <see cref="DateTimeOffset"/> as UTC ticks.
/// <para>
/// SQLite has no native date type, and EF's default mapping is TEXT — which it refuses to
/// use in ORDER BY. Persisting the instant as an INTEGER keeps sorting (Recent, progress
/// history) working in SQL rather than forcing client-side ordering. The original UTC
/// offset is not retained; the app only ever renders local time.
/// </para>
/// </summary>
public sealed class DateTimeOffsetToUtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToUtcTicksConverter()
        : base(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}

/// <summary>
/// Stores a <see cref="TimeSpan"/> as ticks so durations sort and sum in SQL. EF's default
/// TEXT mapping compares lexicographically, which breaks the moment a value passes 24 hours.
/// </summary>
public sealed class TimeSpanToTicksConverter : ValueConverter<TimeSpan, long>
{
    public TimeSpanToTicksConverter()
        : base(
            value => value.Ticks,
            ticks => TimeSpan.FromTicks(ticks))
    {
    }
}
