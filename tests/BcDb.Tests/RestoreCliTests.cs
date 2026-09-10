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

    static RestoreOptions Options(params string[] args)
        => RestoreCommand.OptionsFrom(Program.ParseOpts(args, "restore"), out _, out _);

    [Fact]
    public void ItAcceptsItsOwnOptions()
    {
        var o = Program.ParseOpts(new[]
        {
            "--to", "Server=localhost;Database=CRONUS;", "--replace", "--dry-run",
            "--include-system", "--create", "--strict", "--batch-size", "5000", "--table", "a,b", "--exclude-table", "c,d",
            "--rename-company", "Src=Dst", "--allow-column-loss", "--include-identity",
        }, "restore");
        Assert.Equal("Server=localhost;Database=CRONUS;", o["to"]);
        Assert.Equal("true", o["replace"]);
        Assert.Equal("true", o["dry-run"]);
        Assert.Equal("true", o["include-system"]);
        Assert.Equal("true", o["create"]);
        Assert.Equal("true", o["strict"]);
        Assert.Equal("5000", o["batch-size"]);
        Assert.Equal("a,b", o["table"]);
        Assert.Equal("c,d", o["exclude-table"]);
        Assert.Equal("Src=Dst", o["rename-company"]);
        Assert.Equal("true", o["allow-column-loss"]);
        Assert.Equal("true", o["include-identity"]);

        // and the "off" spelling of each on/off pair
        var off = Program.ParseOpts(new[]
        {
            "--to", "x", "--no-replace", "--no-create", "--no-allow-column-loss",
        }, "restore");
        Assert.Equal("true", off["no-replace"]);
        Assert.Equal("true", off["no-create"]);
        Assert.Equal("true", off["no-allow-column-loss"]);
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
            Program.ParseOpts(new[] { "--replace" }, "restore"), out _, out _));
        Assert.Contains("--to", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsAreReadOffTheCommandLine()
    {
        var opts = RestoreCommand.OptionsFrom(Program.ParseOpts(new[]
        {
            "--to", "Server=tcp:localhost,1433;Database=CRONUS;", "--replace", "--dry-run",
            "--include-system", "--create", "--strict", "--batch-size", "250", "--table", " probe_dense , probe_notnull ",
            "--exclude-table", " probe_row , probe_page ",
            "--rename-company", " Stefan Maron Consulting = CRONUS International Ltd. , A = B ",
            "--allow-column-loss", "--include-identity",
        }, "restore"), out var connection, out var needsReplaceConfirmation);

        Assert.Equal("Server=tcp:localhost,1433;Database=CRONUS;", connection);
        Assert.True(opts.Replace);
        Assert.False(needsReplaceConfirmation);   // --replace was named explicitly, nothing to ask about
        Assert.True(opts.DryRun);
        Assert.True(opts.IncludeSystem);
        Assert.True(opts.CreateMissing);
        Assert.True(opts.Strict);
        Assert.Equal(250, opts.BatchSize);
        Assert.Equal(new[] { "probe_dense", "probe_notnull" }, opts.OnlyTables);
        Assert.Equal(new[] { "probe_row", "probe_page" }, opts.ExcludeTables);
        // company names carry spaces, so only "=" and "," are delimiters, both trimmed
        Assert.Equal("CRONUS International Ltd.", opts.RenameCompany["Stefan Maron Consulting"]);
        Assert.Equal("B", opts.RenameCompany["A"]);
        Assert.True(opts.AllowColumnLoss);
        Assert.True(opts.IncludeIdentity);
    }

    [Fact]
    public void RenameCompanyRejectsAPairWithNoEqualsSign()
    {
        var e = Assert.Throws<ArgumentException>(() => RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "x", "--rename-company", "JustOneName" }, "restore"), out _, out _));
        Assert.Contains("rename-company", e.Message, StringComparison.Ordinal);
        Assert.Contains("JustOneName", e.Message, StringComparison.Ordinal);
        Assert.Contains("=", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultsAssumeAnAlreadyCorrectContainer()
    {
        // Restoring into a container whose extensions already built the target's schema
        // correctly is the common case this tool exists for, so the defaults are shaped for
        // it rather than for building a company from bare source schema.
        var opts = RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "Server=localhost;" }, "restore"), out _, out var needsReplaceConfirmation);
        Assert.True(needsReplaceConfirmation);   // replacing existing rows needs asking, never assumed silently
        Assert.False(opts.DryRun);
        Assert.False(opts.IncludeSystem);        // $ndo$... tables belong to the container
        Assert.False(opts.CreateMissing);        // building a table from bare source schema loses BC's own SumIndexField views etc.
        Assert.False(opts.Strict);               // one unreconcilable table does not stop the other 4,000
        Assert.Empty(opts.OnlyTables);
        Assert.Empty(opts.ExcludeTables);
        Assert.Empty(opts.RenameCompany);
        Assert.True(opts.AllowColumnLoss);       // only matters once CreateMissing is off, which is now the default
        Assert.False(opts.IncludeIdentity);      // the container's login/session/app-registry tables stay untouched by default
        Assert.True(opts.BatchSize > 0);
    }

    [Theory]
    [InlineData("replace", "no-replace")]
    [InlineData("create", "no-create")]
    [InlineData("allow-column-loss", "no-allow-column-loss")]
    public void OppositeSwitchesCannotBothBeGiven(string onFlag, string offFlag)
    {
        var e = Assert.Throws<ArgumentException>(() => RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "x", $"--{onFlag}", $"--{offFlag}" }, "restore"), out _, out _));
        Assert.Contains(onFlag, e.Message, StringComparison.Ordinal);
        Assert.Contains(offFlag, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoReplaceRefusesWithoutAsking()
    {
        var opts = RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "x", "--no-replace" }, "restore"), out _, out var needsReplaceConfirmation);
        Assert.False(opts.Replace);
        Assert.False(needsReplaceConfirmation);  // named explicitly — nothing to prompt about
    }

    [Fact]
    public void NoCreateIsStillAcceptedThoughItIsNowTheDefault()
    {
        // Redundant with the default, but a caller stating intent explicitly (a script that
        // wants to be self-documenting, say) is not an error.
        Assert.False(Options("--to", "x", "--no-create").CreateMissing);
    }

    [Fact]
    public void NoAllowColumnLossOptsIntoTheStricterRefusal()
    {
        Assert.False(Options("--to", "x", "--no-allow-column-loss").AllowColumnLoss);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void ABatchSizeThatIsNotARowCountIsRefused(string value)
    {
        var e = Assert.Throws<ArgumentException>(() => RestoreCommand.OptionsFrom(
            Program.ParseOpts(new[] { "--to", "x", "--batch-size", value }, "restore"), out _, out _));
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
