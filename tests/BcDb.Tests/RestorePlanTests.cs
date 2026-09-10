using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// What `bcdb restore` decides before it writes anything, against the committed
/// typeprobe.bak and a target schema built to mirror it (RestoreTestSchema).
///
/// The plan is where data loss is caught. A restore that silently dropped a column,
/// truncated a value into a narrower one, or wrote a table that only half matched would
/// produce a container that looks loaded and is wrong — the same failure the reader's
/// loud-failures rule exists to prevent, one direction further along. Every test here
/// either pins a mapping down to specific column names or pins the refusal down to the
/// message a user has to act on.
/// </summary>
public class RestorePlanTests : IDisposable
{
    readonly IBcSource _src;

    public RestorePlanTests() => _src = BcSource.Open(RestoreTestSchema.TypeprobeBak);

    public void Dispose() => _src.Dispose();

    // CreateMissing is off by default and AllowColumnLoss is on — the shape restoring into
    // an already-correct container wants. Building a table from bare source schema, and
    // refusing rather than dropping an unmapped column, are both now opt-in.
    static readonly RestoreOptions Default = new();
    static readonly RestoreOptions Strict = new() { Strict = true };
    static readonly RestoreOptions Create = new() { CreateMissing = true };
    static readonly RestoreOptions CreateStrict = new() { CreateMissing = true, Strict = true };
    static readonly RestoreOptions NoColumnLoss = new() { AllowColumnLoss = false };
    static readonly RestoreOptions NoColumnLossStrict = new() { AllowColumnLoss = false, Strict = true };

    (IReadOnlyList<TablePlan> Tables, IReadOnlyList<SkippedTable> Skipped) Build(
        List<TargetTable> target, RestoreOptions? opts = null)
        => RestorePlanner.Build(_src, target, opts ?? Default);

    List<TargetTable> MirrorAll() => RestoreTestSchema.MirrorAll(_src);

    TablePlan PlanFor(List<TargetTable> target, string table, RestoreOptions? opts = null)
        => Build(target, opts).Tables.Single(p => p.Source.Name == table);

    [Fact]
    public void MirroredSchemaMapsEveryDataColumn()
    {
        var plan = PlanFor(MirrorAll(), "probe_notnull");
        Assert.Equal(new[]
        {
            "id", "n_tinyint", "n_smallint", "n_int", "n_bigint", "n_bit",
            "n_dec38_20", "n_dec18_2", "n_dec5_0", "n_datetime", "n_datetime2_7", "n_datetime2_0",
            "n_date", "n_time7", "n_time0", "n_guid", "n_nvarchar", "n_varchar", "n_nchar",
            "n_char", "n_binary", "n_varbinary", "n_real", "n_float", "n_vbmax", "n_nvmax",
        }, plan.Columns.Select(c => c.Target.Name));
        Assert.Empty(plan.TargetOnlyColumns);
        Assert.False(plan.NeedsIdentityInsert);
        Assert.Equal("[dbo].[probe_notnull]", plan.Target.QuotedName);
    }

    [Fact]
    public void RowversionIsNeverWritten()
    {
        // n_ver is a rowversion: SQL Server stamps it on insert and refuses a supplied
        // value. It is in the source's columns and must not be in the plan's.
        Assert.Contains(_src.Columns(_src.Tables.Single(t => t.Name == "probe_notnull")),
            c => c.Name == "n_ver" && c.TypeName == "timestamp");
        Assert.DoesNotContain(PlanFor(MirrorAll(), "probe_notnull").Columns, c => c.Target.Name == "n_ver");
    }

    [Fact]
    public void ComputedColumnsAreNeverWritten()
    {
        var target = MirrorAll();
        var t = RestoreTestSchema.Mirror(_src, "probe_dense");
        var note = t.Columns.Single(c => c.Name == "note");
        var plan = PlanFor(target.Replace(t.With("note", note with { IsComputed = true })), "probe_dense");
        Assert.DoesNotContain(plan.Columns, c => c.Target.Name == "note");
        Assert.Contains(plan.Columns, c => c.Target.Name == "amount");
    }

