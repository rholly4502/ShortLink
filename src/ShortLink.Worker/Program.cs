using ShortLink.Core;
using ShortLink.Infrastructure.Persistence;
using ShortLink.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddShortLinkPersistence(builder.Configuration);
builder.Services.Configure<ClickConsumerOptions>(
    builder.Configuration.GetSection(ClickConsumerOptions.SectionName));
builder.Services.AddScoped<ClickIngestionService>();
builder.Services.AddScoped<ClickAggregationService>();
builder.Services.AddSingleton<IClickEventHandler, ScopedClickEventHandler>();
builder.Services.AddSingleton<ClickEventMessageProcessor>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ClickEventConsumer>();

var host = builder.Build();
host.Run();
