using System.Data.Common;
using System.Diagnostics;
using HealthWatch.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace HealthWatch.Checks;

public record CheckOutcome(bool Success, int? StatusCode, int LatencyMs, string? Error);

public static class StatusMatcher
{
    /// <summary>Spec like "200-399,401". Invalid parts are ignored; empty spec means 200-399.</summary>
    public static bool Matches(string? spec, int code)
    {
        if (string.IsNullOrWhiteSpace(spec)) spec = "200-399";
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var r = part.Split('-', 2);
            if (int.TryParse(r[0], out var lo) && (r.Length == 1 ? lo == code
                : int.TryParse(r[1], out var hi) && code >= lo && code <= hi)) return true;
        }
        return false;
    }
}

public static class HttpChecker
{
    public static async Task<CheckOutcome> CheckAsync(Target t, HttpClient client, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, t.TimeoutSeconds)));
        try
        {
            using var resp = await client.GetAsync(t.Url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)resp.StatusCode;
            var ok = StatusMatcher.Matches(t.ExpectedStatus, code);
            return new(ok, code, (int)sw.ElapsedMilliseconds, ok ? null : $"Unexpected status {code} {resp.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, (int)sw.ElapsedMilliseconds, $"Timed out after {t.TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, null, (int)sw.ElapsedMilliseconds, ex.InnerException?.Message ?? ex.Message);
        }
    }
}

public static class DbChecker
{
    public static DbConnection CreateConnection(DbProvider p, string cs) => p switch
    {
        DbProvider.SqlServer => new SqlConnection(cs),
        DbProvider.PostgreSql => new NpgsqlConnection(cs),
        DbProvider.MySql => new MySqlConnection(cs),
        DbProvider.Sqlite => new SqliteConnection(cs),
        _ => throw new NotSupportedException(p.ToString())
    };

    public static async Task<CheckOutcome> CheckAsync(Target t, string? connectionString, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(connectionString))
            return new(false, null, 0, "No connection string configured");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, t.TimeoutSeconds)));
        try
        {
            await using var conn = CreateConnection(t.Provider, connectionString);
            await conn.OpenAsync(cts.Token);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = string.IsNullOrWhiteSpace(t.Query) ? "SELECT 1" : t.Query;
            cmd.CommandTimeout = Math.Max(1, t.TimeoutSeconds);
            await cmd.ExecuteScalarAsync(cts.Token);
            return new(true, null, (int)sw.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, null, (int)sw.ElapsedMilliseconds, $"Timed out after {t.TimeoutSeconds}s");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, null, (int)sw.ElapsedMilliseconds, ex.Message);
        }
    }
}
