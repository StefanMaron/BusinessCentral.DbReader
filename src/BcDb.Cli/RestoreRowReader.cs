using System.Data;
using System.Diagnostics.CodeAnalysis;

namespace BusinessCentral.DbReader;

/// <summary>
/// The source's rows for one table, presented as the forward-only reader a bulk-copy
/// client consumes. It is a streaming adapter and nothing more: rows are pulled from the
/// source one at a time and converted one at a time, so a table larger than memory loads
/// the same way a small one does.
///
/// The whole row is converted in <see cref="Read"/> rather than lazily in GetValue, so a
/// value that cannot be carried across fails at a known point — with the table, column and
/// row number in the message — instead of somewhere inside the client's own column loop.
/// </summary>
public sealed class RestoreRowReader : IDataReader
{
    readonly IReadOnlyList<ColumnMapping> _cols;
    readonly IEnumerator<IReadOnlyDictionary<string, object?>> _rows;
    readonly string _table;
    readonly object[] _current;

    public RestoreRowReader(string table, IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        IReadOnlyList<ColumnMapping> columns)
    {
        _table = table;
        _cols = columns;
        _rows = rows.GetEnumerator();
        _current = new object[columns.Count];
    }

    /// <summary>Rows handed out so far — what the progress line reports.</summary>
    public long RowsRead { get; private set; }

    public int FieldCount => _cols.Count;

    public bool Read()
    {
        if (!_rows.MoveNext()) return false;
        var row = _rows.Current;
        for (int i = 0; i < _cols.Count; i++)
        {
            var m = _cols[i];
            try { _current[i] = RestoreValues.ToSqlValue(row[m.Source.Name], m.Source, m.Target); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                // Which row, so a failure on row 400,000 of a table is something a caller
                // can go and look at rather than a message about a column name.
                throw new InvalidDataException($"{_table}, row {RowsRead + 1}: {ex.Message}", ex);
            }
        }
        RowsRead++;
        return true;
    }

    public object GetValue(int i) => _current[i];

    public string GetName(int i) => _cols[i].Target.Name;

    public int GetOrdinal(string name)
    {
        for (int i = 0; i < _cols.Count; i++)
            if (string.Equals(_cols[i].Target.Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new IndexOutOfRangeException($"{_table}: no column '{name}' in the restore row reader");
    }

    // IDataRecord annotates this return value for the trimmer — a caller may reflect over
    // the returned type's public fields and properties — and an override has to carry the
    // identical annotation or the AOT analyzer rejects it (IL2093).
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public Type GetFieldType(int i) => RestoreValues.ClrType(_cols[i].Source, _cols[i].Target);

    public bool IsDBNull(int i) => _current[i] is DBNull;

    public string GetDataTypeName(int i) => _cols[i].Target.TypeName;

    public void Close() { _rows.Dispose(); IsClosed = true; }

    public void Dispose() => Close();

    public bool IsClosed { get; private set; }
    public int Depth => 0;
    public int RecordsAffected => -1;
    public bool NextResult() => false;

    // A bulk-copy client reads values through GetValue and never asks for these. Any of
    // them being called means this adapter is being used for something it was not written
    // for, and guessing an answer there would be a silent wrong result.
    public object this[int i] => GetValue(i);
    public object this[string name] => GetValue(GetOrdinal(name));
    public bool GetBoolean(int i) => throw Unsupported(nameof(GetBoolean));
    public byte GetByte(int i) => throw Unsupported(nameof(GetByte));
    public long GetBytes(int i, long off, byte[]? buffer, int bufferOffset, int length) => throw Unsupported(nameof(GetBytes));
    public char GetChar(int i) => throw Unsupported(nameof(GetChar));
    public long GetChars(int i, long off, char[]? buffer, int bufferOffset, int length) => throw Unsupported(nameof(GetChars));
    public IDataReader GetData(int i) => throw Unsupported(nameof(GetData));
    public DateTime GetDateTime(int i) => throw Unsupported(nameof(GetDateTime));
    public decimal GetDecimal(int i) => throw Unsupported(nameof(GetDecimal));
    public double GetDouble(int i) => throw Unsupported(nameof(GetDouble));
    public float GetFloat(int i) => throw Unsupported(nameof(GetFloat));
    public Guid GetGuid(int i) => throw Unsupported(nameof(GetGuid));
    public short GetInt16(int i) => throw Unsupported(nameof(GetInt16));
    public int GetInt32(int i) => throw Unsupported(nameof(GetInt32));
    public long GetInt64(int i) => throw Unsupported(nameof(GetInt64));
    public string GetString(int i) => throw Unsupported(nameof(GetString));
    public int GetValues(object[] values) => throw Unsupported(nameof(GetValues));
    public DataTable? GetSchemaTable() => throw Unsupported(nameof(GetSchemaTable));

    NotSupportedException Unsupported(string member)
        => new($"{_table}: RestoreRowReader.{member} is not implemented — this reader exists to feed a bulk copy, "
               + "which reads values through GetValue");
}
