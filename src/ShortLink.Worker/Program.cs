using ShortLink.Core;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddShortLinkPersistence(builder.Configuration);
builder.Services.AddScoped<ClickIngestionService>();
builder.Services.AddScoped<ClickAggregationService>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ClickEventConsumer>();

var host = builder.Build();
host.Run();
