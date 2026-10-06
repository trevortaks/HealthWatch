using System.Text;
using HealthWatch.Data;
using Microsoft.EntityFrameworkCore;

namespace HealthWatch.Reports;

public record TargetReport(int TargetId, string Name, TargetKind Kind, int Checks, int Failures,
    double UptimePercent, double AvgLatencyMs, int P95LatencyMs, int Incidents, TimeSpan Downtime);

public record IncidentRow(string Target, DateTime StartedAt, DateTime? EndedAt, TimeSpan Duration, string? Error);

public static class ReportService
{
    public static async Task<(List<TargetReport> Targets, List<IncidentRow> Incidents)> BuildAsync(
        AppDbContext db, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var targets = await db.Targets.OrderBy(t => t.Name).ToListAsync(ct);
        var rows = await db.Results.Where(r => r.At >= fromUtc && r.At <= toUtc)
            .Select(r => new { r.TargetId, r.Success, r.LatencyMs }).ToListAsync(ct);
        var incidents = await db.Incidents
            .Where(i => i.StartedAt <= toUtc && (i.EndedAt == null || i.EndedAt >= fromUtc))
            .OrderByDescending(i => i.StartedAt).ToListAsync(ct);
        var now = DateTime.UtcNow;
        TimeSpan Overlap(Incident i)
        {
            var s = i.StartedAt < fromUtc ? fromUtc : i.StartedAt;
            var e = Min(i.EndedAt ?? now, toUtc);
            return e > s ? e - s : TimeSpan.Zero;
        }

        var reports = targets.Select(t =>
        {
            var mine = rows.Where(r => r.TargetId == t.Id).ToList();
            var inc = incidents.Where(i => i.TargetId == t.Id).ToList();
            var (up, avg, p95) = Summarize(mine.Select(r => (r.Success, r.LatencyMs)));
            return new TargetReport(t.Id, t.Name, t.Kind, mine.Count, mine.Count(r => !r.Success),
                up, avg, p95, inc.Count, TimeSpan.FromTicks(inc.Sum(i => Overlap(i).Ticks)));
        }).ToList();

        var names = targets.ToDictionary(t => t.Id, t => t.Name);
        var incRows = incidents.Select(i => new IncidentRow(names.GetValueOrDefault(i.TargetId, "?"),
            i.StartedAt, i.EndedAt, Overlap(i), i.LastError)).ToList();
        return (reports, incRows);
    }

    static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    /// <summary>Uptime % (100 when no data), mean latency and p95 latency of successful checks.</summary>
    public static (double Uptime, double Avg, int P95) Summarize(IEnumerable<(bool Success, int LatencyMs)> checks)
    {
        var list = checks.ToList();
        if (list.Count == 0) return (100, 0, 0);
        var uptime = 100.0 * list.Count(c => c.Success) / list.Count;
        var lat = list.Where(c => c.Success).Select(c => c.LatencyMs).Order().ToList();
        if (lat.Count == 0) return (uptime, 0, 0);
        return (uptime, lat.Average(), lat[(int)Math.Ceiling(lat.Count * 0.95) - 1]);
    }

    public static string ToCsv(IEnumerable<TargetReport> reports)
    {
        var sb = new StringBuilder("Name,Type,Checks,Failures,Uptime %,Avg latency ms,P95 latency ms,Incidents,Downtime\n");
        foreach (var r in reports)
            sb.AppendLine(string.Join(',', Csv(r.Name), r.Kind, r.Checks, r.Failures, r.UptimePercent.ToString("F2"),
                r.AvgLatencyMs.ToString("F0"), r.P95LatencyMs, r.Incidents, r.Downtime.ToString(@"d\.hh\:mm\:ss")));
        return sb.ToString();
    }

    static string Csv(string v) => v.Contains(',') || v.Contains('"') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    public static string ToText(IEnumerable<TargetReport> reports, string title)
    {
        var sb = new StringBuilder(title + "\n\n");
        foreach (var r in reports)
            sb.AppendLine($"{r.Name,-30} {r.UptimePercent,7:F2}%  checks {r.Checks,6}  failures {r.Failures,5}  incidents {r.Incidents,3}  avg {r.AvgLatencyMs:F0}ms");
        return sb.ToString();
    }
}
