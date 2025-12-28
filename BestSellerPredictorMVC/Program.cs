using BestSellerPredictorMVC.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using OfficeOpenXml;
using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// EPPlus license
ExcelPackage.License.SetNonCommercialPersonal("Moises");

// Persist DataProtection keys to durable location (App Service HOME = D:\home)
var keysPath = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Directory.GetCurrentDirectory(), "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("BestSellerPredictorMVC");

// Determine environment-specific cookie policies
var isDev = builder.Environment.IsDevelopment();
var cookieSecurePolicy = isDev ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
var sameSiteMode = isDev ? SameSiteMode.Lax : SameSiteMode.None;

// Force cookie-based TempData provider and configure its cookie
builder.Services.Configure<CookieTempDataProviderOptions>(options =>
{
    options.Cookie.Name = ".AspNetCore.Mvc.CookieTempDataProvider";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
    options.Cookie.SameSite = sameSiteMode;
});
builder.Services.AddSingleton<ITempDataProvider, CookieTempDataProvider>();

// Add MVC
builder.Services.AddControllersWithViews();

// Configure cookie policy (helps SameSite handling behind proxies)
builder.Services.Configure<CookiePolicyOptions>(options =>
{
    options.MinimumSameSitePolicy = SameSiteMode.Unspecified;
    options.HttpOnly = HttpOnlyPolicy.Always;
    options.Secure = cookieSecurePolicy;
});

// Increase multipart body limit for uploads (adjust the value as needed)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 200 * 1024 * 1024; // 200 MB for example
});

// Session (in-memory ok for single instance; use Redis for scale-out)
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(1);
    options.Cookie.Name = ".AspNetCore.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = sameSiteMode;
    options.Cookie.SecurePolicy = cookieSecurePolicy;
});

// Configure forwarded headers so the app sees the original scheme behind proxies (App Service, Front Door, etc.)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // If you know the proxy IPs, add them to KnownProxies / KnownNetworks for extra security.
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Process forwarded headers BEFORE cookie policy and session
app.UseForwardedHeaders();

app.UseCookiePolicy();

app.UseSession();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();