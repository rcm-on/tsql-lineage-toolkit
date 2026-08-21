using System;

namespace TSqlParser;

/// <summary>
/// Single point of construction for T-SQL connection strings. Defaults to integrated
/// security (unchanged behavior); optional SQL auth credentials come from
/// TSQL_SQL_USER / TSQL_SQL_PASSWORD environment variables, read only via
/// <see cref="FromEnvironment"/> so tests can exercise <see cref="Build"/> without
/// touching the process environment. Enables running against a container that only
/// accepts SQL auth (e.g. mcr.microsoft.com/mssql/server in CI).
/// </summary>
internal static class SqlConnections
{
    public static string Build(string server, string database, int timeoutSeconds = 10, SqlCredentials? creds = null)
    {
        if (creds is { } c)
            return $"Server={server};Database={database};User ID={Quote(c.User)};Password={Quote(c.Password)};TrustServerCertificate=true;Connection Timeout={timeoutSeconds};";

        return $"Server={server};Database={database};Integrated Security=true;TrustServerCertificate=true;Connection Timeout={timeoutSeconds};";
    }

    /// <summary>
    /// Standard ADO.NET connection-string quoting: a value containing ';', '"', '\'',
    /// '=', or leading/trailing whitespace must be wrapped in double quotes, with any
    /// interior double quotes doubled. Guards against a SQL-auth password (e.g. a
    /// randomly generated CI SA password) corrupting or truncating the connection
    /// string.
    /// </summary>
    private static string Quote(string value)
    {
        var needsQuoting = value.Length == 0
            || value.IndexOfAny(['\'', '"', ';', '=']) >= 0
            || value[0] == ' ' || value[^1] == ' ';

        return needsQuoting ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>
    /// Mensaje del último fallo de conexión. Los gates LiveSql solo ven un código de
    /// retorno; sin esto el fallo en CI es "¿SQL Server no disponible?" sin causa.
    /// </summary>
    public static string? LastError { get; private set; }

    public static void RecordFailure(Exception ex) => LastError = ex.Message;

    public static SqlCredentials? FromEnvironment() =>
        FromEnvironment(Environment.GetEnvironmentVariable("TSQL_SQL_USER"), Environment.GetEnvironmentVariable("TSQL_SQL_PASSWORD"));

    /// <summary>
    /// Testable core of <see cref="FromEnvironment()"/>: takes the two raw values as
    /// parameters so tests can exercise the "only one defined" fail-closed path without
    /// mutating the process environment.
    /// </summary>
    internal static SqlCredentials? FromEnvironment(string? user, string? password) =>
        string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password)
            ? null
            : new SqlCredentials(user, password);

    public readonly record struct SqlCredentials(string User, string Password);
}
