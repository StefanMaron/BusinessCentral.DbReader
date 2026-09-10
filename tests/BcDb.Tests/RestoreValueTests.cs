using System.Data.SqlTypes;
using System.Globalization;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The write-side conversion: from what the reader decoded to the CLR value the bulk-copy
/// client hands SQL Server.
///
/// The anchor is fixtures/typeprobe-probe-notnull.tsv — SELECT output from the oracle for
/// a table that carries every supported type as NOT NULL, at its extremes. Converting each
/// cell and rendering the result the way SQL Server renders it must reproduce that file
/// exactly. A conversion that rounded a decimal, dropped a fractional second, truncated a
/// blob or lost a code point would show up as a differing field, on a value a real SQL
/// Server produced.
/// </summary>
public class RestoreValueTests : IDisposable
{
    readonly IBcSource _src;
    readonly SourceTable _notnull;
    readonly TargetTable _target;

    public RestoreValueTests()
    {
        _src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
        _notnull = _src.Tables.Single(t => t.Name == "probe_notnull");
        _target = RestoreTestSchema.Mirror(_src, "probe_notnull");
    }

    public void Dispose() => _src.Dispose();

    /// <summary>
    /// The fixture's columns, in its order. n_real/n_float are absent from it on purpose
    /// (SQL Server's float-to-string form is not .NET's round-trip form) and are asserted
    /// separately below; n_ver is a rowversion and is never written at all.
    /// </summary>
    static readonly string[] FixtureCols =
    {
        "id", "n_tinyint", "n_smallint", "n_int", "n_bigint", "n_bit",
        "n_dec38_20", "n_dec18_2", "n_dec5_0", "n_datetime", "n_datetime2_7", "n_datetime2_0",
        "n_date", "n_time7", "n_time0", "n_guid", "n_nvarchar", "n_varchar", "n_nchar",
        "n_char", "n_binary", "n_varbinary", "n_vbmax", "n_nvmax",
    };

    SysColumn Src(string name) => _src.Columns(_notnull).Single(c => c.Name == name);
    TargetColumn Tgt(string name) => _target.Columns.Single(c => c.Name == name);

    object Convert1(string column, object? decoded) => RestoreValues.ToSqlValue(decoded, Src(column), Tgt(column));

    /// <summary>
    /// Renders a converted value the way SQL Server's own CONVERT(varchar, …, 121) does,
    /// which is what produced the fixture. Deliberately written from the target column's
    /// declared type rather than from anything the reader does, so the comparison is
    /// against the oracle's rendering and not against this codebase's.
    /// </summary>
    static string Render(object v, SysColumn c) => v switch
    {
        DBNull => "NULL",
        bool b => b ? "1" : "0",
        byte or short or int or long => System.Convert.ToString(v, CultureInfo.InvariantCulture)!,
        SqlDecimal d => d.ToString(),
        Guid g => g.ToString().ToUpperInvariant(),
        byte[] bytes => "0x" + System.Convert.ToHexString(bytes),
        TimeSpan t => t.ToString(c.Scale == 0 ? @"hh\:mm\:ss" : @"hh\:mm\:ss\." + new string('f', c.Scale),
            CultureInfo.InvariantCulture),
        DateTime dt => c.XType switch
        {
            40 => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            61 or 58 => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            42 => dt.ToString(c.Scale == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss." + new string('f', c.Scale),
                CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"no oracle rendering for date/time xtype {c.XType}"),
        },
        string s => s,
        _ => throw new NotSupportedException($"no oracle rendering for {v.GetType().Name}"),
    };

    [Fact]
    public void EveryNonNullableTypeConvertsBackToTheOracleValue()
    {
        var cols = FixtureCols.Select(Src).ToList();
        var rows = new List<string>();
        foreach (var row in _src.ReadRows(_notnull, cols))
            rows.Add(string.Join("|", cols.Select(c =>
                Render(RestoreValues.ToSqlValue(row[c.Name], c, Tgt(c.Name)), c))));
        rows.Sort(StringComparer.Ordinal);

        var expected = File.ReadAllLines(Path.Combine(RestoreTestSchema.Root, "fixtures", "typeprobe-probe-notnull.tsv"))
            .Where(l => l.Length > 0)
            // Drop the trailing "|#" sentinel and the rowversion field before it.
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .Select(l => l[..l.LastIndexOf('|')])
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(3, expected.Count);
        Assert.Equal(expected, rows);
    }

