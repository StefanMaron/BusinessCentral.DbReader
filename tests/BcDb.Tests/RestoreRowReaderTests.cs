using System.Data;
using System.Data.SqlTypes;
using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The adapter a bulk copy consumes: source rows presented as an IDataReader, converted
/// on the way through. It streams — nothing here may materialise a table — and it answers
/// only the members a bulk copy actually calls, refusing the rest by name rather than
/// inventing a value for them.
/// </summary>
public class RestoreRowReaderTests : IDisposable
{
    readonly IBcSource _src;

    public RestoreRowReaderTests() => _src = BcSource.Open(RestoreTestSchema.TypeprobeBak);

    public void Dispose() => _src.Dispose();

    RestoreRowReader OpenReader(string table, out TablePlan plan)
    {
        var built = RestorePlanner.Build(_src, RestoreTestSchema.MirrorAll(_src), new RestoreOptions());
        plan = built.Tables.Single(p => p.Source.Name == table);
        var cols = plan.Columns.Select(c => c.Source).ToList();
        return new RestoreRowReader(table, _src.ReadRows(plan.Source, cols), plan.Columns);
    }

    [Fact]
    public void ItPresentsTheMappedColumnsUnderTheirTargetNames()
    {
        using var r = OpenReader("probe_dense", out var plan);
        Assert.Equal(plan.Columns.Count, r.FieldCount);
        Assert.Equal("id", r.GetName(0));
        Assert.Equal(0, r.GetOrdinal("id"));
        Assert.Equal(r.GetOrdinal("amount"), r.GetOrdinal("AMOUNT"));      // SQL names are case-insensitive
        Assert.Equal(typeof(SqlDecimal), r.GetFieldType(r.GetOrdinal("amount")));
        Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("nope"));
    }

    [Fact]
    public void ItYieldsEveryRowWithConvertedValues()
    {
        using var r = OpenReader("probe_notnull", out _);
        int id = r.GetOrdinal("id"), dec = r.GetOrdinal("n_dec38_20"), guid = r.GetOrdinal("n_guid");
        var seen = new List<(int Id, SqlDecimal Amount, Guid G)>();
        while (r.Read())
            seen.Add(((int)r.GetValue(id), (SqlDecimal)r.GetValue(dec), (Guid)r.GetValue(guid)));

        Assert.Equal(3, seen.Count);
        Assert.Equal(3, r.RowsRead);
        Assert.Equal(new[] { 1, 2, 3 }, seen.Select(s => s.Id).OrderBy(x => x));
        Assert.Equal(SqlDecimal.Parse("99999999999999999.99999999999999999999"),
            seen.Single(s => s.Id == 2).Amount);
        Assert.Equal(Guid.Parse("12345678-9ABC-DEF0-1234-56789ABCDEF0"), seen.Single(s => s.Id == 3).G);
        Assert.False(r.Read());
    }

    [Fact]
    public void ItStreamsRatherThanMaterialising()
    {
        // probe_dense has 4000 rows; reading one row must not have pulled the rest.
        int pulled = 0;
        var plan = RestorePlanner.Build(_src, RestoreTestSchema.MirrorAll(_src), new RestoreOptions())
            .Tables.Single(p => p.Source.Name == "probe_dense");
        var cols = plan.Columns.Select(c => c.Source).ToList();
        IEnumerable<IReadOnlyDictionary<string, object?>> Counted()
        {
            foreach (var row in _src.ReadRows(plan.Source, cols)) { pulled++; yield return row; }
        }
        using var r = new RestoreRowReader("probe_dense", Counted(), plan.Columns);
        Assert.True(r.Read());
        Assert.Equal(1, pulled);
        Assert.Equal(1, r.RowsRead);
    }

    [Fact]
    public void MembersABulkCopyDoesNotUseRefuseByName()
    {
        using var r = OpenReader("probe_dense", out _);
        Assert.True(r.Read());
        var ex = Assert.Throws<NotSupportedException>(() => r.GetString(0));
        Assert.Contains("GetString", ex.Message, StringComparison.Ordinal);
        Assert.Contains("probe_dense", ex.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => r.GetSchemaTable());
        Assert.False(((IDataReader)r).NextResult());
    }
}
