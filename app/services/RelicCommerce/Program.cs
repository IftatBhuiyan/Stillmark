using RelicCommerce;

var builder = WebApplication.CreateBuilder(args);
CommerceOptions commerceOptions = CommerceOptions.Load(builder.Configuration);
if (commerceOptions.Enabled)
    commerceOptions.RequireSigningKeyMatchesDesktopVerifier();
builder.Services.AddSingleton(commerceOptions);
builder.Services.AddSingleton<OrderStore>();
builder.Services.AddSingleton<StripeGateway>();
builder.Services.AddSingleton<LicenseIssuer>();
builder.Services.AddSingleton<FulfillmentService>();

var app = builder.Build();
app.MapCommerceEndpoints();
app.Run();

public partial class Program { }
