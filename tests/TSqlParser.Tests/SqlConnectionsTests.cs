using TSqlParser;

namespace TSqlParser.Tests;

/// <summary>
/// Gate for the single connection-string builder (SqlConnections.cs). The two
/// "no creds" tests pin the exact literal that every call site produced before this
/// refactor - default behavior must not change when TSQL_SQL_USER/PASSWORD are unset.
/// </summary>
public class SqlConnectionsTests
{
    [Fact]
    public void Build_NoCreds_ReturnsExactLegacyString_DefaultTimeout()
    {
        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019");

        Assert.Equal(
            "Server=localhost\\SQLEXPRESS;Database=AdventureWorks2019;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=10;",
            connStr);
    }

    [Fact]
    public void Build_NoCreds_ReturnsExactLegacyString_Timeout15()
    {
        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019", 15);

        Assert.Equal(
            "Server=localhost\\SQLEXPRESS;Database=AdventureWorks2019;Integrated Security=true;TrustServerCertificate=true;Connection Timeout=15;",
            connStr);
    }

    [Fact]
    public void Build_WithCreds_UsesSqlAuth_NotIntegratedSecurity()
    {
        var creds = new SqlConnections.SqlCredentials("sa", "P@ssw0rd!");

        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019", 10, creds);

        Assert.Contains("User ID=sa", connStr);
        Assert.Contains("Password=P@ssw0rd!", connStr);
        Assert.DoesNotContain("Integrated Security", connStr);
    }

    [Fact]
    public void FromEnvironment_OnlyUserDefined_ReturnsNull_FailsClosedToIntegratedAuth()
    {
        var creds = SqlConnections.FromEnvironment("sa", null);

        Assert.Null(creds);
    }

    [Fact]
    public void Build_PasswordWithSemicolon_IsWrappedInDoubleQuotes()
    {
        var creds = new SqlConnections.SqlCredentials("sa", "P@ss;word");

        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019", 10, creds);

        Assert.Contains("Password=\"P@ss;word\";", connStr);
    }

    [Fact]
    public void Build_PasswordWithInteriorDoubleQuote_DoublesTheQuote()
    {
        var creds = new SqlConnections.SqlCredentials("sa", "P@ss\"word");

        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019", 10, creds);

        Assert.Contains("Password=\"P@ss\"\"word\";", connStr);
    }

    [Fact]
    public void Build_PasswordWithoutSpecialChars_IsNotQuoted()
    {
        var creds = new SqlConnections.SqlCredentials("sa", "P@ssw0rd!");

        var connStr = SqlConnections.Build("localhost\\SQLEXPRESS", "AdventureWorks2019", 10, creds);

        Assert.Contains("Password=P@ssw0rd!;", connStr);
    }
}
