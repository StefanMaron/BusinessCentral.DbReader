using BusinessCentral.DbReader;
using Microsoft.Data.SqlClient;
using Xunit;

/// <summary>
/// The one test that proves a restore actually lands: it writes fixtures/typeprobe.bak
/// into a real SQL Server and reads the rows back with SQL Server's own SELECT, comparing
/// them against the oracle fixture the same server family produced. Everything else in the
/// restore suite is hermetic and checks decisions and conversions; only this checks that
/// the bytes arrive.
///
/// It needs a server, so it is skippable — and skipped, never quietly passed, per the
/// no-silent-skips rule. Point BCDB_RESTORE_SQL at one to run it:
///
///   BCDB_RESTORE_SQL='Server=localhost,14330;User ID=sa;Password=...;TrustServerCertificate=True' \
///     dotnet test BcDb.sln -c Release
///
/// The oracle container is exactly such a server (CLAUDE.md, "The oracle"), and verify.sh
/// passes it in. The test creates and drops its own scratch database and touches nothing
/// else on the server.
/// </summary>
public class SqlRestoreIntegrationTests
{
    const string EnvVar = "BCDB_RESTORE_SQL";
    const string ScratchDb = "bcdb_restore_test";

    /// <summary>Same shape as tools/typeprobe.sql's probe_notnull — every supported type, plus a rowversion.</summary>
    const string ProbeNotNullDdl = """
        CREATE TABLE probe_notnull (
          id int NOT NULL, n_tinyint tinyint NOT NULL, n_smallint smallint NOT NULL,
          n_int int NOT NULL, n_bigint bigint NOT NULL, n_bit bit NOT NULL,
          n_dec38_20 decimal(38,20) NOT NULL, n_dec18_2 decimal(18,2) NOT NULL, n_dec5_0 decimal(5,0) NOT NULL,
          n_datetime datetime NOT NULL, n_datetime2_7 datetime2(7) NOT NULL, n_datetime2_0 datetime2(0) NOT NULL,
          n_date date NOT NULL, n_time7 time(7) NOT NULL, n_time0 time(0) NOT NULL,
          n_guid uniqueidentifier NOT NULL, n_nvarchar nvarchar(100) NOT NULL, n_varchar varchar(100) NOT NULL,
          n_nchar nchar(10) NOT NULL, n_char char(10) NOT NULL, n_binary binary(8) NOT NULL,
          n_varbinary varbinary(100) NOT NULL, n_real real NOT NULL, n_float float NOT NULL,
          n_vbmax varbinary(max) NOT NULL, n_nvmax nvarchar(max) NOT NULL, n_ver rowversion NOT NULL,
          CONSTRAINT pk_probe_notnull PRIMARY KEY CLUSTERED (id));
        """;

    /// <summary>The fixture's own SELECT (tools/export-fixtures.sh), minus the rowversion field.</summary>
    const string ReadBackSql = """
        SELECT CONCAT(CAST(id AS varchar(max)),'|',CAST(n_tinyint AS varchar(5)),'|',CAST(n_smallint AS varchar(8)),'|',
          CAST(n_int AS varchar(12)),'|',CAST(n_bigint AS varchar(22)),'|',CAST(CAST(n_bit AS int) AS varchar(4)),'|',
          CONVERT(varchar(60),n_dec38_20),'|',CONVERT(varchar(30),n_dec18_2),'|',CONVERT(varchar(10),n_dec5_0),'|',
          CONVERT(varchar(30),n_datetime,121),'|',CONVERT(varchar(40),n_datetime2_7,121),'|',CONVERT(varchar(40),n_datetime2_0,121),'|',
          CONVERT(varchar(10),n_date,121),'|',CONVERT(varchar(20),n_time7,121),'|',CONVERT(varchar(10),n_time0,121),'|',
          CONVERT(varchar(36),n_guid),'|',n_nvarchar,'|',n_varchar,'|',CAST(n_nchar AS nvarchar(10)),'|',
          CAST(n_char AS varchar(10)),'|','0x'+CONVERT(varchar(20),n_binary,2),'|','0x'+CONVERT(varchar(220),n_varbinary,2),'|',
          '0x'+CONVERT(varchar(max),n_vbmax,2),'|',n_nvmax) FROM probe_notnull ORDER BY id
        """;

    static string? Server => Environment.GetEnvironmentVariable(EnvVar);

    static string ScratchConnection()
    {
        var b = new SqlConnectionStringBuilder(Server) { InitialCatalog = ScratchDb };
        return b.ConnectionString;
    }

    static void Exec(string connection, string sql)
    {
        using var cn = new SqlConnection(connection);
        cn.Open();
        foreach (var batch in sql.Split("\nGO\n", StringSplitOptions.RemoveEmptyEntries))
        {
            using var cmd = new SqlCommand(batch, cn) { CommandTimeout = 120 };
            cmd.ExecuteNonQuery();
        }
    }

    static List<string> Query(string connection, string sql)
    {
        using var cn = new SqlConnection(connection);
        cn.Open();
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
        using var r = cmd.ExecuteReader();
        var rows = new List<string>();
        while (r.Read()) rows.Add(r.GetString(0));
        return rows;
    }

