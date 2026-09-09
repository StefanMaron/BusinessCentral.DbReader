using BusinessCentral.DbReader;
using Xunit;

/// <summary>
/// The restore command's own command-line surface. It follows the same rule as every
/// other command: an option it does not accept fails the command rather than being
/// dropped, because a --replce that silently does nothing is a restore that appended to
/// the container's existing rows instead of replacing them.
/// </summary>
public class RestoreCliTests
{
    static string Refused(params string[] args)
        => Assert.Throws<ArgumentException>(() => Program.ParseOpts(args, "restore")).Message;

    [Fact]
    public void ItAcceptsItsOwnOptions()
    {
        var o = Program.ParseOpts(new[]
        {
            "--to", "Server=localhost;Database=CRONUS;", "--replace", "--dry-run",
            "--include-system", "--skip-mismatched", "--batch-size", "5000", "--table", "a,b",
        }, "restore");
        Assert.Equal("Server=localhost;Database=CRONUS;", o["to"]);
        Assert.Equal("true", o["replace"]);
        Assert.Equal("true", o["dry-run"]);
        Assert.Equal("true", o["include-system"]);
        Assert.Equal("true", o["skip-mismatched"]);
        Assert.Equal("5000", o["batch-size"]);
        Assert.Equal("a,b", o["table"]);
    }

    [Fact]
    public void ItRefusesOptionsThatBelongToOtherCommands()
    {
        Assert.Contains("select", Refused("--to", "x", "--select", "id"));
        Assert.Contains("merge-extensions", Refused("--to", "x", "--merge-extensions"));
        // and the read commands do not accept the restore options
        Assert.Contains("replace", Assert.Throws<ArgumentException>(
            () => Program.ParseOpts(new[] { "--table", "probe", "--replace" }, "read")).Message);
    }

    [Fact]
    public void ItRefusesAMisspeltSwitchRatherThanIgnoringIt()
    {
        var e = Refused("--to", "x", "--replce");
        Assert.Contains("replce", e);
        Assert.Contains("--replace", e);       // the accepted spelling is named
        Assert.Contains("--dry-run", e);
    }

    [Fact]
    public void ConnectionStringIsRequired()
    {
        var e = Assert.Throws<ArgumentException>(() => RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--replace" }, "restore"), out _));
        Assert.Contains("--to", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAreReadOffTheCommandLine()
    {
        var opts = RestoreCommand.OptionsFrom(Program.ParseOpts(new[]
        {
            "--to", "Server=tcp:localhost,1433;Database=CRONUS;", "--replace", "--dry-run",
            "--include-system", "--skip-mismatched", "--batch-size", "250", "--table", " probe_dense , probe_notnull ",
        }, "restore"), out var connection);

        Assert.Equal("Server=tcp:localhost,1433;Database=CRONUS;", connection);
        Assert.True(opts.Replace);
        Assert.True(opts.DryRun);
        Assert.True(opts.IncludeSystem);
        Assert.True(opts.SkipMismatchedTables);
        Assert.Equal(250, opts.BatchSize);
        Assert.Equal(new[] { "probe_dense", "probe_notnull" }, opts.OnlyTables);
    }

    [Fact]
    public void TheDefaultsAreTheSafeOnes()
    {
        var opts = RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "Server=localhost;" }, "restore"), out _);
        Assert.False(opts.Replace);          // a non-empty target is refused, never appended to
        Assert.False(opts.DryRun);
        Assert.False(opts.IncludeSystem);    // $ndo$... tables belong to the container
        Assert.False(opts.SkipMismatchedTables);  // a schema mismatch stops the restore before it writes
        Assert.Empty(opts.OnlyTables);
        Assert.True(opts.BatchSize > 0);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void ABatchSizeThatIsNotARowCountIsRefused(string value)
    {
        var e = Assert.Throws<ArgumentException>(() => RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "x", "--batch-size", value }, "restore"), out _));
        Assert.Contains("batch-size", e.Message, StringComparison.Ordinal);
        Assert.Contains(value, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageMentionsRestore()
    {
        // The command has to be discoverable, and the connection string is the one thing
        // a first-time caller gets wrong (a container's certificate is self-signed).
        var sw = new StringWriter();
        var stderr = Console.Error;
        Console.SetError(sw);
        try { Program.Main(Array.Empty<string>()); } finally { Console.SetError(stderr); }
        var usage = sw.ToString();
        Assert.Contains("bcdb restore", usage, StringComparison.Ordinal);
        Assert.Contains("TrustServerCertificate", usage, StringComparison.Ordinal);
    }
}
