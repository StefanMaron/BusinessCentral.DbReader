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
        opts = ResolveCompany(src, target, opts, log);
        var (plans, skipped) = RestorePlanner.Build(src, target, opts);

        // A table left out by --table is not news — the caller asked for that — so it is
        // counted rather than listed. On a BC database that is 4,000 lines of noise around
        // the one line that matters.
        int filtered = 0;
        foreach (var s in skipped)
        {
            if (s.Reason == RestorePlanner.FilteredOut) { filtered++; continue; }
            // --exclude-table is a caller naming specific tables on purpose (typically a
            // short, deliberate list, e.g. the container's own login/session tables) — say
            // which ones, unlike the --table filter count above, which is usually thousands
            // of tables the caller did not ask about individually.
            log.WriteLine($"skip  {s.Name}: {s.Reason}");
        }
        if (filtered > 0) log.WriteLine($"skip  {filtered} tables not named by --table");
        if (plans.Count == 0)
            throw new InvalidDataException(
                $"none of the source's {src.Tables.Count} tables matched a table in {cn.Database} — "
                + "nothing would be written. Check that the container has the extensions this data came from "
                + "installed, and that the company names match (a cloud tenant's company is part of every table name).");
        foreach (var p in plans)
            log.WriteLine($"plan  {p.Source.Name} -> {p.Target.QuotedName}: {p.Columns.Count} columns"
                + (p.CreateTable ? ", creating the table" : "")
                + (p.AddColumns.Count > 0 ? $", adding {string.Join(", ", p.AddColumns.Select(c => c.Name))}" : "")
                + (p.TargetOnlyColumns.Count > 0 ? $", leaving {string.Join(", ", p.TargetOnlyColumns)} to the target" : "")
                + (p.DroppedColumns.Count > 0 ? $", dropping {string.Join(", ", p.DroppedColumns)} (--allow-column-loss)" : "")
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

    /// <summary>
    /// --rename-company, filled in when the caller did not name one: unambiguous only when
    /// there is exactly one company with data on the source side and exactly one on the
    /// target's. Refuses by name rather than guessing only once both sides genuinely look
    /// like they carry company data and still do not resolve to one candidate each — a
    /// target with *no* company-prefixed tables at all (a scratch database restoring a
    /// handful of company-agnostic tables by name, mainly the hermetic tests' own shape)
    /// means there is nothing to map into, so it is left exactly as the caller gave it
    /// (empty): today's no-rename behavior, unchanged, whatever the source looks like.
    /// </summary>
    static RestoreOptions ResolveCompany(IBcSource src, IReadOnlyList<TargetTable> target, RestoreOptions opts, TextWriter log)
    {
        if (opts.RenameCompany.Count > 0) return opts;

        var targetCompanies = RestorePlanner.TargetCompanies(target);
        if (targetCompanies.Count == 0) return opts;

        // A company --table/--exclude-table already scoped this restore away from should
        // not force a choice — same precedence idea as everywhere else here, just applied
        // to which tables are even looked at rather than to which get written.
        var only = new HashSet<string>(opts.OnlyTables, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(opts.ExcludeTables, StringComparer.OrdinalIgnoreCase);
        bool InScope(string name) => !exclude.Contains(name) && (only.Count == 0 || only.Contains(name));

        var sourceCompanies = RestorePlanner.SourceCompaniesWithData(src, InScope);
        if (sourceCompanies.Count == 0) return opts;

        if (sourceCompanies.Count == 1 && targetCompanies.Count == 1)
        {
            log.WriteLine($"company {sourceCompanies[0]} -> {targetCompanies[0]} "
                + "(auto-detected: the only company with data on each side)");
            return opts with { RenameCompany = new Dictionary<string, string> { [sourceCompanies[0]] = targetCompanies[0] } };
        }
        throw new ArgumentException(
            $"the source has {sourceCompanies.Count} compan{(sourceCompanies.Count == 1 ? "y" : "ies")} with data "
            + $"({string.Join(", ", sourceCompanies)}) and the target has {targetCompanies.Count} "
            + $"({string.Join(", ", targetCompanies)}) — pass --rename-company \"Src=Dst\" to say which maps to which");
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
        if (p.CreateTable)
        {
            Execute(cn, RestoreDdl.CreateTable(p.Target, p.SourceColumns, p.KeyColumns));
            log.WriteLine($"create {p.Target.QuotedName}: {p.SourceColumns.Count} columns"
                + (p.KeyColumns.Count > 0 ? $", key ({string.Join(", ", p.KeyColumns)})" : ", no key"));
        }
        else if (p.AddColumns.Count > 0)
        {
            bool empty = !HasRows(cn, p.Target);
            foreach (var c in p.AddColumns)
            {
                Execute(cn, RestoreDdl.AddColumn(p.Target, c, empty));
                // Said out loud: a column the source declares NOT NULL lands NULLable on a
                // table that already has rows, because SQL Server cannot do otherwise
                // without inventing a default for the rows already there.
                log.WriteLine($"alter {p.Target.QuotedName}: added {c.Name} {RestoreDdl.TypeText(c)}"
                    + (!c.IsNullable && !empty ? " as NULL (the table already has rows)" : ""));
            }
        }

        if (opts.Replace) Empty(cn, p.Target, log);
        else if (!p.CreateTable)
        {
            if (HasRows(cn, p.Target))
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

    static bool HasRows(SqlConnection cn, TargetTable t)
    {
        using var probe = new SqlCommand($"SELECT TOP 1 1 FROM {t.QuotedName}", cn) { CommandTimeout = 120 };
        return probe.ExecuteScalar() != null;
    }

    static void Execute(SqlConnection cn, string sql)
    {
        using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 0 };
        cmd.ExecuteNonQuery();
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
    /// <summary>
    /// One flag's on/off/unset state read off two opposite command-line switches (--create
    /// and --no-create, say), refusing the contradiction of both at once. Unset means the
    /// caller named neither, so the option's own default applies — everything but --replace
    /// resolves that silently; --replace's own "unset" is the one case with a third,
    /// mid-run answer (ask), which is why it is handled separately rather than through this.
    /// </summary>
    static bool? Toggle(Dictionary<string, string> opts, string onFlag, string offFlag)
    {
        bool on = opts.ContainsKey(onFlag);
        bool off = opts.ContainsKey(offFlag);
        if (on && off)
            throw new ArgumentException($"--{onFlag} and --{offFlag} name opposite choices — pass one or the other, not both");
        return on ? true : off ? false : null;
    }

    /// <summary>
    /// Reads the restore options off a parsed command line, refusing values that are not
    /// ones. <paramref name="needsReplaceConfirmation"/> is true when the caller named
    /// neither --replace nor --no-replace — replacing existing rows is the default *effect*,
    /// but is confirmed rather than assumed, so the returned options' own Replace is only
    /// provisional in that case; the caller resolves it (interactively or by refusing to run
    /// non-interactively) before using the options to restore anything.
    /// </summary>
    public static RestoreOptions OptionsFrom(Dictionary<string, string> opts, out string connection, out bool needsReplaceConfirmation)
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
        var exclude = opts.TryGetValue("exclude-table", out var ex)
            ? ex.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray()
            : Array.Empty<string>();

        var renameCompany = new Dictionary<string, string>();
        if (opts.TryGetValue("rename-company", out var rc))
            foreach (var pair in rc.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0)
                    throw new ArgumentException(
                        $"--rename-company expects \"<source company>=<target company>\" pairs, got '{pair}' with no '='");
                renameCompany[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }

        bool? replace = Toggle(opts, "replace", "no-replace");
        needsReplaceConfirmation = replace is null;

        return new RestoreOptions
        {
            OnlyTables = only,
            ExcludeTables = exclude,
            RenameCompany = renameCompany,
            IncludeSystem = opts.ContainsKey("include-system"),
            IncludeIdentity = opts.ContainsKey("include-identity"),
            Replace = replace ?? false,   // provisional when needsReplaceConfirmation — the caller resolves it
            CreateMissing = Toggle(opts, "create", "no-create") ?? false,
            AllowColumnLoss = Toggle(opts, "allow-column-loss", "no-allow-column-loss") ?? true,
            Strict = opts.ContainsKey("strict"),
            DryRun = opts.ContainsKey("dry-run"),
            BatchSize = batch,
        };
    }

    public static int Run(IBcSource src, Dictionary<string, string> opts)
    {
        var options = OptionsFrom(opts, out var connection, out var needsReplaceConfirmation);

        // --dry-run writes nothing, so there is nothing to confirm — asking would be
        // asking about an action that is not actually about to happen.
        if (needsReplaceConfirmation && !options.DryRun)
        {
            if (!TryConfirmReplace(out bool replace))
            {
                Console.WriteLine("Neither an interactive terminal nor --replace/--no-replace was given — "
                    + "refusing to guess whether existing rows should be replaced.");
                return 1;
            }
            if (!replace)
            {
                Console.WriteLine("Declined — nothing was written. Pass --replace or --no-replace to skip this prompt next time.");
                return 1;
            }
            options = options with { Replace = true };
        }

        // Progress goes to stderr so the one-line result on stdout stays parseable.
        var report = SqlRestore.Run(src, connection, options, Console.Error);
        Console.WriteLine(options.DryRun
            ? $"dry run: {report.Planned.Count} tables would be written, {report.Skipped.Count} skipped"
            : $"{report.Loaded.Count} tables, {report.Loaded.Sum(l => l.Rows)} rows written, {report.Skipped.Count} tables skipped");
        return 0;
    }

    /// <summary>
    /// Asks, on the real console, whether to replace existing rows — the interactive half of
    /// --replace's default. Returns false (without asking anything) when there is no
    /// interactive terminal to ask on, so a script that forgot --replace/--no-replace gets a
    /// clear refusal instead of hanging on a prompt nothing will ever answer.
    /// </summary>
    static bool TryConfirmReplace(out bool replace)
    {
        replace = false;
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return false;

        Console.Write("This restore will replace existing rows in any target table that already has data. "
            + "Continue? [y/N] ");
        var answer = (Console.ReadLine() ?? "").Trim();
        replace = answer.Equals("y", StringComparison.OrdinalIgnoreCase)
            || answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
        return true;
    }
}
