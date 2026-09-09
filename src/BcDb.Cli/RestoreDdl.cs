namespace BusinessCentral.DbReader;

/// <summary>
/// The DDL a restore emits when the target does not have a table (or a column) the source
/// does. Nothing here invents schema: every type, width, scale and nullability comes from
/// the source's own catalog, so a created table is the one the source had.
///
/// Shaped after what Business Central's own schema synchronisation produces, read off a
/// live BC 28.4 container (PROVENANCE.md, "What BC's own tables look like"):
///
///   CREATE TABLE [dbo].[CRONUS International Ltd_$Customer$437dbf0e-…] (
///     [timestamp] rowversion NOT NULL,
///     [No_] nvarchar(20) NOT NULL,
///     …
///     CONSTRAINT [CRONUS International Ltd_$Customer$437dbf0e-…$Key1] PRIMARY KEY CLUSTERED ([No_])
///   );
///
/// Two things deliberately absent. Column collation: BC's text columns are
/// Latin1_General_100_CS_AS and so is the database's own default collation, so a column
/// created without one inherits exactly that — naming it would only be a way to get it
/// wrong on a database collated differently. Column defaults: BC declares one on nearly
/// every column, but a restore supplies every column of every row it writes, and the
/// service tier's schema synchronisation adds the defaults it wants when the extension
/// that owns the table is installed.
/// </summary>
public static class RestoreDdl
{
    /// <summary>Bracket-quote one identifier.</summary>
    public static string Quote(string name) => $"[{name.Replace("]", "]]")}]";

    /// <summary>
    /// The type as DDL writes it — "nvarchar(100)", "decimal(38,20)", "varbinary(max)",
    /// "datetime2(7)", "int". A rowversion is emitted as `rowversion`, the modern spelling
    /// of the `timestamp` sys.types reports.
    /// </summary>
    public static string TypeText(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "timestamp" or "rowversion" => "rowversion",
        "nvarchar" or "nchar" => type + (maxLength < 0 ? "(max)" : $"({maxLength / 2})"),
        "varchar" or "char" or "varbinary" or "binary" => type + (maxLength < 0 ? "(max)" : $"({maxLength})"),
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "time" or "datetime2" or "datetimeoffset" => $"{type}({scale})",
        _ => type,
    };

    public static string TypeText(SysColumn c) => TypeText(c.TypeName, c.MaxLength, c.Precision, c.Scale);
    public static string TypeText(TargetColumn c) => TypeText(c.TypeName, c.MaxLength, c.Precision, c.Scale);

    /// <summary>One column's DDL fragment.</summary>
    public static string ColumnText(SysColumn c)
        => $"{Quote(c.Name)} {TypeText(c)} {(c.IsNullable ? "NULL" : "NOT NULL")}";

    /// <summary>
    /// CREATE TABLE for a table the target does not have, from the source's own columns
    /// and key. The key becomes a clustered primary key, the way BC's does — but only when
    /// every one of its columns is NOT NULL, because SQL Server refuses a primary key over
    /// a nullable column and a table with its rows is worth more than a table with its key.
    /// </summary>
    public static string CreateTable(TargetTable target, IReadOnlyList<SysColumn> columns,
        IReadOnlyList<string> keyColumns)
    {
        if (columns.Count == 0)
            throw new InvalidDataException($"cannot create {target.QuotedName}: the source reports no columns for it");

        var lines = columns.Select(ColumnText).ToList();
        bool keyable = keyColumns.Count > 0 && keyColumns.All(k =>
            columns.Any(c => c.Name.Equals(k, StringComparison.OrdinalIgnoreCase) && !c.IsNullable));
        if (keyable)
            lines.Add($"CONSTRAINT {Quote(target.Name + "$Key1")} PRIMARY KEY CLUSTERED ("
                      + string.Join(", ", keyColumns.Select(Quote)) + ")");

        return $"CREATE TABLE {target.QuotedName} (\n  " + string.Join(",\n  ", lines) + "\n)";
    }

    /// <summary>
    /// ALTER TABLE … ADD for a column the target's table lacks. A column is added exactly
    /// as the source declares it only while the table is empty; SQL Server cannot add a
    /// NOT NULL column with no default to a table that already has rows, so on a non-empty
    /// table it is added NULL and the caller says so.
    /// </summary>
    public static string AddColumn(TargetTable target, SysColumn c, bool tableIsEmpty)
    {
        string nullability = c.IsNullable || !tableIsEmpty ? "NULL" : "NOT NULL";
        return $"ALTER TABLE {target.QuotedName} ADD {Quote(c.Name)} {TypeText(c)} {nullability}";
    }
}