    [Fact]
    public void DecimalsWiderThanSystemDecimalSurviveExactly()
    {
        // The reason decimals cross as SqlDecimal. BC declares amounts decimal(38,20);
        // this value has 37 significant digits and System.Decimal holds 28-29.
        const string extreme = "99999999999999999.99999999999999999999";
        // System.Decimal does not refuse this value, which is what makes it dangerous: it
        // parses and quietly rounds the low digits away.
        Assert.True(decimal.TryParse(extreme, NumberStyles.Number, CultureInfo.InvariantCulture, out var rounded));
        Assert.NotEqual(extreme, rounded.ToString(CultureInfo.InvariantCulture));

        var v = Assert.IsType<SqlDecimal>(Convert1("n_dec38_20", extreme));
        Assert.Equal(SqlDecimal.Parse(extreme), v);
        Assert.Equal(38, v.Precision);
        Assert.Equal(20, v.Scale);
        Assert.Equal(extreme, v.ToString());
    }

    [Fact]
    public void FloatsCrossAsFloatsAndDoublesAsDoubles()
    {
        var byId = _src.ReadRows(_notnull, new[] { Src("id"), Src("n_real"), Src("n_float") })
            .ToDictionary(r => (long)r["id"]!, r => r);
        Assert.Equal(1.5f, Assert.IsType<float>(Convert1("n_real", byId[2]["n_real"])));
        Assert.Equal(2.25d, Assert.IsType<double>(Convert1("n_float", byId[2]["n_float"])));
        Assert.Equal(-1.5f, Assert.IsType<float>(Convert1("n_real", byId[3]["n_real"])));
        Assert.Equal(-2.25d, Assert.IsType<double>(Convert1("n_float", byId[3]["n_float"])));
    }

    [Theory]
    [InlineData("id", typeof(int))]
    [InlineData("n_tinyint", typeof(byte))]
    [InlineData("n_smallint", typeof(short))]
    [InlineData("n_bigint", typeof(long))]
    [InlineData("n_bit", typeof(bool))]
    [InlineData("n_dec38_20", typeof(SqlDecimal))]
    [InlineData("n_datetime", typeof(DateTime))]
    [InlineData("n_datetime2_7", typeof(DateTime))]
    [InlineData("n_date", typeof(DateTime))]
    [InlineData("n_time7", typeof(TimeSpan))]
    [InlineData("n_guid", typeof(Guid))]
    [InlineData("n_nvarchar", typeof(string))]
    [InlineData("n_char", typeof(string))]
    [InlineData("n_binary", typeof(byte[]))]
    [InlineData("n_vbmax", typeof(byte[]))]
    [InlineData("n_real", typeof(float))]
    [InlineData("n_float", typeof(double))]
    public void TheDeclaredClrTypeIsTheOneValuesConvertTo(string column, Type expected)
        => Assert.Equal(expected, RestoreValues.ClrType(Src(column), Tgt(column)));

    [Fact]
    public void AnIntegerColumnNarrowsToTheTargetsOwnWidth()
    {
        // The reader answers every integer type as long; what goes across has to be the
        // target's width or the client sends the wrong number of bytes.
        Assert.Equal((byte)255, Assert.IsType<byte>(Convert1("n_tinyint", 255L)));
        Assert.Equal((short)-32768, Assert.IsType<short>(Convert1("n_smallint", -32768L)));
        Assert.Equal(2147483647, Assert.IsType<int>(Convert1("n_int", 2147483647L)));
        Assert.Equal(long.MinValue, Assert.IsType<long>(Convert1("n_bigint", long.MinValue)));
    }

    [Fact]
    public void AnIntegerThatDoesNotFitTheTargetIsRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Convert1("n_tinyint", 256L));
        Assert.Contains("n_tinyint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("256", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tinyint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NullCrossesAsSqlNull()
        => Assert.Equal(DBNull.Value, Convert1("n_nvarchar", null));

    [Fact]
    public void ARowversionValueIsRefused()
    {
        // Nothing should ever ask for one — the plan drops rowversion columns — so being
        // asked means a caller built a mapping by hand and would get a rejected insert.
        var ex = Assert.Throws<NotSupportedException>(
            () => RestoreValues.ToSqlValue("0x00000000000007E9", Src("n_ver"), RestoreTestSchema.Mirror(Src("n_ver"), 27)));
        Assert.Contains("n_ver", ex.Message, StringComparison.Ordinal);
        Assert.Contains("rowversion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AValueOfTheWrongShapeIsRefusedByName()
    {
        // Not a defensive nicety: the decoders answer different CLR types per type family,
        // and a value arriving in the wrong one means the wrong column was read.
        var ex = Assert.Throws<InvalidDataException>(() => Convert1("n_datetime", 42L));
        Assert.Contains("n_datetime", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableBinaryValueIsRefused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Convert1("n_binary", "not-hex"));
        Assert.Contains("n_binary", ex.Message, StringComparison.Ordinal);
    }
}
