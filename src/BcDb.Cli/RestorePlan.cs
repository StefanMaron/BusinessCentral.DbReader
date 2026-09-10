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

    /// <summary>
    /// Source columns the target lacks whose values were dropped rather than carried across,
    /// under --allow-column-loss. Empty unless that option let this table load at all.
    /// </summary>
    public IReadOnlyList<string> DroppedColumns { get; init; } = Array.Empty<string>();
}

/// <summary>A source table that will not be written, and why — always reported, never silent.</summary>
public sealed record SkippedTable(string Name, string Reason);

/// <summary>Everything the caller chose on the command line that shapes the plan.</summary>
public sealed record RestoreOptions
{
    /// <summary>Only these source tables (raw SQL object names), or empty for every one of them.</summary>
    public IReadOnlyCollection<string> OnlyTables { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Source company name → target company name. A source table whose name starts with
    /// "&lt;SourceCompany&gt;$" (or is exactly that company) is matched and, when created,
    /// named as if it belonged to the target company instead — the rows still come from
    /// the real source table (<see cref="TablePlan.Source"/> is unaffected), only where
    /// they land changes.
    ///
    /// For restoring into a company that already exists, correctly, with its own schema —
    /// a Business Central container's own demo company (CRONUS, or one an admin created
    /// through BC itself), rather than a company this restore has to build from nothing.
    /// Reusing it sidesteps every gap between what a bare source schema declares and what
    /// BC's own tooling actually needs — generated SumIndexField views, per-key indexes,
    /// anything else that is a property of a *properly created* company, not of any one
    /// table's declared columns — because that schema was never touched: the extensions
    /// that own it already built it correctly.
    ///
    /// Tables with no company prefix at all (User, Company itself, the Tenant Profile and
    /// NAV App families, $ndo$… platform tables) are unaffected: nothing about identity,
    /// permissions, profile registration, or the platform's own app/company registry should
    /// come from the source, which is exactly what <see cref="RestorePlanner.ContainerIdentityTables"/>
    /// and $ndo$ tables are already excluded by default for — this option does not exclude
    /// anything itself, it only decides the destination table name for tables that do carry
    /// a company prefix.
    /// </summary>
    public IReadOnlyDictionary<string, string> RenameCompany { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// These source tables (raw SQL object names) are left alone entirely — not created,
    /// not written, target rows untouched. Checked before every other rule, including
    /// --table: it always wins, the same way $ndo$ and <see cref="RestorePlanner.ContainerIdentityTables"/>
    /// tables always win over --table.
    ///
    /// The container's own login/session/company-identity/app-registry tables no longer
    /// need naming here — <see cref="RestorePlanner.ContainerIdentityTables"/> excludes them
    /// by default the same way $ndo$-prefixed tables already did
    /// (<see cref="IncludeSystem"/>) — so this is for anything *else* a particular restore
    /// should not touch: a table an extension owns that this target's version does not
    /// carry the same way, company-scoped state a caller wants left alone for its own
    /// reasons, and so on.
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

    /// <summary>
    /// Write the container's own login, session, profile and installed-app tables too —
    /// <see cref="RestorePlanner.ContainerIdentityTables"/> — instead of leaving them alone
    /// by default. Off by default for the same reason $ndo$... tables are: these describe
    /// the *container's* own state, not the exported tenant's business data, and restoring
    /// into a database that is already running and signed into (the point of
    /// <see cref="RenameCompany"/>) means overwriting them breaks the container rather than
    /// filling it — a login locked out (User, Access Control, User Personalization, User
    /// Property), a company registration that no longer matches what was just written
    /// (Company), a profile a signed-in user can no longer resolve after the next service-
    /// tier restart (the Tenant Profile family), or an installed-app registry pointing at
    /// packages the container never compiled (the NAV App family) — every one of these was
    /// observed breaking a real container this way before being added to the list.
    /// </summary>
    public bool IncludeIdentity { get; init; }

    /// <summary>Empty each target table before loading it. Without it a non-empty target is refused.</summary>
    public bool Replace { get; init; }

    /// <summary>
    /// Create what the target does not have: a table the source has and the target lacks,
    /// and a column missing from a table that does exist. Off by default; --create turns it
    /// on.
    ///
    /// Building a table's own shape from a bare source schema cannot reproduce what BC's own
    /// schema synchronisation generates when a table is created *through BC* — SumIndexField
    /// views, per-key indexes, whatever else a properly installed extension's table has that
    /// a `CREATE TABLE` matching its declared columns does not. Confirmed broken this way: a
    /// company built by letting an earlier version of this default create its tables failed
    /// with `Invalid object name '…$VSIFT$Key2'` the moment a page touched a FlowField.
    /// Restoring into a container whose extensions are already installed avoids the problem
    /// entirely — the table was never built by this tool, only filled — which is why that is
    /// the default now: --create is for the caller who has decided an incomplete table (or
    /// none of this tool's business, since the caller is not restoring into an existing BC
    /// schema at all) is an acceptable tradeoff, not the assumption every restore starts from.
    /// </summary>
    public bool CreateMissing { get; init; }

    /// <summary>
    /// Drop a source column's values instead of refusing the whole table when the target
    /// lacks that column (the default when --create is off — a target the source column has
    /// no home in is nothing to lose values from, it just adds one). On by default;
    /// --no-allow-column-loss turns it off.
    ///
    /// Losing a column silently would produce rows that look complete and are not, but
    /// refusing the whole table is a strictly bigger loss for the same reason — a production
    /// tenant's installed extensions are essentially never identical to a dev sandbox's, so
    /// this is the common case, not the exception, and losing every row over one unmapped
    /// field (a `…$ext` companion table, most often — see "Restoring into a container that is
    /// running" in PROVENANCE.md for what that costs a page that reads the main table) is a
    /// worse default than dropping the field. Every column it drops is still named in the
    /// plan, same as every other decision here — never silent either way, only which loss is
    /// the default.
    /// </summary>
    public bool AllowColumnLoss { get; init; } = true;

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

    /// <summary>
    /// The container's own login, session, profile and installed-app tables — not
    /// $ndo$-prefixed, so <see cref="IsSystemTable"/> does not already catch them, but the
    /// same idea: they describe *this* container, not the exported tenant's business data.
    /// See <see cref="RestoreOptions.IncludeIdentity"/>.
    ///
    /// User / Access Control / User Personalization / User Property / Company: who can sign
    /// in and which companies this server currently considers real — restoring them replaces
    /// the container's own working identity with the source's.
    ///
    /// Tenant Profile / Tenant Profile Extension / Tenant Profile Page Metadata / Tenant
    /// Profile Setting: a source bacpac carries these near-empty (a cloud export does not
    /// include the platform's own role-center registration), so --replace truncates the
    /// container's own correctly-populated rows down to the source's — confirmed by
    /// reproducing "No default profile could be found" / role center stuck on "Getting
    /// ready" on a real container, then confirming it does not reproduce once these are
    /// left alone.
    ///
    /// NAV App Installed App / NAV App Published App / NAV App Setting / NAV App Tenant
    /// Add-In / NAV App Tenant Operation / NAV App Data Archive: the container's own record
    /// of which compiled app packages are installed. A source tenant's extension set is
    /// essentially never identical to a dev container's, so restoring these points the
    /// container at packages it never compiled — confirmed the same way: reproduced NST's
    /// "Could not find any app metadata for 1 runtime package IDs" restoring them, confirmed
    /// it does not reproduce leaving them alone.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ContainerIdentityTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "User", "Access Control", "User Personalization", "User Property", "Company",
        "Tenant Profile", "Tenant Profile Extension", "Tenant Profile Page Metadata", "Tenant Profile Setting",
        "NAV App Installed App", "NAV App Published App", "NAV App Setting",
        "NAV App Tenant Add-In", "NAV App Tenant Operation", "NAV App Data Archive",
    };

    /// <summary>See <see cref="ContainerIdentityTables"/>.</summary>
    public static bool IsContainerIdentityTable(string sqlName) => ContainerIdentityTables.Contains(sqlName);

    /// <summary>
    /// The company segment of a raw SQL table name, or null when the name carries none —
    /// $ndo$... platform tables, container-identity tables, and a handful of genuinely
    /// company-agnostic AL tables all have no prefix. Mirrors the shape
    /// Program.BcTables derives company/table/app-id names from
    /// (<code>&lt;Company&gt;$&lt;Table&gt;[$&lt;AppId&gt;][$ext]</code>) — kept here as its own
    /// small function rather than called across into the CLI dispatcher, since this is the
    /// one piece of it a restore's own company auto-detection needs.
    /// </summary>
    public static string? CompanySegment(string sqlTableName)
    {
        var segs = sqlTableName.Split('$');
        bool isExt = segs[^1] == "ext";
        var core = isExt ? segs[..^1] : segs;
        if (core.Length >= 3 && Guid.TryParse(core[^1], out _)) return string.Join("$", core[..^2]);
        if (core.Length == 2 && Guid.TryParse(core[^1], out _)) return null;   // <table>$<appid>, no company
        if (core.Length == 2) return core[0];                                  // <company>$<table>
        return null;
    }

    /// <summary>
    /// The source's own companies that actually carry data — grouped by
    /// <see cref="CompanySegment"/>, keeping only a company with at least one row somewhere
    /// under it. A cloud export commonly carries an unused "My Company" skeleton alongside
    /// the real one (every table 0 rows); filtering it out is what makes single-company
    /// auto-detection actually single in that case rather than perpetually ambiguous.
    ///
    /// <paramref name="inScope"/> narrows which source tables are even looked at — a
    /// company entirely outside --table/--exclude-table's scope should not force a company
    /// choice on a restore that was never going to touch it. Every table counts when omitted.
    /// </summary>
    public static IReadOnlyList<string> SourceCompaniesWithData(IBcSource source, Func<string, bool>? inScope = null)
        => source.Tables
            .Where(t => inScope is null || inScope(t.Name))
            .Select(t => (Table: t, Company: CompanySegment(t.Name)))
            .Where(x => x.Company != null)
            .GroupBy(x => x.Company!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(x => x.Table.RowCount() > 0))
            .Select(g => g.Key)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The distinct companies the target's *own* schema already declares — read off its
    /// table names, the ground truth for what a company's SQL prefix actually is (BC
    /// replaces characters SQL disallows in an identifier with '_', so the display name in
    /// the target's own Company table is not reliably the same string). No data filter: a
    /// company existing in the target's schema at all is the question, not how much it holds.
    /// </summary>
    public static IReadOnlyList<string> TargetCompanies(IReadOnlyList<TargetTable> target)
        => target.Select(t => CompanySegment(t.Name)).Where(c => c != null).Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

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
            if (!opts.IncludeIdentity && IsContainerIdentityTable(st.Name))
            {
                skipped.Add(new SkippedTable(st.Name,
                    "the container's own login, session, profile or installed-app state, not the exported "
                    + "tenant's business data — pass --include-identity to write it anyway"));
                continue;
            }
            string targetName = RenameCompanyPrefix(st.Name, opts.RenameCompany) ?? st.Name;
            byName.TryGetValue(targetName, out var hits);
            if (hits is null && !opts.CreateMissing)
            {
                skipped.Add(new SkippedTable(st.Name,
                    $"no table named {targetName} in the target database — pass --create to build it "
                    + "from the source's own schema"));
                continue;
            }
            if (hits is { Count: > 1 })
                throw new InvalidDataException(
                    $"the target database has {hits.Count} tables named {targetName} "
                    + $"(schemas {string.Join(", ", hits.Select(h => h.Schema).OrderBy(x => x, StringComparer.Ordinal))}) "
                    + "— refusing to guess which one the source's rows belong in");
            try { plans.Add(BuildTable(source, st, targetName, hits?[0], opts)); }
            catch (InvalidDataException) when (opts.Strict) { throw; }
            catch (InvalidDataException ex)
            {
                skipped.Add(new SkippedTable(st.Name, ex.Message));
            }
        }
        return (plans, skipped);
    }

    /// <summary>
    /// Source table name → target table name under a <see cref="RestoreOptions.RenameCompany"/>
    /// mapping, or null when no company prefix in it matches (including when the table
    /// carries no company prefix at all). A match is a whole leading "&lt;Company&gt;$"
    /// segment — never a bare string prefix, so a mapped company "TP" does not also catch
    /// a table actually belonging to company "TPX".
    /// </summary>
    public static string? RenameCompanyPrefix(string sourceTableName, IReadOnlyDictionary<string, string> renameCompany)
    {
        foreach (var (src, dst) in renameCompany.OrderByDescending(kv => kv.Key.Length))
        {
            if (sourceTableName.Equals(src, StringComparison.OrdinalIgnoreCase)) return dst;
            if (sourceTableName.StartsWith(src + "$", StringComparison.OrdinalIgnoreCase))
                return dst + sourceTableName[src.Length..];
        }
        return null;
    }

    /// <summary>
    /// The plan for one table. <paramref name="existing"/> is null when the target has no
    /// such table and it is to be created; <paramref name="targetName"/> is the source
    /// table's own name, or its <see cref="RenameCompanyPrefix"/> substitution.
    /// </summary>
    static TablePlan BuildTable(IBcSource source, SourceTable st, string targetName, TargetTable? existing, RestoreOptions opts)
    {
        var srcCols = source.Columns(st);
        var keyColumns = source.RowKeyColumns(st);
        bool create = existing is null;
        var tt = existing ?? new TargetTable("dbo", targetName, Array.Empty<TargetColumn>());

        var mappings = new List<ColumnMapping>();
        var addColumns = new List<SysColumn>();
        var droppedColumns = new List<string>();
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
                if (!opts.CreateMissing && opts.AllowColumnLoss)
                {
                    droppedColumns.Add(sc.Name);
                    continue;
                }
                if (!opts.CreateMissing)
                    throw new InvalidDataException(
                        $"{st.Name}.{sc.Name} ({TypeText(sc)}) has no column in target table {tt.QuotedName}, "
                        + "and --no-allow-column-loss was given — refusing to load rows that would lose it "
                        + "(pass --create to add the column instead)");
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
            DroppedColumns = droppedColumns,
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