    [Fact]
    public void ATableTheTargetDoesNotHaveIsCreatedFromTheSourcesOwnSchemaUnderCreate()
    {
        // BC keeps a table's data when its extension is uninstalled and adopts the table
        // again on reinstall, so a table that exists before its owner does is a state BC
        // already handles — --create opts into building one this way.
        var target = MirrorAll().Where(t => t.Name != "probe_notnull").ToList();
        var plan = PlanFor(target, "probe_notnull", Create);

        Assert.True(plan.CreateTable);
        Assert.Equal("[dbo].[probe_notnull]", plan.Target.QuotedName);
        Assert.Equal(new[] { "id" }, plan.KeyColumns);
        // Every source column is in the DDL, the rowversion included — it is part of the
        // table's shape even though no value is ever written to it.
        Assert.Contains(plan.SourceColumns, c => c.Name == "n_ver" && c.TypeName == "timestamp");
        Assert.Equal(27, plan.SourceColumns.Count);
        // …but it is still not written.
        Assert.DoesNotContain(plan.Columns, c => c.Target.Name == "n_ver");
        Assert.Equal(26, plan.Columns.Count);
    }

    [Fact]
    public void CreatedColumnsCarryTheSourcesNullability()
    {
        var target = MirrorAll().Where(t => t.Name != "probe").ToList();
        var plan = PlanFor(target, "probe", Create);
        // tools/typeprobe.sql: probe.id is NOT NULL, every other column is nullable.
        Assert.False(plan.SourceColumns.Single(c => c.Name == "id").IsNullable);
        Assert.True(plan.SourceColumns.Single(c => c.Name == "c_nvarchar").IsNullable);
    }

