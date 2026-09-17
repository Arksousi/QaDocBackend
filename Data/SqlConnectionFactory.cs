using Npgsql;

namespace QaDocBackend.Data;

public interface ISqlConnectionFactory
{
    Task<NpgsqlConnection> OpenAsync();
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public SqlConnectionFactory(IConfiguration configuration)
    {
        _dataSource = NpgsqlDataSource.Create(ConnectionStrings.Resolve(configuration));
    }

    public async Task<NpgsqlConnection> OpenAsync() => await _dataSource.OpenConnectionAsync();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

public static class ConnectionStrings
{
    /// <summary>
    /// Uses ConnectionStrings:DefaultConnection, or DATABASE_URL (postgresql://user:pass@host:port/db),
    /// which is what Railway and most hosts provide.
    /// </summary>
    public static string Resolve(IConfiguration configuration)
    {
        var url = configuration["DATABASE_URL"];
        if (!string.IsNullOrWhiteSpace(url)) return FromUrl(url);

        return configuration.GetConnectionString("DefaultConnection") is { Length: > 0 } cs
            ? cs
            : throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection or DATABASE_URL.");
    }

    private static string FromUrl(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null
        }.ConnectionString;
    }
}
