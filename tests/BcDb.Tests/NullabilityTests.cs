using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// Column nullability, for both containers, against fixtures/typeprobe-nullability.tsv —
/// sys.columns.is_nullable for every column of every user table of the probe database,
/// exported from the oracle.
///
/// The reader never needed this: a record's null bitmap has a bit for a column whether or
/// not the column accepts NULL, so nullability is invisible in the row. Recreating the
/// table somewhere else needs it, which is what `bcdb restore --create-missing` does — and
/// getting it wrong there produces a table BC would not accept.
/// </summary>
public class NullabilityTests
{
    static List<string> Fixture()
        => File.ReadAllLines(Path.Combine(RestoreTestSchema.Root, "fixtures", "typeprobe-nullability.tsv"))
            .Where(l => l.Length > 0)
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

    static List<string> FromSource(string file)
    {
        using var src = BcSource.Open(Path.Combine(RestoreTestSchema.Root, "fixtures", file));
        var lines = new List<string>();
        foreach (var t in src.Tables)
            foreach (var c in src.Columns(t))
                lines.Add($"{t.Name}|{c.Name}|{(c.IsNullable ? 1 : 0)}");
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    [Fact]
    public void BakNullabilityMatchesTheOracle()
    {
        var expected = Fixture();
        Assert.Equal(383, expected.Count);
        Assert.Equal(expected, FromSource("typeprobe.bak"));
    }

    [Fact]
    public void BacpacNullabilityMatchesTheOracle()
    {
        // sqlpackage does not export change tracking's internal side tables, so the bacpac
        // carries a subset; every column it does carry must agree.
        var expected = Fixture().ToHashSet(StringComparer.Ordinal);
        var actual = FromSource("typeprobe.bacpac");
        Assert.NotEmpty(actual);
        var wrong = actual.Where(a => !expected.Contains(a)).ToList();
        Assert.Empty(wrong);
    }

    [Fact]
    public void NotNullAndNullableAreBothRepresented()
    {
        // A test that passed by answering "nullable" to everything would be worthless.
        var actual = FromSource("typeprobe.bak");
        Assert.Equal(64, actual.Count(a => a.EndsWith("|0", StringComparison.Ordinal)));
        Assert.Equal(319, actual.Count(a => a.EndsWith("|1", StringComparison.Ordinal)));
    }
}
