using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// Shared support for the restore tests: the repository root, an open typeprobe source,
/// and a target schema built to mirror it.
///
/// Mirroring is the workflow's own premise. `bcdb restore` writes into a container whose
/// schema was made by installing the same extensions the source was exported from, so the
/// expected case is that every source table and column has an identically named and typed
/// home in the target. The negative tests below take this mirror and break one thing about
/// it — a missing column, a narrower one, a different type — which is exactly how the
/// mismatch shows up in practice: one extension at the wrong version.
/// </summary>
public static class RestoreTestSchema
{
    public static string Root
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "BcDb.sln")))
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return dir!;
        }
    }

    public static string TypeprobeBak => Path.Combine(Root, "fixtures", "typeprobe.bak");

    /// <summary>The target column a live SQL Server would report for this source column.</summary>
    public static TargetColumn Mirror(SysColumn c, int ordinal) => new(
        c.Name, ordinal, c.TypeName, c.MaxLength, c.Precision, c.Scale,
        IsNullable: true, IsIdentity: false, IsComputed: false, HasDefault: false);

    /// <summary>A target table that mirrors one table of the source exactly.</summary>
    public static TargetTable Mirror(IBcSource src, string table)
    {
        var t = src.Tables.Single(x => x.Name == table);
        return new TargetTable("dbo", table,
            src.Columns(t).Select((c, i) => Mirror(c, i + 1)).ToList());
    }

    /// <summary>A target database that mirrors every table of the source.</summary>
    public static List<TargetTable> MirrorAll(IBcSource src)
        => src.Tables.Select(t => new TargetTable("dbo", t.Name,
                src.Columns(t).Select((c, i) => Mirror(c, i + 1)).ToList()))
            .ToList();

    /// <summary>The same target table with one column replaced by <paramref name="replacement"/>, or dropped when null.</summary>
    public static TargetTable With(this TargetTable t, string column, TargetColumn? replacement)
    {
        var cols = new List<TargetColumn>();
        int hits = 0;
        foreach (var c in t.Columns)
        {
            if (!c.Name.Equals(column, StringComparison.OrdinalIgnoreCase)) { cols.Add(c); continue; }
            hits++;
            if (replacement != null) cols.Add(replacement);
        }
        // A test that mutates a column the table does not have would assert nothing.
        Assert.Equal(1, hits);
        return t with { Columns = cols };
    }

    /// <summary>The same target table with one extra column the source does not have.</summary>
    public static TargetTable Plus(this TargetTable t, TargetColumn extra)
        => t with { Columns = t.Columns.Append(extra).ToList() };

    /// <summary>Replace one table of a mirrored target database.</summary>
    public static List<TargetTable> Replace(this List<TargetTable> db, TargetTable t)
        => db.Select(x => x.Name == t.Name ? t : x).ToList();

    /// <summary>
    /// The wrapped source's own tables, plus one more per <paramref name="aliases"/>: a
    /// <see cref="SourceTable"/> under a new name whose columns and rows are exactly
    /// <paramref name="borrowFrom"/>'s. Column/row lookup keys off the borrowed table's own
    /// handle, never its name, so the alias reads as a real, independent table — this is
    /// how a check against real BC table names (RestorePlanner's container-identity list,
    /// which the committed typeprobe.bak has no matching names for and must not be hand-
    /// edited to add — oracle-verification.md) gets exercised against a name it actually
    /// recognizes, without needing that name in the fixture itself.
    /// </summary>
    public static IBcSource WithAliases(this IBcSource src, string borrowFrom, params string[] aliases)
        => new AliasedSource(src, borrowFrom, aliases);

    /// <summary>
    /// The wrapped source's own tables, plus one raw <see cref="SourceTable"/> per
    /// <paramref name="extras"/> — a name and a row count, nothing borrowed. For a check
    /// that only ever calls <c>.Name</c>/<c>.RowCount()</c> (company auto-detection's
    /// data-filter, in particular), which is everything a fake handle that is never
    /// dereferenced needs to get right.
    /// </summary>
    public static IBcSource WithExtraTables(this IBcSource src, params (string Name, long RowCount)[] extras)
        => new ExtraTablesSource(src, extras);

    sealed class AliasedSource(IBcSource inner, string borrowFrom, string[] aliases) : IBcSource
    {
        readonly SourceTable _borrowed = inner.Tables.Single(t => t.Name == borrowFrom);
        List<SourceTable>? _tables;

        public IReadOnlyList<SourceTable> Tables => _tables ??= inner.Tables
            .Concat(aliases.Select(a => new SourceTable(a, _borrowed.Compression, _borrowed.RowCountProvider, _borrowed.Handle)))
            .ToList();

        public IReadOnlyList<SysColumn> Columns(SourceTable t) => inner.Columns(t);
        public IReadOnlyList<string> RowKeyColumns(SourceTable t) => inner.RowKeyColumns(t);
        public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(SourceTable t, IReadOnlyList<SysColumn> columns)
            => inner.ReadRows(t, columns);
        public void PreloadMetadata() => inner.PreloadMetadata();
        public string Banner => inner.Banner;
        public void Dispose() { } // the wrapped source is disposed by whoever opened it
    }

    sealed class ExtraTablesSource(IBcSource inner, (string Name, long RowCount)[] extras) : IBcSource
    {
        List<SourceTable>? _tables;

        public IReadOnlyList<SourceTable> Tables => _tables ??= inner.Tables
            .Concat(extras.Select(e => new SourceTable(e.Name, "none", () => e.RowCount, new object())))
            .ToList();

        public IReadOnlyList<SysColumn> Columns(SourceTable t) => inner.Columns(t);
        public IReadOnlyList<string> RowKeyColumns(SourceTable t) => inner.RowKeyColumns(t);
        public IEnumerable<IReadOnlyDictionary<string, object?>> ReadRows(SourceTable t, IReadOnlyList<SysColumn> columns)
            => inner.ReadRows(t, columns);
        public void PreloadMetadata() => inner.PreloadMetadata();
        public string Banner => inner.Banner;
        public void Dispose() { }
    }
}
