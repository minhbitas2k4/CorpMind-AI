using CorpMindAI.Api;
using CorpMindAI.Infrastructure.Extentions;
using CorpMindAI.Infrastructure.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
