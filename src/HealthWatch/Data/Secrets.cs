using Microsoft.AspNetCore.DataProtection;

namespace HealthWatch.Data;

/// <summary>Encrypts connection strings / passwords at rest (key ring lives in the data dir).</summary>
public class Secrets(IDataProtectionProvider provider)
{
    readonly IDataProtector _p = provider.CreateProtector("HealthWatch.Secrets.v1");
    public string? Protect(string? plain) => string.IsNullOrEmpty(plain) ? null : _p.Protect(plain);
    public string? Unprotect(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return null;
        try { return _p.Unprotect(enc); } catch { return null; }
    }
}