    /// <summary>A freshly created scratch database holding an empty probe_notnull.</summary>
    static string FreshScratchDatabase()
    {
        var master = new SqlConnectionStringBuilder(Server) { InitialCatalog = "master" }.ConnectionString;
        Exec(master, $"""
            IF DB_ID('{ScratchDb}') IS NOT NULL
            BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END;
            CREATE DATABASE [{ScratchDb}];
            """);
        var scratch = ScratchConnection();
        Exec(scratch, ProbeNotNullDdl);
        return scratch;
    }

    static void DropScratchDatabase()
    {
        var master = new SqlConnectionStringBuilder(Server) { InitialCatalog = "master" }.ConnectionString;
        Exec(master, $"""
            IF DB_ID('{ScratchDb}') IS NOT NULL
            BEGIN ALTER DATABASE [{ScratchDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{ScratchDb}]; END;
            """);
    }

    static List<string> ExpectedRows()
        => File.ReadAllLines(Path.Combine(RestoreTestSchema.Root, "fixtures", "typeprobe-probe-notnull.tsv"))
            .Where(l => l.Length > 0)
            .Select(l => l.EndsWith("|#", StringComparison.Ordinal) ? l[..^2] : l)
            .Select(l => l[..l.LastIndexOf('|')])       // drop the rowversion field
            .ToList();

