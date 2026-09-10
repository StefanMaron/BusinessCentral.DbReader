using System.Data.SqlTypes;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BusinessCentral.DbReader;

/// <summary>
/// Turns a decoded reader value into the CLR value the bulk-copy client hands to SQL
/// Server for that target column.
///
/// The reader's job is to print faithfully, so it answers in display form: a decimal as a
/// plain string with exactly its declared scale, a datetime as "yyyy-MM-dd HH:mm:ss.fff",
/// binary as "0x…", a GUID as upper-case text (Values.cs). Writing needs the value, not
/// its rendering, and every conversion back is exact or it throws: the loud-failures rule
/// applies at least as hard on the write side, because a value that lands wrong in a
/// container looks exactly like one that landed right.
///
/// decimal is the reason this is not a one-liner. BC declares amounts decimal(38,20), and
/// System.Decimal holds 28-29 significant digits — the probe database's own
/// 99999999999999999.99999999999999999999 has 37 and does not fit. SqlDecimal is the
/// 38-digit type SQL Server's own client uses for exactly this, so decimals go across as
/// SqlDecimal and nothing is rounded on the way in.
/// </summary>
public static class RestoreValues
{
    /// <summary>
    /// The CLR type values of this source column are converted to. The trimmer annotation
    /// matches IDataRecord.GetFieldType's, which is where this answer ends up.
    /// </summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public static Type ClrType(SysColumn source, TargetColumn target) => source.XType switch
    {
        104 => typeof(bool),
        48 or 52 or 56 or 127 => IntegerType(source, target),
        106 or 108 => typeof(SqlDecimal),
        61 or 58 or 42 or 40 => typeof(DateTime),
        41 => typeof(TimeSpan),
        36 => typeof(Guid),
        231 or 239 or 167 or 175 or 35 or 99 or 241 => typeof(string),
        165 or 173 or 34 => typeof(byte[]),
        59 => typeof(float),
        62 => typeof(double),
        189 => throw RowVersion(source),
        _ => throw new NotSupportedException(
            $"column {source.Name}: type {source.TypeName} cannot be written by bcdb restore"),
    };

    /// <summary>Integers all decode as long; what crosses the wire has to be the target's own width.</summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    static Type IntegerType(SysColumn source, TargetColumn target) => target.TypeName switch
    {
        "tinyint" => typeof(byte),
        "smallint" => typeof(short),
        "int" => typeof(int),
        "bigint" => typeof(long),
        _ => throw new NotSupportedException(
            $"column {source.Name}: {source.TypeName} in the source, {target.TypeName} in the target — not an integer column"),
    };

    static NotSupportedException RowVersion(SysColumn c)
        => new($"column {c.Name} is a rowversion — SQL Server stamps it on insert and refuses a supplied value");

    /// <summary>
    /// The value to write, or <see cref="DBNull.Value"/> for SQL NULL. Throws naming the
    /// column when the decoded value cannot be carried into the target column exactly.
    /// </summary>
    public static object ToSqlValue(object? decoded, SysColumn source, TargetColumn target)
    {
        if (source.XType == 189) throw RowVersion(source);
        if (decoded is null) return DBNull.Value;
        return source.XType switch
        {
            104 => Expect<bool>(decoded, source),
            48 or 52 or 56 or 127 => Integer(Expect<long>(decoded, source), source, target),
            106 or 108 => Decimal(Expect<string>(decoded, source), source, target),
            61 or 58 or 42 or 40 => Moment(Expect<string>(decoded, source), source),
            41 => Time(Expect<string>(decoded, source), source),
            36 => Uuid(Expect<string>(decoded, source), source),
            231 or 239 or 167 or 175 or 35 or 99 or 241 => Expect<string>(decoded, source),
            165 or 173 or 34 => Binary(Expect<string>(decoded, source), source),
            59 => Expect<float>(decoded, source),
            62 => Expect<double>(decoded, source),
            _ => throw new NotSupportedException(
                $"column {source.Name}: type {source.TypeName} cannot be written by bcdb restore"),
        };
    }

    /// <summary>
    /// The decoders answer a different CLR type per type family, so a value arriving in
    /// the wrong one means the wrong column was read — never something to convert past.
    /// </summary>
    static T Expect<T>(object v, SysColumn c)
        => v is T t ? t : throw new InvalidDataException(
            $"column {c.Name} ({c.TypeName}): the reader answered a {v.GetType().Name} where a "
            + $"{typeof(T).Name} was expected — refusing to guess a conversion");

    static object Integer(long v, SysColumn c, TargetColumn target)
    {
        switch (target.TypeName)
        {
            case "tinyint" when v is >= byte.MinValue and <= byte.MaxValue: return (byte)v;
            case "smallint" when v is >= short.MinValue and <= short.MaxValue: return (short)v;
            case "int" when v is >= int.MinValue and <= int.MaxValue: return (int)v;
            case "bigint": return v;
            case "tinyint" or "smallint" or "int":
                throw new InvalidDataException(
                    $"column {c.Name}: the value {v} does not fit the target's {target.TypeName}");
            default:
                throw new NotSupportedException(
                    $"column {c.Name}: {c.TypeName} in the source, {target.TypeName} in the target — not an integer column");
        }
    }

    /// <summary>
    /// Parsed as SqlDecimal, then fixed at the target's declared precision and scale so
    /// what arrives is exactly the column's type. System.Decimal is not an option here:
    /// BC's decimal(38,20) holds values with more significant digits than it has.
    /// </summary>
    static object Decimal(string s, SysColumn c, TargetColumn target)
    {
        SqlDecimal d;
        try { d = SqlDecimal.Parse(s); }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
        {
            throw new InvalidDataException($"column {c.Name}: '{s}' is not a decimal value ({ex.Message})");
        }
        try { return SqlDecimal.ConvertToPrecScale(d, target.Precision, target.Scale); }
        catch (SqlTruncateException ex)
        {
            throw new InvalidDataException(
                $"column {c.Name}: {s} does not fit the target's decimal({target.Precision},{target.Scale}) ({ex.Message})");
        }
    }

    static object Moment(string s, SysColumn c)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : throw new InvalidDataException($"column {c.Name} ({c.TypeName}): '{s}' is not a date/time value");

    static object Time(string s, SysColumn c)
        => TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t)
            ? t
            : throw new InvalidDataException($"column {c.Name} (time): '{s}' is not a time of day");

    static object Uuid(string s, SysColumn c)
        => Guid.TryParse(s, out var g)
            ? g
            : throw new InvalidDataException($"column {c.Name} (uniqueidentifier): '{s}' is not a GUID");

    static object Binary(string s, SysColumn c)
    {
        if (!s.StartsWith("0x", StringComparison.Ordinal))
            throw new InvalidDataException($"column {c.Name} ({c.TypeName}): '{s}' is not a 0x… binary value");
        try { return Convert.FromHexString(s.AsSpan(2)); }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"column {c.Name} ({c.TypeName}): '{s}' is not hexadecimal ({ex.Message})");
        }
    }
}
