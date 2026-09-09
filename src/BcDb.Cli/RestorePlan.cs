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
}

/// <summary>A source table that will not be written, and why — always reported, never silent.</summary>
public sealed record SkippedTable(string Name, string Reason);

/// <summary>Everything the caller chose on the command line that shapes the plan.</summary>
public sealed record RestoreOptions
{
    /// <summary>Only these source tables (raw SQL object names), or empty for every one of them.</summary>
    public IReadOnlyCollection<string> OnlyTables { get; init; } = Array.Empty<string>();

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
    /// Report a table whose columns do not line up as skipped and carry on, instead of
    /// refusing the whole restore.
    ///
    /// The default is to refuse, and it is the right default: the plan is built before
    /// anything is written, so a mismatch normally means the container has the wrong
    /// version of an extension installed and the honest answer is to stop while the
    /// database is still untouched. This exists for the case where that is known and
    /// accepted — an export from an environment whose apps have moved on, where the rest
    /// of the tables are still worth having. It never loads a mismatched table part way:
    /// the table is skipped whole, with the mismatch as its reason.
    /// </summary>
    public bool SkipMismatchedTables { get; init; }

    /// <summary>Rows per bulk-copy batch.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>Build and print the plan, write nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>
/// Matches the source's tables and columns to the target database's, by name, and refuses
/// anything that would move data inexactly.
///
/// The whole point of this command is that the container's schema was made by installing
/// the same extensions the source was exported from, so a name match is meant to be an
/// exact schema match too. Where it is not — a column the target has no home for, a
/// narrower target column, a different type — the restore stops and says which table and
/// column, rather than loading a row that silently lost a field. A source table the target
/// does not have at all is the one expected mismatch (an extension that is not installed):
/// that one is reported and skipped, because it is the workflow's normal case and it loses
/// nothing that was going to be written.
/// </summary>
public static class RestorePlanner
{
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
        var plans = new List<TablePlan>();
        var skipped = new List<SkippedTable>();

        foreach (var st in source.Tables.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (only.Count > 0 && !only.Contains(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name, "not named by --table"));
                continue;
            }
            if (!opts.IncludeSystem && IsSystemTable(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name,
                    "a platform table: it describes the service tier's own view of the database, which belongs to "
                    + "the container — pass --include-system to write it anyway"));
                continue;
            }
            if (!byName.TryGetValue(st.Name, out var hits))
            {
                skipped.Add(new SkippedTable(st.Name,
                    "no table of this name in the target database — install the extension that defines it, then restore again"));
                continue;
            }
            if (hits.Count > 1)
                throw new InvalidDataException(
                    $"the target database has {hits.Count} tables named {st.Name} "
                    + $"(schemas {string.Join(", ", hits.Select(h => h.Schema).OrderBy(x => x, StringComparer.Ordinal))}) "
                    + "— refusing to guess which one the source's rows belong in");
            try { plans.Add(BuildTable(source, st, hits[0])); }
            catch (InvalidDataException ex) when (opts.SkipMismatchedTables)
            {
                skipped.Add(new SkippedTable(st.Name, ex.Message));
            }
        }
        return (plans, skipped);
    }

    static TablePlan BuildTable(IBcSource source, SourceTable st, TargetTable tt)
    {
        var mappings = new List<ColumnMapping>();
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sc in source.Columns(st))
        {
            // A rowversion in the source is not data: the engine that wrote it stamped it,
            // and the engine receiving these rows will stamp its own.
            if (sc.XType == 189) continue;

            var hit = tt.Columns.FirstOrDefault(c => c.Name.Equals(sc.Name, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
                throw new InvalidDataException(
                    $"{st.Name}.{sc.Name} ({TypeText(sc)}) has no column in target table {tt.QuotedName} — "
                    + "refusing to load rows that would silently lose it; the target's schema is at a different "
                    + "version of the extension that defines this table");
            if (hit.IsRowVersion)
                throw new InvalidDataException(
                    $"{st.Name}.{sc.Name} is {TypeText(sc)} in the source and a rowversion in the target — "
                    + "a rowversion cannot be written, so this column's values have nowhere to go");
            // A computed target column derives its own value from the columns around it;
            // not writing it loses nothing.
            if (hit.IsComputed) continue;

            CheckCompatible(st.Name, sc, hit);
            mappings.Add(new ColumnMapping(sc, hit));
            mapped.Add(hit.Name);
        }

        var targetOnly = new List<string>();
        foreach (var tc in tt.Columns)
        {
            if (mapped.Contains(tc.Name) || !tc.IsWritable) continue;
            if (tc.IsIdentity || tc.IsNullable || tc.HasDefault) { targetOnly.Add(tc.Name); continue; }
            throw new InvalidDataException(
                $"target column {tt.QuotedName}.{tc.Name} ({TypeText(tc)}) is NOT NULL with no default, and "
                + $"{st.Name} has no column of that name — every row would be rejected, so the load stops here "
                + "rather than part way through");
        }
        return new TablePlan(st, tt, mappings, targetOnly);
    }

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
