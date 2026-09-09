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

    static readonly RestoreOptions Default = new();

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
    public void ATableTheTargetDoesNotHaveIsSkippedByNameWithAReason()
    {
        // The expected mismatch: an extension the container has not installed. It is
        // reported, not fatal — but it is never silent.
        var target = MirrorAll().Where(t => t.Name != "probe_notnull").ToList();
        var skipped = Build(target).Skipped.Single(s => s.Name == "probe_notnull");
        Assert.Contains("no table", skipped.Reason, StringComparison.OrdinalIgnoreCase);
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
    public void OnlyTablesLimitsThePlanAndSkipsTheRestByName()
    {
        var plan = Build(MirrorAll(), new RestoreOptions { OnlyTables = new[] { "probe_notnull" } });
        Assert.Equal(new[] { "probe_notnull" }, plan.Tables.Select(p => p.Source.Name));
        Assert.Contains(plan.Skipped, s => s.Name == "probe_dense" && s.Reason.Contains("--table"));
    }

    [Fact]
    public void ASourceColumnWithNoTargetColumnIsRefused()
    {
        // Loading the other 25 columns and dropping this one would produce rows that look
        // complete. The tool stops and names the column instead.
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
        Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("n_nvarchar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSkipMismatchedTheMismatchedTableIsSkippedAndTheRestStillPlanned()
    {
        // The escape hatch, and its shape matters: the table is skipped whole, carrying
        // the mismatch as its reason, and no half-mapped plan for it exists.
        var target = MirrorAll().Replace(RestoreTestSchema.Mirror(_src, "probe_notnull").With("n_nvarchar", null));
        var plan = Build(target, new RestoreOptions { SkipMismatchedTables = true });

        var skipped = plan.Skipped.Single(s => s.Name == "probe_notnull");
        Assert.Contains("n_nvarchar", skipped.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Tables, p => p.Source.Name == "probe_notnull");
        Assert.Contains(plan.Tables, p => p.Source.Name == "probe_dense");
    }

    [Fact]
    public void ANarrowerTargetColumnIsRefused()
    {
        var t = RestoreTestSchema.Mirror(_src, "probe_notnull");
        var nv = t.Columns.Single(c => c.Name == "n_nvarchar");
        Assert.Equal(200, nv.MaxLength);                                  // nvarchar(100) = 200 bytes
        var target = MirrorAll().Replace(t.With("n_nvarchar", nv with { MaxLength = 100 }));
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
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
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
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
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
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
            Assert.Throws<InvalidDataException>(() => Build(target)).Message, StringComparison.Ordinal);
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
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
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
        var ex = Assert.Throws<InvalidDataException>(() => Build(target));
        Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dbo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("other", ex.Message, StringComparison.Ordinal);
    }
}
