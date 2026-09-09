using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace BusinessCentral.DbReader;

/// <summary>One table that was written, and what it cost.</summary>
public sealed record TableLoad(string Table, long Rows, TimeSpan Elapsed);

/// <summary>What a restore planned, wrote and left alone.</summary>
public sealed record RestoreReport(
    IReadOnlyList<TablePlan> Planned,
    IReadOnlyList<TableLoad> Loaded,
    IReadOnlyList<SkippedTable> Skipped);

/// <summary>
/// Writes a source's rows into a database that already exists — the whole point of the
/// command. The target is a Business Central container: somebody started a CRONUS image,
/// installed the extensions the data was exported from, and wants the data itself, which
/// is the one thing installing extensions does not bring.
///
/// It is deliberately not an import: no database is created, no schema is deployed, no
/// service tier is involved. The container's schema is the authority, this only carries
/// rows into it, and the plan (RestorePlan.cs) refuses anything that would not land
/// exactly. Rows go across with SqlBulkCopy, streamed from the source a row at a time, so
/// a table larger than memory costs the same as a small one.
///
/// Bulk copy does not check constraints or fire triggers unless asked, which is what makes
/// table order irrelevant: a child table may be loaded before its parent, exactly as
/// SQL Server's own bulk load behaves.
/// </summary>
public static class SqlRestore
{
    /// <summary>Progress line every this many rows, for tables big enough to wonder about.</summary>
    const int NotifyEvery = 100_000;

    public static RestoreReport Run(IBcSource src, string connectionString, RestoreOptions opts, TextWriter log)
    {
        using var cn = Connect(connectionString);
        var target = ReadSchema(cn);
        var (plans, skipped) = RestorePlanner.Build(src, target, opts);

        foreach (var s in skipped) log.WriteLine($"skip  {s.Name}: {s.Reason}");
        if (plans.Count == 0)
            throw new InvalidDataException(
                $"none of the source's {src.Tables.Count} tables matched a table in {cn.Database} — "
                + "nothing would be written. Check that the container has the extensions this data came from "
                + "installed, and that the company names match (a cloud tenant's company is part of every table name).");
        foreach (var p in plans)
            log.WriteLine($"plan  {p.Source.Name} -> {p.Target.QuotedName}: {p.Columns.Count} columns"
                + (p.TargetOnlyColumns.Count > 0 ? $", leaving {string.Join(", ", p.TargetOnlyColumns)} to the target" : "")
                + (p.NeedsIdentityInsert ? ", keeping the source's identity values" : ""));

        if (opts.DryRun)
        {
            log.WriteLine("--dry-run: nothing was written");
            return new RestoreReport(plans, Array.Empty<TableLoad>(), skipped);
        }

        var loaded = new List<TableLoad>();
        foreach (var p in plans) loaded.Add(LoadTable(cn, src, p, opts, log));
        return new RestoreReport(plans, loaded, skipped);
    }

    static SqlConnection Connect(string connectionString)
    {
        SqlConnectionStringBuilder b;
        try { b = new SqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException ex)
        {
            throw new ArgumentException($"--to is not a SQL Server connection string: {ex.Message}");
        }
        if (string.IsNullOrEmpty(b.InitialCatalog))
            throw new ArgumentException(
                "--to needs the database to write into, e.g. \"Server=localhost;Database=CRONUS;User ID=sa;"
                + "Password=…;TrustServerCertificate=True\" — bcdb restore writes into a database that already exists");

        var cn = new SqlConnection(b.ConnectionString);
        try { cn.Open(); }
        catch (Exception ex)
        {
            cn.Dispose();
            // The near-universal first failure: a container's certificate is self-signed
            // and the client encrypts by default, so the connection fails on the
            // certificate rather than on anything the user got wrong. Only said when the
            // failure actually was the certificate — appending it to an unreachable-host
            // error would send the reader after the wrong problem.
            bool certificate = ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                               || ex.Message.Contains("trust", StringComparison.OrdinalIgnoreCase);
            string hint = certificate && b.Encrypt && !b.TrustServerCertificate
                ? " — a BC container's certificate is self-signed; add TrustServerCertificate=True to the connection string"
                : "";
            throw new InvalidDataException($"cannot connect to {b.DataSource}/{b.InitialCatalog}: {ex.Message}{hint}");
        }
        return cn;
    }

