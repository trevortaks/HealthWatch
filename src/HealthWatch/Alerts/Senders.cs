using System.Text.Json;
using HealthWatch.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace HealthWatch.Alerts;

public class EmailSender(Secrets secrets)
{
    public async Task SendAsync(Settings s, IEnumerable<string> to, string subject, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.SmtpHost)) throw new InvalidOperationException("SMTP host not configured");
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(s.SmtpFrom ?? s.SmtpUser ?? "healthwatch@localhost"));
        foreach (var a in to) msg.To.Add(MailboxAddress.Parse(a));
        msg.Subject = subject;
        msg.Body = new TextPart("plain") { Text = body };
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(s.SmtpHost, s.SmtpPort,
            s.SmtpUseSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None, ct);
        if (!string.IsNullOrEmpty(s.SmtpUser))
            await smtp.AuthenticateAsync(s.SmtpUser, secrets.Unprotect(s.SmtpPasswordEnc) ?? "", ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}

public enum GikkoChannel { Sms, WhatsApp }

/// <summary>Posts to the Gikko API using the configured URL, auth header and JSON body template.</summary>
public class GikkoClient(IHttpClientFactory http, Secrets secrets)
{
    public async Task SendAsync(Settings s, GikkoChannel ch, string to, string message, CancellationToken ct)
    {
        var url = ch == GikkoChannel.Sms ? s.GikkoSmsUrl : s.GikkoWhatsAppUrl;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException($"Gikko {ch} URL not configured");
        var template = ch == GikkoChannel.Sms ? s.GikkoSmsBody : s.GikkoWhatsAppBody;
        var body = Render(template, to, message);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        var auth = secrets.Unprotect(s.GikkoAuthValueEnc);
        if (!string.IsNullOrEmpty(auth) && !string.IsNullOrWhiteSpace(s.GikkoAuthHeader))
            req.Headers.TryAddWithoutValidation(s.GikkoAuthHeader, auth);
        using var resp = await http.CreateClient("monitor").SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Gikko {ch} returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Replaces {to} / {message} with JSON-escaped values (no surrounding quotes).</summary>
    public static string Render(string template, string to, string message) =>
        template.Replace("{to}", JsonEncodedText.Encode(to).ToString())
                .Replace("{message}", JsonEncodedText.Encode(message).ToString());
}

/// <summary>Sends SMS through SMSPoP (https://smspop.co.zw/documentation) as one campaign per alert.</summary>
public class SmsPopClient(IHttpClientFactory http, Secrets secrets)
{
    public const string Url = "https://smspop.co.zw/api/campaigns";

    public static bool IsConfigured(Settings s) =>
        s.SmsPopTokenEnc != null && !string.IsNullOrWhiteSpace(s.SmsPopSenderId);

    public async Task SendAsync(Settings s, IEnumerable<string> phones, string message, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = "HealthWatch alert",
            message,
            sender_id = s.SmsPopSenderId!.Trim(),
            contact_import_method = "manual",
            manual_contacts = string.Join(",", phones),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secrets.Unprotect(s.SmsPopTokenEnc));
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var resp = await http.CreateClient("monitor").SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"SMSPoP returned {(int)resp.StatusCode}: {json}");
        if (CheckResponse(json) is { } error) throw new InvalidOperationException(error);
    }

    /// <summary>SMSPoP answers 200 even when some numbers fail; returns an error description or null if all were sent.</summary>
    public static string? CheckResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True)
            return $"SMSPoP rejected the request: {json}";
        if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("contacts", out var contacts)) return null;
        var bad = contacts.EnumerateArray()
            .Where(c => c.GetProperty("status").GetString() != "sent")
            .Select(c => $"{c.GetProperty("phone").GetString()} ({c.GetProperty("status").GetString()})")
            .ToList();
        var invalid = data.TryGetProperty("summary", out var sum) && sum.TryGetProperty("invalid", out var inv) ? inv.GetInt32() : 0;
        if (bad.Count == 0 && invalid == 0) return null;
        return $"SMSPoP did not send to: {string.Join(", ", bad)}{(invalid > 0 ? $" (+{invalid} invalid numbers)" : "")}";
    }
}

public class Notifier(IServiceScopeFactory sf, EmailSender email, GikkoClient gikko, SmsPopClient smsPop, ILogger<Notifier> log) : INotifier
{
    public async Task NotifyAsync(string shortText, string longText, CancellationToken ct = default, bool emailOnly = false)
    {
        using var scope = sf.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var s = await db.GetSettingsAsync(ct);
        var contacts = await db.Contacts.Where(c => c.Enabled).ToListAsync(ct);

        async Task Try(string what, Func<Task> send)
        {
            try { await send(); }
            catch (Exception ex) { log.LogError(ex, "Failed to send {What}", what); }
        }

        var emails = contacts.Where(c => c.UseEmail && !string.IsNullOrWhiteSpace(c.Email)).Select(c => c.Email!).ToList();
        if (emails.Count > 0) await Try("email", () => email.SendAsync(s, emails, shortText, longText, ct));
        if (emailOnly) return;
        var useSmsPop = SmsPopClient.IsConfigured(s);
        var smsPhones = contacts.Where(c => c.UseSms && !string.IsNullOrWhiteSpace(c.Phone)).Select(c => c.Phone!).ToList();
        if (useSmsPop && smsPhones.Count > 0) await Try("SMS via SMSPoP", () => smsPop.SendAsync(s, smsPhones, shortText, ct));
        foreach (var c in contacts.Where(c => !string.IsNullOrWhiteSpace(c.Phone)))
        {
            if (c.UseSms && !useSmsPop) await Try($"SMS to {c.Name}", () => gikko.SendAsync(s, GikkoChannel.Sms, c.Phone!, shortText, ct));
            if (c.UseWhatsApp) await Try($"WhatsApp to {c.Name}", () => gikko.SendAsync(s, GikkoChannel.WhatsApp, c.Phone!, shortText, ct));
        }
    }
}
