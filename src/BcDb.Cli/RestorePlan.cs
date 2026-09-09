namespace BusinessCentral.DbReader;

/// <summary>
/// One column of a target table, as sys.columns describes it on the live server the rows
/// are being written into. This is the container's schema, not the backup's: a restore
/// writes into a database somebody else created (a BC container with its extensions
/// already installed), so every decision below is made against what that database
/// actually declares rather than against what the source file happens to carry.
/// </summary>
public sealed record TargetColumn(
    string Name, int ColumnId, string TypeName, short MaxLength, byte Precision, byte Scale,
    bool IsNullable, bool IsIdentity, bool IsComputed, bool HasDefault)
{
    /// <summary>A rowversion is stamped by the engine; it can never be written.</summary>
    public bool IsRowVersion => TypeName is "timestamp" or "rowversion";

    /// <summary>Columns a client may supply a value for.</summary>
    public bool IsWritable => !IsComputed && !IsRowVersion;
}

/// <summary>A table of the target database and the columns it declares.</summary>
public sealed record TargetTable(string Schema, string Name, IReadOnlyList<TargetColumn> Columns)
{
    /// <summary>Bracket-quoted two-part name, safe to interpolate into a statement.</summary>
    public string QuotedName => $"[{Schema.Replace("]", "]]")}].[{Name.Replace("]", "]]")}]";
}

/// <summary>A source column and the target column its values are written into.</summary>
public sealed record ColumnMapping(SysColumn Source, TargetColumn Target);

/// <summary>What the restore will do to one table: which columns move, and which do not.</summary>
public sealed record TablePlan(
    SourceTable Source,
    TargetTable Target,
    IReadOnlyList<ColumnMapping> Columns,
    IReadOnlyList<string> TargetOnlyColumns)
{
    /// <summary>The target's own identity values are replaced by the source's, so IDENTITY_INSERT is needed.</summary>
    public bool NeedsIdentityInsert => Columns.Any(c => c.Target.IsIdentity);

    /// <summary>The target has no such table; it is created from the source's schema before loading.</summary>
    public bool CreateTable { get; init; }

    /// <summary>Source columns the target's table lacks; added to it before loading.</summary>
    public IReadOnlyList<SysColumn> AddColumns { get; init; } = Array.Empty<SysColumn>();

    /// <summary>Every source column in source order, rowversion included — what CREATE TABLE emits.</summary>
    public IReadOnlyList<SysColumn> SourceColumns { get; init; } = Array.Empty<SysColumn>();

    /// <summary>The source's key columns, which become the created table's clustered primary key.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = Array.Empty<string>();
}

/// <summary>A source table that will not be written, and why — always reported, never silent.</summary>
public sealed record SkippedTable(string Name, string Reason);

/// <summary>Everything the caller chose on the command line that shapes the plan.</summary>
public sealed record RestoreOptions
{
    /// <summary>Only these source tables (raw SQL object names), or empty for every one of them.</summary>
    public IReadOnlyCollection<string> OnlyTables { get; init; } = Array.Empty<string>();

