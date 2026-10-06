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

public class Notifier(IServiceScopeFactory sf, EmailSender email, GikkoClient gikko, ILogger<Notifier> log) : INotifier
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
        foreach (var c in contacts.Where(c => !string.IsNullOrWhiteSpace(c.Phone)))
        {
            if (c.UseSms) await Try($"SMS to {c.Name}", () => gikko.SendAsync(s, GikkoChannel.Sms, c.Phone!, shortText, ct));
            if (c.UseWhatsApp) await Try($"WhatsApp to {c.Name}", () => gikko.SendAsync(s, GikkoChannel.WhatsApp, c.Phone!, shortText, ct));
        }
    }
}
