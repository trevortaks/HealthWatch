using HealthWatch.Checks;
using HealthWatch.Data;
using Microsoft.EntityFrameworkCore;

namespace HealthWatch.Alerts;

public interface INotifier
{
    /// <param name="shortText">Used as the email subject and the SMS/WhatsApp body.</param>
    Task NotifyAsync(string shortText, string longText, CancellationToken ct = default, bool emailOnly = false);
}

/// <summary>Records each outcome and runs the up/down state machine: one DOWN alert per incident, one RECOVERED alert.</summary>
public class AlertService(INotifier notifier)
{
    public async Task ProcessAsync(AppDbContext db, Target t, CheckOutcome o, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        db.Results.Add(new CheckResult
        {
            TargetId = t.Id, At = now, Success = o.Success, StatusCode = o.StatusCode,
            LatencyMs = o.LatencyMs, Error = o.Error
        });
        t.LastCheckAt = now;
        t.LastSuccess = o.Success;
        t.LastLatencyMs = o.LatencyMs;
        t.LastMessage = o.Success ? (o.StatusCode is { } c ? $"HTTP {c}" : "OK") : o.Error;

        var open = await db.Incidents.FirstOrDefaultAsync(i => i.TargetId == t.Id && i.EndedAt == null, ct);
        (string, string)? message = null;

        if (o.Success)
        {
            t.ConsecutiveFailures = 0;
            if (open is not null)
            {
                open.EndedAt = now;
                var dur = FormatDuration(now - open.StartedAt);
                message = ($"[HealthWatch] RECOVERED: {t.Name}",
                    $"{t.Name} is back up after {dur}.\nTime: {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
        }
        else
        {
            t.ConsecutiveFailures++;
            if (open is not null) open.LastError = o.Error;
            else if (t.ConsecutiveFailures >= Math.Max(1, t.FailuresBeforeAlert))
            {
                db.Incidents.Add(new Incident { TargetId = t.Id, StartedAt = now, LastError = o.Error });
                message = ($"[HealthWatch] DOWN: {t.Name}",
                    $"{t.Name} ({(t.Kind == TargetKind.Http ? t.Url : t.Provider.ToString())}) failed {t.ConsecutiveFailures} check(s) in a row.\n" +
                    $"Error: {o.Error}\nTime: {now.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            }
        }

        await db.SaveChangesAsync(ct); // persist state first so a notifier failure never loses it
        if (message is var (s, l)) await notifier.NotifyAsync(s, l, ct);
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" :
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes}m {d.Seconds}s" : $"{(int)d.TotalSeconds}s";
}
