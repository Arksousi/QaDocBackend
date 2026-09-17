using Npgsql;

namespace QaDocBackend.Data;

/// <summary>Applies the embedded, idempotent QaDocDb.sql when the API starts (retrying while the database boots).</summary>
public static class SchemaInitializer
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(SchemaInitializer));
        var db = services.GetRequiredService<ISqlConnectionFactory>();

        await using var stream = typeof(SchemaInitializer).Assembly.GetManifestResourceStream("QaDocDb.sql")
            ?? throw new InvalidOperationException("Embedded resource QaDocDb.sql is missing.");
        string script = await new StreamReader(stream).ReadToEndAsync(ct);

        const int attempts = 30;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenAsync();
                await using var cmd = new NpgsqlCommand(script, conn);
                await cmd.ExecuteNonQueryAsync(ct);
                logger.LogInformation("QaDoc database schema is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < attempts && ex is NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Database not reachable yet (attempt {Attempt}/{Attempts}): {Message}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }
}