    /// <summary>
    /// These source tables (raw SQL object names) are left alone entirely — not created,
    /// not written, target rows untouched. Checked before every other rule, including
    /// --table: it always wins, the same way $ndo$ tables always win over --table.
    ///
    /// For restoring into a database that is already running, not a blank one: the
    /// container's own login/session/company-identity tables — `User`, `Access Control`,
    /// `User Personalization`, the platform's `$ndo$tenantcompany` registry — are not part
    /// of the exported tenant's business data, they are how *this* server knows who can
    /// sign in and what it currently considers the tenant's companies. Restoring them
    /// replaces the container's own working identity with the source's, which is business
    /// data for the source's server, not something a target server should adopt.
    /// $ndo$-prefixed tables already default to excluded (<see cref="IncludeSystem"/>);
    /// this is the same idea for ordinary-looking tables that also happen to be
    /// container-local rather than business data.
    /// </summary>
    public IReadOnlyCollection<string> ExcludeTables { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Include the platform's own bookkeeping tables — the ones whose SQL name starts with
    /// '$', such as $ndo$dbproperty and $ndo$navappinstalledapp. They are excluded by
    /// default because they describe the *service tier's* view of the database it is
    /// attached to: which apps are installed, which tenant this is, which schema version
    /// the NST expects. A container has its own answers to those, put there by the
    /// extensions actually installed in it, and overwriting them with a cloud tenant's
    /// answers breaks the container rather than filling it with data. Excluded tables are
    /// listed in the plan like any other skip, and this switch turns them back on.
    /// </summary>
    public bool IncludeSystem { get; init; }

    /// <summary>Empty each target table before loading it. Without it a non-empty target is refused.</summary>
    public bool Replace { get; init; }

    /// <summary>
    /// Create what the target does not have: a table the source has and the target lacks,
    /// and a column missing from a table that does exist. On by default.
    ///
    /// This is what makes the command useful against a Business Central container. BC
    /// keeps a table's data when its extension is uninstalled and picks the table up again
    /// when the extension is reinstalled, so a table that exists before its owner does is
    /// a state BC already handles — and a column or a table it does not know about is
    /// ignored rather than resented. Creating is therefore the permissive, useful default;
    /// --no-create turns it off for a target whose schema must not be touched.
    /// </summary>
    public bool CreateMissing { get; init; } = true;

    /// <summary>
    /// Refuse the whole restore when any table cannot be reconciled, instead of reporting
    /// that table and carrying on.
    ///
    /// Off by default. The plan is built before anything is written, so either way nothing
    /// lands from a table that does not line up; the difference is whether the other 4,000
    /// tables still load. Turn it on when a partial restore would be worse than none.
    /// </summary>
    public bool Strict { get; init; }

    /// <summary>Rows per bulk-copy batch.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>Build and print the plan, write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Matches the source's tables and columns to the target database's by name, creating what
/// the target does not have, and refusing only what it cannot carry across intact.
///
/// The bias is deliberately toward getting the data in. Business Central tolerates a
/// database that holds more than its extensions declare: a table or column nothing owns is
/// ignored, and a table that already exists when its extension is installed is adopted
/// rather than rejected — that is the same path an uninstall/reinstall takes, which keeps
/// a table's data across the gap. So a table the target lacks is created from the source's
/// own schema, and a column missing from a table that exists is added.
///
/// What is still refused is the case where a value would arrive as a different value: a
/// target column narrower than the source's, a different type, a different decimal scale.
/// Those tables are reported and skipped (or, with --strict, stop the run) — the plan is
/// built before anything is written, so nothing from them lands either way.
/// </summary>
public static class RestorePlanner
{
    /// <summary>The reason a table filtered out by --table carries, so the report can count them rather than list them.</summary>
    public const string FilteredOut = "not named by --table";

    /// <summary>The reason a table named by --exclude-table carries.</summary>
    public const string ExcludedOut = "excluded by --exclude-table: left untouched";

    /// <summary>Source tables whose SQL name starts with '$' are the platform's own — see <see cref="RestoreOptions.IncludeSystem"/>.</summary>
    public static bool IsSystemTable(string sqlName) => sqlName.StartsWith('$');

    public static (IReadOnlyList<TablePlan> Tables, IReadOnlyList<SkippedTable> Skipped) Build(
        IBcSource source, IReadOnlyList<TargetTable> target, RestoreOptions opts)
    {
        var byName = new Dictionary<string, List<TargetTable>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in target)
        {
            if (!byName.TryGetValue(t.Name, out var list)) byName[t.Name] = list = new List<TargetTable>();
            list.Add(t);
        }
        var only = new HashSet<string>(opts.OnlyTables, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(opts.ExcludeTables, StringComparer.OrdinalIgnoreCase);
        var plans = new List<TablePlan>();
        var skipped = new List<SkippedTable>();

        foreach (var st in source.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (exclude.Contains(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name, ExcludedOut));
                continue;
            }
            if (only.Count > 0 && !only.Contains(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name, FilteredOut));
                continue;
            }
            if (!opts.IncludeSystem && IsSystemTable(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name,
                    "a platform table: it describes the service tier's own view of the database, which belongs to "
                    + "the container — pass --include-system to write it anyway"));
                continue;
            }
            byName.TryGetValue(st.Name, out var hits);
            if (hits is null && !opts.CreateMissing)
            {
                skipped.Add(new SkippedTable(st.Name,
                    "no table of this name in the target database, and --no-create was given"));
                continue;
            }
            if (hits is { Count: > 1 })
                throw new InvalidDataException(
                    $"the target database has {hits.Count} tables named {st.Name} "
                    + $"(schemas {string.Join(", ", hits.Select(h => h.Schema).OrderBy(x => x, StringComparer.Ordinal))}) "
                    + "— refusing to guess which one the source's rows belong in");
            try { plans.Add(BuildTable(source, st, hits?[0], opts)); }
            catch (InvalidDataException) when (opts.Strict) { throw; }
            catch (InvalidDataException ex)
            {
                skipped.Add(new SkippedTable(st.Name, ex.Message));
            }
        }
        return (plans, skipped);
    }

    /// <summary>
    /// The plan for one table. <paramref name="existing"/> is null when the target has no
    /// such table and it is to be created.
    /// </summary>
    static TablePlan BuildTable(IBcSource source, SourceTable st, TargetTable? existing, RestoreOptions opts)
    {
        var srcCols = source.Columns(st);
        var keyColumns = source.RowKeyColumns(st);
        bool create = existing is null;
        var tt = existing ?? new TargetTable("dbo", st.Name, Array.Empty<TargetColumn>());

        var mappings = new List<ColumnMapping>();
        var addColumns = new List<SysColumn>();
        var columns = tt.Columns.ToList();
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sc in srcCols)
        {
            // A rowversion in the source is not data: the engine that wrote it stamped it,
            // and the engine receiving these rows will stamp its own. It is still part of
            // the table's shape, so a created table gets one.
            if (sc.XType == 189) continue;

            var hit = columns.FirstOrDefault(c => c.Name.Equals(sc.Name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                if (!opts.CreateMissing)
                    throw new InvalidDataException(
                        $"{st.Name}.{sc.Name} ({TypeText(sc)}) has no column in target table {tt.QuotedName}, "
                        + "and --no-create was given — refusing to load rows that would silently lose it");
                hit = Synthesize(sc, columns.Count + 1);
                columns.Add(hit);
                if (!create) addColumns.Add(sc);
            }
            else if (hit.IsRowVersion)
            {
                throw new InvalidDataException(
                    $"{st.Name}.{sc.Name} is {TypeText(sc)} in the source and a rowversion in the target — "
                    + "a rowversion cannot be written, so this column's values have nowhere to go");
            }
            // A computed target column derives its own value from the columns around it;
            // not writing it loses nothing.
            else if (hit.IsComputed) continue;
            else CheckCompatible(st.Name, sc, hit);

            mappings.Add(new ColumnMapping(sc, hit));
            mapped.Add(hit.Name);
        }

        var targetOnly = new List<string>();
        foreach (var tc in columns)
        {
            if (mapped.Contains(tc.Name) || !tc.IsWritable) continue;
            if (tc.IsIdentity || tc.IsNullable || tc.HasDefault) { targetOnly.Add(tc.Name); continue; }
            throw new InvalidDataException(
                $"target column {tt.QuotedName}.{tc.Name} ({TypeText(tc)}) is NOT NULL with no default, and "
                + $"{st.Name} has no column of that name — every row would be rejected, so the load stops here "
                + "rather than part way through");
        }

        return new TablePlan(st, tt with { Columns = columns }, mappings, targetOnly)
        {
            CreateTable = create,
            AddColumns = addColumns,
            SourceColumns = srcCols,
            KeyColumns = keyColumns,
        };
    }

    /// <summary>The target column a source column becomes when the target has to grow one.</summary>
    static TargetColumn Synthesize(SysColumn c, int ordinal) => new(
        c.Name, ordinal, c.TypeName, c.MaxLength, c.Precision, c.Scale,
        IsNullable: c.IsNullable, IsIdentity: false, IsComputed: false, HasDefault: false);

    /// <summary>
    /// Refuses any mapping that would not carry every value across intact. Widening is
    /// fine — a target nvarchar(200) holds everything an nvarchar(100) did — narrowing,
    /// re-scaling and type changes are not, because each of them turns some values into
    /// different values without anything failing at load time.
    /// </summary>
    static void CheckCompatible(string table, SysColumn s, TargetColumn t)
    {
        if (!SameType(s.TypeName, t.TypeName))
            throw new InvalidDataException(
                $"{table}.{s.Name} is {TypeText(s)} in the source and {TypeText(t)} in the target — "
                + "refusing to convert between types");

        switch (s.TypeName)
        {
            case "decimal" or "numeric":
                if (t.Scale != s.Scale || t.Precision < s.Precision)
                    throw new InvalidDataException(
                        $"{table}.{s.Name} is {TypeText(s)} in the source and {TypeText(t)} in the target — "
                        + "a different scale or a lower precision cannot hold every value the source has");
                break;
            case "nvarchar" or "nchar" or "varchar" or "char" or "binary" or "varbinary":
                // MaxLength is in bytes, −1 for (max); a (max) source never fits a sized target.
                if (t.MaxLength >= 0 && (s.MaxLength < 0 || s.MaxLength > t.MaxLength))
                    throw new InvalidDataException(
                        $"{table}.{s.Name} is {TypeText(s)} in the source and {TypeText(t)} in the target — "
                        + "the target is narrower and would truncate");
                break;
            case "time" or "datetime2" or "datetimeoffset":
                if (t.Scale < s.Scale)
                    throw new InvalidDataException(
                        $"{table}.{s.Name} is {TypeText(s)} in the source and {TypeText(t)} in the target — "
                        + "the target holds fewer fractional digits and would round");
                break;
        }
    }

    /// <summary>decimal and numeric are the same type under two names; nothing else is.</summary>
    static bool SameType(string a, string b)
        => a.Equals(b, StringComparison.OrdinalIgnoreCase)
           || (Decimalish(a) && Decimalish(b));

    static bool Decimalish(string t) => t is "decimal" or "numeric";

    static string TypeText(SysColumn c) => TypeText(c.TypeName, c.MaxLength, c.Precision, c.Scale);
    static string TypeText(TargetColumn c) => TypeText(c.TypeName, c.MaxLength, c.Precision, c.Scale);

    /// <summary>"nvarchar(100)", "decimal(38,20)", "datetime2(7)", "int" — the way the DDL says it.</summary>
    static string TypeText(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "nvarchar" or "nchar" => type + (maxLength < 0 ? "(max)" : $"({maxLength / 2})"),
        "varchar" or "char" or "varbinary" or "binary" => type + (maxLength < 0 ? "(max)" : $"({maxLength})"),
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "time" or "datetime2" or "datetimeoffset" => $"{type}({scale})",
        _ => type,
    };
}