    [Fact]
    public void ByDefaultAMissingTableIsSkippedByNameWithAReason()
    {
        var target = MirrorAll().Where(t => t.Name != "probe_notnull").ToList();
        var skipped = Build(target).Skipped.Single(s => s.Name == "probe_notnull");
        Assert.Contains("no table", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--create", skipped.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(Build(target).Tables, p => p.Source.Name == "probe_notnull");
    }

    [Fact]
    public void PlatformTablesAreSkippedUnlessAskedFor()
    {
        // $probe$platform stands in for BC's $ndo$... tables: they describe the service
        // tier's own view of the database and belong to the container, not to the export.
        var target = MirrorAll();
        var skipped = Build(target).Skipped.Single(s => s.Name == "$probe$platform");
        Assert.Contains("--include-system", skipped.Reason, StringComparison.Ordinal);

        var withSystem = Build(target, new RestoreOptions { IncludeSystem = true });
        Assert.Contains(withSystem.Tables, p => p.Source.Name == "$probe$platform");
        Assert.DoesNotContain(withSystem.Skipped, s => s.Name == "$probe$platform");
    }

    [Fact]
    public void ContainerIdentityTablesCoversTheRealBcTableNames()
    {
        // Every table this session's investigation found overwriting a running container's
        // own state: login/session (excluded since the first restore session), and the two
        // families discovered today by A/B testing a real container — Tenant Profile (the
        // family excluded restoring stops a service-tier restart from resolving a user's
        // default profile) and NAV App * (excluded stops "Could not find any app metadata
        // for 1 runtime package IDs" — production's own installed-app registry overwriting
        // the container's, since a source's extension set is never guaranteed to match a
        // target container's).
        foreach (var name in new[]
        {
            "User", "Access Control", "User Personalization", "User Property", "Company",
            "Tenant Profile", "Tenant Profile Extension", "Tenant Profile Page Metadata", "Tenant Profile Setting",
            "NAV App Installed App", "NAV App Published App", "NAV App Setting",
            "NAV App Tenant Add-In", "NAV App Tenant Operation", "NAV App Data Archive",
        })
            Assert.True(RestorePlanner.IsContainerIdentityTable(name), $"{name} should be a container-identity table");

        // Case-insensitive, the same as every other name match this planner does.
        Assert.True(RestorePlanner.IsContainerIdentityTable("user personalization"));

        // Exact names only — a real AL business table sharing a word must not be caught by
        // a careless prefix or substring match.
        Assert.False(RestorePlanner.IsContainerIdentityTable("User Setup"));
        Assert.False(RestorePlanner.IsContainerIdentityTable("Application Area Setup"));
        Assert.False(RestorePlanner.IsContainerIdentityTable("Customer"));
    }

    [Fact]
    public void ContainerIdentityTablesAreSkippedByDefaultAndWinOverTable()
    {
        using var src = _src.WithAliases("probe", "User", "NAV App Installed App");
        var target = RestoreTestSchema.MirrorAll(src);

        var result = RestorePlanner.Build(src, target, Default);
        foreach (var name in new[] { "User", "NAV App Installed App" })
        {
            var skipped = result.Skipped.Single(s => s.Name == name);
            Assert.Contains("--include-identity", skipped.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Tables, p => p.Source.Name == name);
        }

        // Naming it explicitly does not override the default exclusion, the same way
        // --table cannot force a $ndo$... table through.
        var named = RestorePlanner.Build(src, target, new RestoreOptions { OnlyTables = new[] { "User" } });
        Assert.DoesNotContain(named.Tables, p => p.Source.Name == "User");
        Assert.Contains(named.Skipped, s => s.Name == "User" && s.Reason.Contains("--include-identity", StringComparison.Ordinal));
    }

    [Fact]
    public void IncludeIdentityWritesContainerIdentityTablesAnyway()
    {
        using var src = _src.WithAliases("probe", "User");
        var target = RestoreTestSchema.MirrorAll(src);

        var result = RestorePlanner.Build(src, target, new RestoreOptions { IncludeIdentity = true });
        Assert.Contains(result.Tables, p => p.Source.Name == "User");
        Assert.DoesNotContain(result.Skipped, s => s.Name == "User");
    }

    [Theory]
    [InlineData("TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "TP")]                    // <company>$<table>$<appid>
    [InlineData("Stefan Maron Consulting$Customer$437dbf0e-84ff-417a-965d-ed2bb9650972$ext", "Stefan Maron Consulting")]  // …and $ext
    [InlineData("CRONUS International Ltd_$Customer$437dbf0e-84ff-417a-965d-ed2bb9650972", "CRONUS International Ltd_")]
    [InlineData("probe_notnull", null)]              // no '$' at all
    [InlineData("NAV App Installed App", null)]       // no '$' at all, container-identity table
    [InlineData("$ndo$dbproperty", null)]              // platform table: leads with '$', not a company prefix
    public void CompanySegmentParsesTheRealBcTableNameShape(string sqlName, string? expectedCompany)
        => Assert.Equal(expectedCompany, RestorePlanner.CompanySegment(sqlName));

    [Fact]
    public void SourceCompaniesWithDataFiltersOutEmptyOnes()
    {
        // A cloud export commonly carries an unused "My Company" skeleton (every table 0
        // rows) alongside the real one — filtered out so single-company auto-detection is
        // actually single, not perpetually "two companies, pick one."
        using var src = _src.WithExtraTables(
            ("Real Co$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 15),
            ("Empty Co$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 0),
            ("Empty Co$Vendor$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 0));

        // typeprobe.bak carries its own real companies (TP, ProbeCo) alongside these —
        // the assertion is about "Empty Co", not a closed list of every company found.
        var companies = RestorePlanner.SourceCompaniesWithData(src);
        Assert.Contains("Real Co", companies);
        Assert.DoesNotContain("Empty Co", companies);
    }

    [Fact]
    public void SourceCompaniesWithDataListsEveryCompanyThatHasAnyRealRowAnywhere()
    {
        using var src = _src.WithExtraTables(
            ("Co A$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 0),
            ("Co A$Vendor$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 3),   // one real row is enough for the whole company
            ("Co B$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 7));

        var companies = RestorePlanner.SourceCompaniesWithData(src);
        Assert.Contains("Co A", companies);   // one of its two tables has rows — the company counts
        Assert.Contains("Co B", companies);
    }

    [Fact]
    public void TargetCompaniesReadsTheTargetsOwnTableNamesNotACompanyDisplayName()
    {
        // The target's own Company.Name is the AL display name ("CRONUS International
        // Ltd."), not the SQL table prefix ("CRONUS International Ltd_", underscore) — so
        // this reads company names off the target's actual table names instead of
        // guessing at BC's character-substitution rule.
        var target = new List<TargetTable>
        {
            new("dbo", "CRONUS International Ltd_$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Array.Empty<TargetColumn>()),
            new("dbo", "CRONUS International Ltd_$Vendor$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Array.Empty<TargetColumn>()),
            new("dbo", "My Company$Customer$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Array.Empty<TargetColumn>()),
            new("dbo", "User", Array.Empty<TargetColumn>()),
        };
        Assert.Equal(new[] { "CRONUS International Ltd_", "My Company" }, RestorePlanner.TargetCompanies(target));
    }

    [Fact]
    public void OnlyTablesLimitsThePlanAndSkipsTheRestByName()
    {
        var plan = Build(MirrorAll(), new RestoreOptions { OnlyTables = new[] { "probe_notnull" } });
        Assert.Equal(new[] { "probe_notnull" }, plan.Tables.Select(p => p.Source.Name));
        Assert.Contains(plan.Skipped, s => s.Name == "probe_dense" && s.Reason.Contains("--table"));
    }

    [Fact]
    public void ExcludeTablesLeavesThemUntouchedAndSkipsThemByName()
    {
        // The container's own login/session/company-identity tables (User, Access
        // Control, User Personalization, $ndo$tenantcompany, ...) are not business data:
        // a restore into a database that is already running should be able to leave them
        // alone entirely, the way it can already leave $ndo$ tables alone by default.
        var plan = Build(MirrorAll(), new RestoreOptions { ExcludeTables = new[] { "probe_notnull" } });
        Assert.DoesNotContain(plan.Tables, p => p.Source.Name == "probe_notnull");
        Assert.Contains(plan.Tables, p => p.Source.Name == "probe_dense");
        Assert.Contains(plan.Skipped, s => s.Name == "probe_notnull" && s.Reason.Contains("--exclude-table"));
    }

    [Fact]
    public void ExcludeTablesIsCaseInsensitiveAndWinsOverCreate()
    {
        // Excluded means excluded even with --create asked for: the table is left alone,
        // not created-and-then-somehow-skipped.
        var target = MirrorAll().Where(t => t.Name != "probe_notnull").ToList();
        var plan = Build(target, new RestoreOptions { CreateMissing = true, ExcludeTables = new[] { "PROBE_NOTNULL" } });
        Assert.DoesNotContain(plan.Tables, p => p.Source.Name == "probe_notnull");
        Assert.Contains(plan.Skipped, s => s.Name == "probe_notnull");
    }

    [Fact]
    public void OnlyTablesAndExcludeTablesCombine()
    {
        // --table narrows to a set; --exclude-table can still carve a table back out of
        // that set, e.g. "restore just these two tables, but not this one's data."
        var plan = Build(MirrorAll(), new RestoreOptions
        {
            OnlyTables = new[] { "probe_notnull", "probe_dense" },
            ExcludeTables = new[] { "probe_notnull" },
        });
        Assert.Equal(new[] { "probe_dense" }, plan.Tables.Select(p => p.Source.Name));
    }

    // "TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" — a genuinely company-prefixed
    // probe table (company "TP"), the only one in typeprobe.bak. Real BC table names all
    // have this shape: <Company>$<Table>[$<AppId>].
    const string TpExttest = "TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const string TargetCoExttest = "TargetCo$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    static readonly RestoreOptions RenamedToTargetCo = new()
    { RenameCompany = new Dictionary<string, string> { ["TP"] = "TargetCo" } };

    [Fact]
    public void RenameCompanyRemapsTheTargetTableNameButNotTheSourceRead()
    {
        // For restoring a source company's data into a target company that already
        // exists — reusing an already-provisioned company (its schema, its identity, its
        // $ndo$tenantcompany registration) instead of creating a new one from scratch.
        var target = MirrorAll();
        target.Add(RestoreTestSchema.Mirror(_src, TpExttest) with { Name = TargetCoExttest });
        var plan = Build(target, RenamedToTargetCo).Tables.Single(p => p.Source.Name == TpExttest);

        Assert.Equal(TargetCoExttest, plan.Target.Name);      // rows land under the renamed table
        Assert.Equal(TpExttest, plan.Source.Name);             // but are read from the real source table
    }

    [Fact]
    public void RenameCompanyLeavesCompanyAgnosticTablesAlone()
    {
        // "probe_notnull" carries no company prefix at all — a rename rule for "TP" must
        // not touch it.
        var plan = PlanFor(MirrorAll(), "probe_notnull", RenamedToTargetCo);
        Assert.Equal("probe_notnull", plan.Target.Name);
    }

    [Fact]
    public void RenameCompanyDoesNotMatchAPrefixThatIsNotACompanySegment()
    {
        // "TPX$..." must not be treated as company "TP" plus a stray "X" — the match is a
        // whole leading segment ending at "$", never a bare string prefix.
        var target = MirrorAll();
        var weirdSource = RestoreTestSchema.Mirror(_src, TpExttest) with { Name = "TPX$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" };
        // No such source table actually exists, so just exercise the naming rule directly
        // via the public rename helper instead of a full plan.
        Assert.Null(RestorePlanner.RenameCompanyPrefix("TPX$exttest$x", RenamedToTargetCo.RenameCompany));
        Assert.Equal("TargetCo$exttest$x", RestorePlanner.RenameCompanyPrefix("TP$exttest$x", RenamedToTargetCo.RenameCompany));
        Assert.Null(RestorePlanner.RenameCompanyPrefix("probe_notnull", RenamedToTargetCo.RenameCompany));
    }

    [Fact]
    public void RenameCompanyCreatesTheTableUnderTheRenamedNameWhenTargetLacksItAndCreateIsOn()
    {
        // The target does not have "TargetCo$exttest$..." at all yet (only
        // "TP$exttest$..." exists, from mirroring the source) — with --create, creation
        // still happens, but under the renamed name.
        var target = MirrorAll();  // has TP$exttest$..., not TargetCo$exttest$...
        var opts = RenamedToTargetCo with { CreateMissing = true };
        var plan = Build(target, opts).Tables.Single(p => p.Source.Name == TpExttest);
        Assert.True(plan.CreateTable);
        Assert.Equal(TargetCoExttest, plan.Target.Name);
    }

    [Fact]
    public void RenameCompanyByDefaultRefusesToInventTheRenamedTable()
    {
        // The exact scenario --rename-company exists for, and now the default: the target
        // company already exists with its own correct schema, and nothing should ever be
        // created — only matched, existing, renamed tables get written.
        var target = MirrorAll();  // no TargetCo$... tables at all
        var skipped = Build(target, RenamedToTargetCo).Skipped.Single(s => s.Name == TpExttest);
        Assert.Contains("no table", skipped.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--create", skipped.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TableAndExcludeTableFiltersStillMatchTheSourceNameNotTheRenamedOne()
    {
        // --table/--exclude-table name *source* tables (what the caller can see by
        // reading the source file); renaming decides where a matched table's rows go,
        // not which source tables are in scope.
        var target = MirrorAll();
        target.Add(RestoreTestSchema.Mirror(_src, TpExttest) with { Name = TargetCoExttest });
        var opts = RenamedToTargetCo with { OnlyTables = new[] { TpExttest } };
        var plan = Build(target, opts);
        Assert.Equal(new[] { TpExttest }, plan.Tables.Select(p => p.Source.Name));
        Assert.Equal(TargetCoExttest, plan.Tables.Single().Target.Name);
    }

    [Fact]
    public void ASourceColumnTheTargetTableLacksIsAddedToItUnderCreate()
    {
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var plan = PlanFor(target, "probe_notnull", Create);

        Assert.False(plan.CreateTable);                       // the table itself was there
        Assert.Equal(new[] { "n_nvarchar" }, plan.AddColumns.Select(c => c.Name));
        Assert.Contains(plan.Columns, c => c.Target.Name == "n_nvarchar");   // and it is written
        Assert.Equal(26, plan.Columns.Count);
    }

    [Fact]
    public void WithNoAllowColumnLossAMissingColumnIsRefusedRatherThanDropped()
    {
        // Loading the other 25 columns and dropping this one would produce rows that look
        // complete. --no-allow-column-loss (with --create off, the default combination
        // once one opts out of the other) names the column instead of dropping it.
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var skipped = Build(target, NoColumnLoss).Skipped.Single(s => s.Name == "probe_notnull");
        Assert.Contains("n_nvarchar", skipped.Reason, StringComparison.Ordinal);

        var ex = Assert.Throws<InvalidDataException>(() => Build(target, NoColumnLossStrict));
        Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("n_nvarchar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ByDefaultAMissingColumnIsDroppedRatherThanRefused()
    {
        // Restoring into a container whose extensions don't quite match the source's (a
        // production tenant's Clockify/Avalara/Stripe fields, say) is the common case, not
        // the exception, so dropping just that column's values — the other 25 columns and
        // every row still load — is the default now, not something to opt into.
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var plan = PlanFor(target, "probe_notnull");

        Assert.DoesNotContain(plan.Columns, c => c.Target.Name == "n_nvarchar");
        Assert.Equal(25, plan.Columns.Count);
        Assert.Equal(new[] { "n_nvarchar" }, plan.DroppedColumns);
        // Still not a table this had to create — the target already had it, minus one column.
        Assert.False(plan.CreateTable);
        Assert.Empty(plan.AddColumns);
        Assert.DoesNotContain(Build(target).Skipped, s => s.Name == "probe_notnull");
    }

    [Fact]
    public void AllowColumnLossDoesNothingWhenCreateIsOn()
    {
        // AllowColumnLoss only matters when CreateMissing is off; with --create, a missing
        // column is added to the target instead, same as always, and nothing is dropped.
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var plan = PlanFor(target, "probe_notnull", Create);

        Assert.Empty(plan.DroppedColumns);
        Assert.Equal(new[] { "n_nvarchar" }, plan.AddColumns.Select(c => c.Name));
        Assert.Contains(plan.Columns, c => c.Target.Name == "n_nvarchar");
    }

    [Fact]
    public void ANarrowerTargetColumnIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var nv = t.Columns.Single(c => c.Name == "n_nvarchar");
        Assert.Equal(200, nv.MaxLength);                                  // nvarchar(100) = 200 bytes
        var target = MirrorAll().Replace(t.With("n_nvarchar", nv with { MaxLength = 100 }));
        // Reported and skipped by default, fatal under --strict — either way nothing lands.
        Assert.Contains("n_nvarchar", Build(target).Skipped.Single(x => x.Name == "probe_notnull").Reason);
        Assert.DoesNotContain(Build(target).Tables, p => p.Source.Name == "probe_notnull");
        var ex = Assert.Throws<InvalidDataException>(() => Build(target, Strict));
        Assert.Contains("n_nvarchar", ex.Message, StringComparison.Ordinal);
        Assert.Contains("nvarchar(100)", ex.Message, StringComparison.Ordinal);   // the source's width
        Assert.Contains("nvarchar(50)", ex.Message, StringComparison.Ordinal);    // the target's
    }

    [Fact]
    public void AWiderTargetColumnIsAccepted()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var nv = t.Columns.Single(c => c.Name == "n_nvarchar");
        var plan = PlanFor(MirrorAll().Replace(t.With("n_nvarchar", nv with { MaxLength = 400 })), "probe_notnull");
        Assert.Contains(plan.Columns, c => c.Target.Name == "n_nvarchar");
    }

    [Fact]
    public void ADifferentTargetTypeIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var i = t.Columns.Single(c => c.Name == "n_int");
        var target = MirrorAll().Replace(t.With("n_int", i with { TypeName = "bigint", MaxLength = 8 }));
        Assert.DoesNotContain(Build(target).Tables, p => p.Source.Name == "probe_notnull");
        var ex = Assert.Throws<InvalidDataException>(() => Build(target, Strict));
        Assert.Contains("n_int", ex.Message, StringComparison.Ordinal);
        Assert.Contains("int", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bigint", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NumericAndDecimalAreTheSameType()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var d = t.Columns.Single(c => c.Name == "n_dec18_2");
        Assert.Equal("decimal", d.TypeName);
        var plan = PlanFor(MirrorAll().Replace(t.With("n_dec18_2", d with { TypeName = "numeric" })), "probe_notnull");
        Assert.Contains(plan.Columns, c => c.Target.Name == "n_dec18_2");
    }

    [Fact]
    public void ADifferentDecimalScaleIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var d = t.Columns.Single(c => c.Name == "n_dec38_20");
        var target = MirrorAll().Replace(t.With("n_dec38_20", d with { Scale = 2 }));
        var ex = Assert.Throws<InvalidDataException>(() => Build(target, Strict));
        Assert.Contains("n_dec38_20", ex.Message, StringComparison.Ordinal);
        Assert.Contains("decimal(38,20)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("decimal(38,2)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALowerDecimalPrecisionIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var d = t.Columns.Single(c => c.Name == "n_dec38_20");
        var target = MirrorAll().Replace(t.With("n_dec38_20", d with { Precision = 30 }));
        Assert.Contains("n_dec38_20",
            Assert.Throws<InvalidDataException>(() => Build(target, Strict)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetOnlyColumnThatCannotDefaultIsRefused()
    {
        // The container declares a NOT NULL column with no default that the source has no
        // value for: the insert would fail row by row inside the bulk copy, or worse, land
        // a column the caller never chose. Refused up front, by name.
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var target = MirrorAll().Replace(t.Plus(new TargetColumn(
            "extra_required", 99, "int", 4, 10, 0,
            IsNullable: false, IsIdentity: false, IsComputed: false, HasDefault: false)));
        var ex = Assert.Throws<InvalidDataException>(() => Build(target, Strict));
        Assert.Contains("extra_required", ex.Message, StringComparison.Ordinal);
        Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]   // nullable: NULL is a legitimate value for it
    [InlineData(false, true)]   // NOT NULL but defaulted: the target fills it in
    public void ATargetOnlyColumnThatCanDefaultIsReportedNotRefused(bool nullable, bool hasDefault)
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var target = MirrorAll().Replace(t.Plus(new TargetColumn(
            "extra_optional", 99, "int", 4, 10, 0,
            IsNullable: nullable, IsIdentity: false, IsComputed: false, HasDefault: hasDefault)));
        Assert.Equal(new[] { "extra_optional" }, PlanFor(target, "probe_notnull").TargetOnlyColumns);
    }

    [Fact]
    public void AnIdentityColumnIsWrittenWithTheSourceValue()
    {
        // A restore reproduces the source's keys; letting the target renumber them would
        // break every row that references one.
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var id = t.Columns.Single(c => c.Name == "id");
        var plan = PlanFor(MirrorAll().Replace(t.With("id", id with { IsIdentity = true })), "probe_notnull");
        Assert.True(plan.NeedsIdentityInsert);
        Assert.Contains(plan.Columns, c => c.Target.Name == "id");
    }

    [Fact]
    public void TablesAreMatchedCaseInsensitivelyAcrossTheSchema()
    {
        var target = MirrorAll()
            .Select(t => t.Name == "probe_notnull" ? t with { Name = "PROBE_NOTNULL" } : t).ToList();
        var plan = Build(target).Tables.Single(p => p.Source.Name == "probe_notnull");
        Assert.Equal("PROBE_NOTNULL", plan.Target.Name);
    }

    [Fact]
    public void TheSameTableNameInTwoSchemasIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var target = MirrorAll();
        target.Add(t with { Schema = "other" });
        // Ambiguity is fatal whatever the mode: there is no safe guess about which one.
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
        Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dbo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("other", ex.Message, StringComparison.Ordinal);
    }
}