    [SkippableFact]
    public void RowsWrittenIntoARealServerReadBackAsTheOracleValues()
    {
        Skip.If(string.IsNullOrEmpty(Server),
            $"{EnvVar} is not set — the restore round trip needs a real SQL Server "
            + "(e.g. BCDB_RESTORE_SQL='Server=localhost,14330;User ID=sa;Password=…;TrustServerCertificate=True')");

        var scratch = FreshScratchDatabase();
        try
        {
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var report = SqlRestore.Run(src, scratch,
                new RestoreOptions { OnlyTables = new[] { "probe_notnull" } }, TextWriter.Null);

            var loaded = Assert.Single(report.Loaded);
            Assert.Equal("probe_notnull", loaded.Table);
            Assert.Equal(3, loaded.Rows);
            Assert.Equal(ExpectedRows(), Query(scratch, ReadBackSql));

            // The rowversion was stamped by the server, not carried over from the source.
            Assert.Equal(3, Query(scratch,
                "SELECT CONVERT(varchar(20), CONVERT(varbinary(8), n_ver), 2) FROM probe_notnull")
                .Distinct().Count());
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void ANonEmptyTargetIsRefusedUnlessReplaceWasAsked()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();
        try
        {
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var only = new RestoreOptions { OnlyTables = new[] { "probe_notnull" } };
            SqlRestore.Run(src, scratch, only, TextWriter.Null);

            // Loading again would double the rows or violate the key: refused by name.
            var ex = Assert.Throws<InvalidDataException>(() => SqlRestore.Run(src, scratch, only, TextWriter.Null));
            Assert.Contains("probe_notnull", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--replace", ex.Message, StringComparison.Ordinal);
            Assert.Equal(3, Query(scratch, "SELECT CAST(COUNT(*) AS varchar(10)) FROM probe_notnull").Select(int.Parse).Single());

            // With --replace it empties the table first and lands the same rows again.
            var report = SqlRestore.Run(src, scratch, only with { Replace = true }, TextWriter.Null);
            Assert.Equal(3, Assert.Single(report.Loaded).Rows);
            Assert.Equal(ExpectedRows(), Query(scratch, ReadBackSql));
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void ADryRunPlansAndWritesNothing()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();
        try
        {
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var log = new StringWriter();
            var report = SqlRestore.Run(src, scratch,
                new RestoreOptions { OnlyTables = new[] { "probe_notnull" }, DryRun = true }, log);

            Assert.Empty(report.Loaded);
            Assert.Contains("probe_notnull", report.Planned.Select(p => p.Source.Name));
            Assert.Contains("probe_notnull", log.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, Query(scratch, "SELECT CAST(COUNT(*) AS varchar(10)) FROM probe_notnull").Select(int.Parse).Single());
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void TablesTheTargetDoesNotHaveAreCreatedAndLoaded()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();     // holds probe_notnull and nothing else
        try
        {
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var report = SqlRestore.Run(src, scratch, new RestoreOptions { CreateMissing = true }, TextWriter.Null);

            // probe_notnull was there; everything else the source has was created (--create).
            Assert.Contains(report.Loaded, l => l.Table == "probe_notnull");
            Assert.Contains(report.Loaded, l => l.Table == "probe_dense");
            Assert.Contains(report.Planned, p => p.Source.Name == "probe_dense" && p.CreateTable);
            Assert.Contains(report.Planned, p => p.Source.Name == "probe_notnull" && !p.CreateTable);
            // The platform table is still left alone — creating is permissive, not blind.
            Assert.Contains(report.Skipped, s => s.Name == "$probe$platform");
            Assert.DoesNotContain(report.Loaded, l => l.Table == "$probe$platform");

            // The table that was already there still reads back as the oracle's values.
            Assert.Equal(ExpectedRows(), Query(scratch, ReadBackSql));

            // A created table holds every row, and carries the source's key as a clustered
            // primary key — the shape BC's own schema synchronisation produces.
            Assert.Equal(4000, Query(scratch, "SELECT CAST(COUNT(*) AS varchar(10)) FROM probe_dense")
                .Select(int.Parse).Single());
            Assert.Equal(new[] { "probe_dense$Key1|CLUSTERED|1|id" }, Query(scratch, """
                SELECT CONCAT(i.name COLLATE DATABASE_DEFAULT,'|',i.type_desc COLLATE DATABASE_DEFAULT,
                              '|',i.is_primary_key,'|',c.name COLLATE DATABASE_DEFAULT)
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
                JOIN sys.columns c ON c.object_id=i.object_id AND c.column_id=ic.column_id
                WHERE i.object_id=OBJECT_ID('probe_dense') AND i.index_id=1 ORDER BY ic.key_ordinal
                """));

            // Created columns carry the source's own nullability, not a guess.
            Assert.Equal(new[] { "c_nvarchar|1", "id|0" }, Query(scratch, """
                SELECT CONCAT(name COLLATE DATABASE_DEFAULT,'|',is_nullable) FROM sys.columns
                WHERE object_id=OBJECT_ID('probe') AND name IN ('id','c_nvarchar') ORDER BY name
                """));
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void AColumnTheTargetTableLacksIsAddedToIt()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();
        try
        {
            Exec(scratch, "ALTER TABLE probe_notnull DROP COLUMN n_nvarchar");
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var report = SqlRestore.Run(src, scratch,
                new RestoreOptions { OnlyTables = new[] { "probe_notnull" }, CreateMissing = true }, TextWriter.Null);

            var plan = Assert.Single(report.Planned);
            Assert.Equal(new[] { "n_nvarchar" }, plan.AddColumns.Select(c => c.Name));
            // The column came back and its values came with it.
            Assert.Equal(ExpectedRows(), Query(scratch, ReadBackSql));
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void WithNoCreateAMissingTableIsReportedAndTheRestStillLoads()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();     // holds probe_notnull and nothing else
        try
        {
            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var report = SqlRestore.Run(src, scratch,
                new RestoreOptions { CreateMissing = false }, TextWriter.Null);

            Assert.Equal(new[] { "probe_notnull" }, report.Loaded.Select(l => l.Table));
            Assert.Contains(report.Skipped, s => s.Name == "probe_dense");
            Assert.Equal(ExpectedRows(), Query(scratch, ReadBackSql));
        }
        finally { DropScratchDatabase(); }
    }

    // typeprobe.bak's one real, data-bearing company besides "TP" (RestorePlanTests'
    // TpExttest) — restoring the whole file, unscoped, always sees both, which is exactly
    // what the ambiguous-company test below wants.
    const string TpExttest = "TP$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [SkippableFact]
    public void CompanyAutoDetectsWhenExactlyOneWithDataOnEachSide()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();
        try
        {
            // Give the target exactly one company-shaped table — company "TargetCo" — so
            // TargetCompanies finds exactly one candidate to auto-map into.
            Exec(scratch, "CREATE TABLE [TargetCo$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa] (dummy int)");

            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            var log = new StringWriter();
            // --table scopes the source to just "TP"'s table, so it is the only source
            // company in view even though the file also carries "ProbeCo" data.
            var report = SqlRestore.Run(src, scratch,
                new RestoreOptions { OnlyTables = new[] { TpExttest }, DryRun = true }, log);

            var plan = Assert.Single(report.Planned);
            Assert.Equal("[dbo].[TargetCo$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa]", plan.Target.QuotedName);
            Assert.Contains("TP -> TargetCo", log.ToString(), StringComparison.Ordinal);
            Assert.Contains("auto-detected", log.ToString(), StringComparison.Ordinal);
        }
        finally { DropScratchDatabase(); }
    }

    [SkippableFact]
    public void CompanyResolutionRefusesToGuessWhenAmbiguous()
    {
        Skip.If(string.IsNullOrEmpty(Server), $"{EnvVar} is not set — this test needs a real SQL Server");

        var scratch = FreshScratchDatabase();
        try
        {
            Exec(scratch, "CREATE TABLE [TargetCo$exttest$aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa] (dummy int)");

            using var src = BcSource.Open(RestoreTestSchema.TypeprobeBak);
            // Unscoped: the source has both "TP" and "ProbeCo" with real data, so there is
            // no single company to auto-map — refused by name, not guessed at.
            var ex = Assert.Throws<ArgumentException>(() =>
                SqlRestore.Run(src, scratch, new RestoreOptions { DryRun = true }, TextWriter.Null));
            Assert.Contains("TP", ex.Message, StringComparison.Ordinal);
            Assert.Contains("ProbeCo", ex.Message, StringComparison.Ordinal);
            Assert.Contains("--rename-company", ex.Message, StringComparison.Ordinal);
        }
        finally { DropScratchDatabase(); }
    }
}