    /// <summary>
    /// The target's tables and columns, from the catalog views. This is the only schema
    /// that matters: what the container declares right now, not what the source file
    /// remembers. is_ms_shipped excludes the engine's own objects; everything else,
    /// including BC's $ndo$… tables, is reported and filtered further up.
    /// </summary>
    public static IReadOnlyList<TargetTable> ReadSchema(SqlConnection cn)
    {
        const string sql = """
            SELECT s.name, t.name, c.name, c.column_id, ty.name, c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed,
                   CASE WHEN c.default_object_id <> 0 THEN 1 ELSE 0 END
            FROM sys.columns c
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id
            """;
        var tables = new List<TargetTable>();
        var columns = new List<TargetColumn>();
        string? schema = null, table = null;

        using (var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 })
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                string s = r.GetString(0), t = r.GetString(1);
                if (schema != null && (s != schema || t != table))
                {
                    tables.Add(new TargetTable(schema, table!, columns));
                    columns = new List<TargetColumn>();
                }
                schema = s;
                table = t;
                columns.Add(new TargetColumn(
                    r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetInt16(5), r.GetByte(6), r.GetByte(7),
                    IsNullable: r.GetBoolean(8), IsIdentity: r.GetBoolean(9), IsComputed: r.GetBoolean(10),
                    HasDefault: r.GetInt32(11) == 1));
            }
        }
        if (schema != null) tables.Add(new TargetTable(schema, table!, columns));
        return tables;
    }

    static TableLoad LoadTable(SqlConnection cn, IBcSource src, TablePlan p, RestoreOptions opts, TextWriter log)
    {
        if (opts.Replace) Empty(cn, p.Target, log);
        else
        {
            using var probe = new SqlCommand($"SELECT TOP 1 1 FROM {p.Target.QuotedName}", cn) { CommandTimeout = 120 };
            if (probe.ExecuteScalar() != null)
                throw new InvalidDataException(
                    $"{p.Target.QuotedName} already has rows — pass --replace to empty every table this restore "
                    + "writes, or leave this one out with --table. Adding the source's rows to the ones already "
                    + "there would give a database that is neither.");
        }

        // KeepNulls: a NULL in the source is a NULL in the target, not the column's
        // default. KeepIdentity: a restore reproduces the source's keys — letting the
        // target renumber them would break every row that references one.
        var options = SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock;
        if (p.NeedsIdentityInsert) options |= SqlBulkCopyOptions.KeepIdentity;

        using var bulk = new SqlBulkCopy(cn, options, externalTransaction: null)
        {
            DestinationTableName = p.Target.QuotedName,
            BatchSize = opts.BatchSize,
            BulkCopyTimeout = 0,            // a large table takes as long as it takes
            NotifyAfter = NotifyEvery,
        };
        foreach (var m in p.Columns) bulk.ColumnMappings.Add(m.Target.Name, m.Target.Name);
        bulk.SqlRowsCopied += (_, e) => log.WriteLine($"      {p.Source.Name}: {e.RowsCopied} rows");

        var cols = p.Columns.Select(m => m.Source).ToList();
        using var reader = new RestoreRowReader(p.Source.Name, src.ReadRows(p.Source, cols), p.Columns);
        var sw = Stopwatch.StartNew();
        try { bulk.WriteToServer(reader); }
        catch (Exception ex)
        {
            // A bulk copy commits batch by batch, so a failure part way through leaves the
            // table holding the batches that already went. Saying so is the difference
            // between a caller re-running with --replace and a caller trusting a table
            // that is missing its tail.
            throw new InvalidDataException(
                $"{p.Target.QuotedName}: the load failed after {reader.RowsRead} rows and the table now holds "
                + $"part of them — re-run with --replace once the cause is fixed. {ex.Message}", ex);
        }
        sw.Stop();
        log.WriteLine($"load  {p.Source.Name}: {reader.RowsRead} rows in {sw.ElapsedMilliseconds} ms");
        return new TableLoad(p.Source.Name, reader.RowsRead, sw.Elapsed);
    }

    /// <summary>
    /// TRUNCATE where the target allows it, DELETE where a foreign key refers to the table.
    /// The fallback is reported rather than silent: DELETE is slower by orders of magnitude
    /// on a large table and it is worth knowing which tables took it.
    /// </summary>
    static void Empty(SqlConnection cn, TargetTable t, TextWriter log)
    {
        try
        {
            using var truncate = new SqlCommand($"TRUNCATE TABLE {t.QuotedName}", cn) { CommandTimeout = 0 };
            truncate.ExecuteNonQuery();
        }
        catch (SqlException ex)
        {
            log.WriteLine($"      {t.QuotedName}: TRUNCATE refused ({ex.Message.Split('\n')[0]}), emptying with DELETE");
            using var delete = new SqlCommand($"DELETE FROM {t.QuotedName}", cn) { CommandTimeout = 0 };
            delete.ExecuteNonQuery();
        }
    }
}

/// <summary>The `bcdb restore` subcommand: its options, and what it prints.</summary>
public static class RestoreCommand
{
    /// <summary>Reads the restore options off a parsed command line, refusing values that are not ones.</summary>
    public static RestoreOptions OptionsFrom(Dictionary<string, string> opts, out string connection)
    {
        if (!opts.TryGetValue("to", out var to))
            throw new ArgumentException(
                "restore needs --to \"<connection string>\": the database to write into, e.g. "
                + "--to \"Server=localhost;Database=CRONUS;User ID=sa;Password=…;TrustServerCertificate=True\"");
        connection = to;

        int batch = new RestoreOptions().BatchSize;
        if (opts.TryGetValue("batch-size", out var bs) && (!int.TryParse(bs, out batch) || batch <= 0))
            throw new ArgumentException($"--batch-size expects a positive number of rows, got '{bs}'");

        var only = opts.TryGetValue("table", out var t)
            ? t.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

        return new RestoreOptions
        {
            OnlyTables = only,
            IncludeSystem = opts.ContainsKey("include-system"),
            Replace = opts.ContainsKey("replace"),
            SkipMismatchedTables = opts.ContainsKey("skip-mismatched"),
            DryRun = opts.ContainsKey("dry-run"),
            BatchSize = batch,
        };
    }

    public static int Run(IBcSource src, Dictionary<string, string> opts)
    {
        var options = OptionsFrom(opts, out var connection);
        // Progress goes to stderr so the one-line result on stdout stays parseable.
        var report = SqlRestore.Run(src, connection, options, Console.Error);
        Console.WriteLine(options.DryRun
            ? $"dry run: {report.Planned.Count} tables would be written, {report.Skipped.Count} skipped"
            : $"{report.Loaded.Count} tables, {report.Loaded.Sum(l => l.Rows)} rows written, {report.Skipped.Count} tables skipped");
        return 0;
    }
}
