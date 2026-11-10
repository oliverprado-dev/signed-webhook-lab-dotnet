using SignedWebhook.Api;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<WebhookAuthenticator>();
builder.Services.AddSingleton<PaymentStore>();

var app = builder.Build();
_ = app.Services.GetRequiredService<WebhookAuthenticator>();
app.Services.GetRequiredService<PaymentStore>().Initialize();
app.MapPost("/webhooks/orders", WebhookEndpoint.HandleAsync);
app.Run();

public partial class Program { }
