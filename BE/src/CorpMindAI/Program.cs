using CorpMindAI.Api;
using CorpMindAI.Application.Interfaces;
using CorpMindAI.Infrastructure.Authorization;
using CorpMindAI.Infrastructure.Extentions;
using CorpMindAI.Infrastructure.Services;
using Hangfire;
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment() && OperatingSystem.IsWindows())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
    builder.Logging.AddConsole();
    builder.Logging.AddDebug();
}

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Cấu hình giới hạn kích thước multipart/form-data (200MB tổng request)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 209_715_200;
    options.ValueLengthLimit = int.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
});

// Đăng ký toàn bộ Dependency Injection đồng bộ của Application và Infrastructure
builder.Services.AddAppDI(builder.Configuration);

// Cấu hình Authentication & Custom Authorization Policies (Sử dụng Scoped Handler và Validation Parameters bảo mật)
builder.Services.AddJwtAuthentication(builder.Configuration);

// Cấu hình Hangfire — bao gồm storage PostgreSQL và background server
builder.Services.AddConfigureHangfire(builder.Configuration)
                .AddHangfireServerWithConfig(builder.Configuration);

// Cấu hình Swagger kèm JWT authorize
builder.Services.AddSwaggerGen(c =>
{
    var jwtSecurityScheme = new OpenApiSecurityScheme
    {
        Name = "JWT Authentication",
        Description = "JWT Authentication for CorpMindAI Management",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT"
    };
    c.AddSecurityDefinition("Bearer", jwtSecurityScheme);
    var securityRequirement = new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    };
    c.AddSecurityRequirement(securityRequirement);
});

var app = builder.Build();

var recurringJobManager = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobManager.AddOrUpdate<IChunkingOutboxDispatcher>(
    "chunking-outbox-dispatcher",
    dispatcher => dispatcher.DispatchPendingAsync(),
    Cron.Minutely);
recurringJobManager.AddOrUpdate<IEmbeddingIndexOutboxDispatcher>(
    "embedding-index-outbox-dispatcher",
    dispatcher => dispatcher.DispatchPendingAsync(),
    Cron.Minutely);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();

// Chỉ user có role "knowledge_manager" mới truy cập được dashboard.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    // Sử dụng HangfireSimpleAuthFilter — kiểm tra IsAuthenticated + role "knowledge_manager"
    Authorization = new[]
    {
        new HangfireSimpleAuthFilter()
    }
});

app.UseAuthorization();

app.MapControllers();

app.Run();
