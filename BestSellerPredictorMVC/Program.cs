using BestSellerPredictorMVC.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore; // Ensure this namespace is included for AddDbContext extension method  
using Microsoft.EntityFrameworkCore.SqlServer;
using Microsoft.Extensions.Configuration; // Add this namespace for ConfigurationManager  
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OfficeOpenXml; // Add namespace for EPPlus
using System;
using System.Collections.Specialized;
using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Set EPPlus license context for non-commercial use
ExcelPackage.License.SetNonCommercialPersonal("Moises");


// Add services to the container.  
builder.Services.AddControllersWithViews(); // For MVC and Razor Pages  

// Use a variable for your connection string for reuse
string connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// Register your data service
builder.Services.AddSingleton<SqlServerDataService>(sp =>
    new SqlServerDataService(connectionString)
);

// Test the connection at startup
try
{
    using var testConnection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
    testConnection.Open();
    Console.WriteLine("Database connection successful.");
    testConnection.Close();
}
catch (Exception ex)
{
    Console.WriteLine($"Database connection failed: {ex.Message}");
    // Optionally, stop the app if the connection is critical
    // throw;
}

var app = builder.Build();

// Configure the HTTP request pipeline.  
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();