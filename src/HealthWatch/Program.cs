using HealthWatch.Alerts;
using HealthWatch.Data;
using HealthWatch.Scheduling;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "HealthWatch");
builder.Host.UseSystemd();

var dataDir = builder.Configuration["DataDir"] ?? Path.Combine(AppContext.BaseDirectory, "data");
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "healthwatch.db")}"));
var dp = builder.Services.AddDataProtection().SetApplicationName("HealthWatch")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddHttpClient("monitor").ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("insecure").ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<GikkoClient>();
builder.Services.AddSingleton<SmsPopClient>();
builder.Services.AddSingleton<INotifier, Notifier>();
builder.Services.AddSingleton<AlertService>();
builder.Services.AddSingleton<CheckRunner>();
builder.Services.AddHostedService<MonitorWorker>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o => { o.LoginPath = "/Login"; o.ExpireTimeSpan = TimeSpan.FromHours(12); o.SlidingExpiration = true; });
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await db.UpgradeSchemaAsync();
    await db.GetSettingsAsync();
}
if (string.IsNullOrEmpty(app.Configuration["Admin:Password"]))
    app.Logger.LogWarning("Admin:Password is not set - using the default password 'changeme'. Change it in appsettings.json.");

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.Run();

public partial class Program;
