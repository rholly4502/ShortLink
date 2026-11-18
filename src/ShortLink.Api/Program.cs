using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using ShortLink.Core;
using ShortLink.Core.Abstractions;
using ShortLink.Infrastructure.Caching;
using ShortLink.Infrastructure.Messaging;
using ShortLink.Api.Middleware;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();
builder.Services.AddControllers();
builder.Services.AddShortLinkPersistence(builder.Configuration);

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.Zero
        };
    });
builder.Services.AddAuthorization();

// Clock + device detection (pure)
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<DeviceTypeDetector>();

// Redis slug cache + resolver
var redisCs = builder.Configuration["Redis:ConnectionString"] ?? "localhost:6379";
builder.Services.AddSingleton<IRedisStringStore>(_ => new RedisStringStore(redisCs));
builder.Services.AddSingleton<ISlugCache>(sp =>
    new RedisSlugCache(sp.GetRequiredService<IRedisStringStore>(), TimeSpan.FromHours(12)));
builder.Services.AddScoped<CachedSlugResolver>(sp =>
    new CachedSlugResolver(
        sp.GetRequiredService<ISlugCache>(),
        sp.GetRequiredService<IShortLinkRepository>(),
        sp.GetRequiredService<IClock>(),
        TimeSpan.FromHours(12)));

// Rate limit (Redis fixed-window)
var rateLimit = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
builder.Services.AddSingleton(rateLimit);
builder.Services.AddSingleton<FixedWindowRateLimiter>();

// RabbitMQ click publisher
var amqp = builder.Configuration["RabbitMQ:ConnectionString"];
builder.Services.AddSingleton<IMessagePublisher>(_ =>
    RabbitMqMessagePublisher.CreateAsync(amqp!).GetAwaiter().GetResult());
builder.Services.AddSingleton<IClickEventPublisher, RabbitMqClickEventPublisher>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();

    using var scope = app.Services.CreateScope();
    var initializer = scope.ServiceProvider.GetRequiredService<IDbInitializer>();
    await initializer.InitializeAsync(migrateEnabled: true);
}

app.UseHttpsRedirection();
app.UseMiddleware<RedisRateLimitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
if (!app.Environment.IsDevelopment())
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

// Entry point yang dibuka utk WebApplicationFactory (integration tests).
public partial class Program { }
