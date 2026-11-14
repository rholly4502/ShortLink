using ShortLink.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<ClickEventConsumer>();

var host = builder.Build();
host.Run();
