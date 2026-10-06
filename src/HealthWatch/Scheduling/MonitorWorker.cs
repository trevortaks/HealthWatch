using HealthWatch.Alerts;
using HealthWatch.Checks;
using HealthWatch.Data;
using HealthWatch.Reports;
using Microsoft.EntityFrameworkCore;

namespace HealthWatch.Scheduling;

public class CheckRunner(IServiceScopeFactory sf, IHttpClientFactory http, Secrets secrets, AlertService alerts)
{
    public async Task RunAsync(int targetId, CancellationToken ct)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var t = await db.Targets.FindAsync([targetId], ct);
        if (t is null) return;
        var outcome = t.Kind == TargetKind.Http
            ? await HttpChecker.CheckAsync(t, http.CreateClient(t.IgnoreTlsErrors ? "insecure" : "monitor"), ct)
            : await DbChecker.CheckAsync(t, secrets.Unprotect(t.ConnectionStringEnc), ct);
        await alerts.ProcessAsync(db, t, outcome, ct);
    }
}

public class MonitorWorker(IServiceScopeFactory sf, CheckRunner runner, INotifier notifier, ILogger<MonitorWorker> log)
    : BackgroundService
{
    readonly Dictionary<int, DateTime> _lastRun = new();
    readonly SemaphoreSlim _gate = new(20);
    DateTime _lastMaintenance = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        log.LogInformation("Monitor started");
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Scheduler tick failed"); }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task TickAsync(CancellationToken ct)
    {
        List<(int Id, int Interval)> targets;
        using (var scope = sf.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            targets = (await db.Targets.Where(t => t.Enabled).Select(t => new { t.Id, t.IntervalSeconds }).ToListAsync(ct))
                .Select(t => (t.Id, t.IntervalSeconds)).ToList();
        }
        var now = DateTime.UtcNow;
        foreach (var (id, interval) in targets)
        {
            if (_lastRun.TryGetValue(id, out var last) && (now - last).TotalSeconds < Math.Max(5, interval)) continue;
            _lastRun[id] = now;
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync(ct);
                try { await runner.RunAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Check {Id} failed", id); }
                finally { _gate.Release(); }
            }, ct);
        }
        if (now - _lastMaintenance > TimeSpan.FromMinutes(15)) { _lastMaintenance = now; await MaintenanceAsync(ct); }
    }

    /// <summary>Purges old history and sends the optional daily summary.</summary>
    async Task MaintenanceAsync(CancellationToken ct)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.GetSettingsAsync(ct);
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, s.RetentionDays));
        await db.Results.Where(r => r.At < cutoff).ExecuteDeleteAsync(ct);
        await db.Incidents.Where(i => i.EndedAt != null && i.EndedAt < cutoff).ExecuteDeleteAsync(ct);

        var local = DateTime.Now;
        if (s.DailyReportHour is >= 0 and <= 23 && local.Hour >= s.DailyReportHour
            && (s.LastDailyReportAt is null || s.LastDailyReportAt.Value.ToLocalTime().Date < local.Date))
        {
            s.LastDailyReportAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            var (reports, _) = await ReportService.BuildAsync(db, DateTime.UtcNow.AddHours(-24), DateTime.UtcNow, ct);
            await notifier.NotifyAsync("[HealthWatch] Daily summary", ReportService.ToText(reports, "Last 24 hours"), ct, emailOnly: true);
        }
    }
}
