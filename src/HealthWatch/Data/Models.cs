namespace HealthWatch.Data;

public enum TargetKind { Http, Database }
public enum DbProvider { SqlServer, PostgreSql, MySql, Sqlite }

public class Target
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public TargetKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 15;
    /// <summary>Consecutive failures before an alert is sent.</summary>
    public int FailuresBeforeAlert { get; set; } = 2;

    // HTTP
    public string? Url { get; set; }
    /// <summary>Accepted status codes, e.g. "200-399,401".</summary>
    public string ExpectedStatus { get; set; } = "200-399";
    public bool IgnoreTlsErrors { get; set; }

    // Database
    public DbProvider Provider { get; set; }
    public string? ConnectionStringEnc { get; set; }
    public string? Query { get; set; }

    // Live state (denormalised for the dashboard)
    public DateTime? LastCheckAt { get; set; }
    public bool? LastSuccess { get; set; }
    public string? LastMessage { get; set; }
    public int? LastLatencyMs { get; set; }
    public int ConsecutiveFailures { get; set; }
}

public class CheckResult
{
    public long Id { get; set; }
    public int TargetId { get; set; }
    public DateTime At { get; set; }
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public int LatencyMs { get; set; }
    public string? Error { get; set; }
}

public class Incident
{
    public int Id { get; set; }
    public int TargetId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string? LastError { get; set; }
}

public class Contact
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool UseEmail { get; set; } = true;
    public bool UseSms { get; set; }
    public bool UseWhatsApp { get; set; }
    public bool Enabled { get; set; } = true;
}

/// <summary>Single-row table (Id = 1).</summary>
public class Settings
{
    public int Id { get; set; } = 1;

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string? SmtpUser { get; set; }
    public string? SmtpPasswordEnc { get; set; }
    public string? SmtpFrom { get; set; }

    // Gikko: fully configurable until the API is pinned down. {to} and {message} are JSON-escaped.
    public string? GikkoSmsUrl { get; set; }
    public string? GikkoWhatsAppUrl { get; set; }
    public string GikkoAuthHeader { get; set; } = "Authorization";
    public string? GikkoAuthValueEnc { get; set; }
    public string GikkoSmsBody { get; set; } = "{\"to\":\"{to}\",\"message\":\"{message}\"}";
    public string GikkoWhatsAppBody { get; set; } = "{\"to\":\"{to}\",\"message\":\"{message}\"}";

    public int RetentionDays { get; set; } = 90;
    /// <summary>Local hour (0-23) to email a 24h summary; -1 disables.</summary>
    public int DailyReportHour { get; set; } = -1;
    public DateTime? LastDailyReportAt { get; set; }
}
