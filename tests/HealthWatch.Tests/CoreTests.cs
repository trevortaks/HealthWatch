using HealthWatch.Alerts;
using HealthWatch.Checks;
using HealthWatch.Data;
using HealthWatch.Reports;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HealthWatch.Tests;

public class CoreTests
{
    class FakeNotifier : INotifier
    {
        public List<string> Sent = [];
        public Task NotifyAsync(string s, string l, CancellationToken ct = default, bool emailOnly = false) { Sent.Add(s); return Task.CompletedTask; }
    }

    static (AppDbContext, SqliteConnection) NewDb()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (db, conn);
    }

    [Theory]
    [InlineData("200-399", 200, true)]
    [InlineData("200-399", 404, false)]
    [InlineData("200,401", 401, true)]
    [InlineData("200-299,401", 500, false)]
    [InlineData("", 301, true)]
    public void StatusCodeMatching(string spec, int code, bool expected) => Assert.Equal(expected, StatusMatcher.Matches(spec, code));

    [Fact]
    public async Task AlertsOncePerIncidentThenRecovers()
    {
        var (db, conn) = NewDb(); using var _ = conn;
        var n = new FakeNotifier(); var svc = new AlertService(n);
        var t = new Target { Name = "App", Kind = TargetKind.Http, Url = "http://x", FailuresBeforeAlert = 2 };
        db.Targets.Add(t); await db.SaveChangesAsync();
        var fail = new CheckOutcome(false, 500, 10, "boom");
        await svc.ProcessAsync(db, t, fail);
        Assert.Empty(n.Sent);                       // below threshold
        await svc.ProcessAsync(db, t, fail);
        Assert.Single(n.Sent);                      // threshold reached
        await svc.ProcessAsync(db, t, fail);
        Assert.Single(n.Sent);                      // no repeat
        await svc.ProcessAsync(db, t, new CheckOutcome(true, 200, 5, null));
        Assert.Equal(2, n.Sent.Count);
        Assert.Contains("RECOVERED", n.Sent[1]);
        Assert.NotNull((await db.Incidents.SingleAsync()).EndedAt);
        Assert.Equal(4, await db.Results.CountAsync());
    }

    [Fact]
    public async Task DbCheckerSucceedsAndFails()
    {
        var t = new Target { Provider = DbProvider.Sqlite, TimeoutSeconds = 5 };
        Assert.True((await DbChecker.CheckAsync(t, "Data Source=:memory:", default)).Success);
        Assert.False((await DbChecker.CheckAsync(t, "Data Source=/nonexistent/dir/x.db;Mode=ReadOnly", default)).Success);
        Assert.False((await DbChecker.CheckAsync(t, null, default)).Success);
    }

    [Fact]
    public async Task HttpCheckerReportsConnectionFailure()
    {
        var t = new Target { Url = "http://127.0.0.1:1/", TimeoutSeconds = 3 };
        var o = await HttpChecker.CheckAsync(t, new HttpClient(), default);
        Assert.False(o.Success);
        Assert.NotNull(o.Error);
    }

    [Fact]
    public void ReportSummary()
    {
        var (up, avg, p95) = ReportService.Summarize([(true, 10), (true, 20), (true, 30), (false, 0)]);
        Assert.Equal(75, up);
        Assert.Equal(20, avg);
        Assert.Equal(30, p95);
        Assert.Equal(100, ReportService.Summarize([]).Uptime);
    }

    [Fact]
    public void GikkoBodyIsValidJsonWithOriginalValues()
    {
        var body = GikkoClient.Render("{\"to\":\"{to}\",\"message\":\"{message}\"}", "26377", "a \"b\"\nc");
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("26377", doc.RootElement.GetProperty("to").GetString());
        Assert.Equal("a \"b\"\nc", doc.RootElement.GetProperty("message").GetString());
    }
}
