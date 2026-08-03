using MaxioPaypalBilling.Services;

EnvLoader.LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var maxio = BillingFlowService.CreateMaxioClient(config, factory);
    var paypal = BillingFlowService.CreatePayPalClient(config, factory);
    return new BillingFlowService(
        config,
        sp.GetRequiredService<SessionStore>(),
        factory,
        maxio,
        paypal);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", (BillingFlowService billing) => Results.Json(billing.GetConfigStatus()));

app.MapPost("/api/checkout/start", async (StartCheckoutRequest body, BillingFlowService billing, CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(body.FirstName) ||
            string.IsNullOrWhiteSpace(body.LastName) ||
            string.IsNullOrWhiteSpace(body.Email))
        {
            return Results.BadRequest(new { error = "firstName, lastName, and email are required" });
        }

        var result = await billing.StartCheckoutAsync(
            new StartCheckoutInput(
                body.FirstName!,
                body.LastName!,
                body.Email!,
                body.Organization,
                body.PlanName,
                body.PriceCents),
            ct);
        return Results.Json(result);
    }
    catch (MaxioTypedApiError ex)
    {
        return Results.Json(
            new { error = ex.Message, errorType = ex.ErrorType, errors = ex.Errors },
            statusCode: ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapGet("/api/checkout/complete", async (string sessionId, string? token, BillingFlowService billing, CancellationToken ct) =>
{
    try
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return Results.BadRequest(new { error = "sessionId is required" });
        }

        var result = await billing.CompleteCheckoutAsync(sessionId, token, ct);
        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/api/errors/customer-422", async (BillingFlowService billing, CancellationToken ct) =>
{
    try
    {
        await billing.ProvokeCustomerValidationErrorAsync(ct);
        return Results.Json(new { error = "Expected a validation error" }, statusCode: 500);
    }
    catch (MaxioTypedApiError ex)
    {
        return Results.Json(
            new { error = ex.Message, errorType = ex.ErrorType, errors = ex.Errors },
            statusCode: ex.StatusCode);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: 500);
    }
});

app.MapFallbackToFile("index.html");

var port = Environment.GetEnvironmentVariable("PORT") ?? "5080";
app.Urls.Add($"http://localhost:{port}");

Console.WriteLine($"Maxio + PayPal billing desk (C#) on http://localhost:{port}");
var billing = app.Services.GetRequiredService<BillingFlowService>();
Console.WriteLine(
    billing.DemoMode
        ? "Running in DEMO mode (set Maxio + PayPal env vars / .env for live APIs)"
        : "Live Maxio + PayPal credentials detected");

app.Run();

public sealed record StartCheckoutRequest(
    string? FirstName,
    string? LastName,
    string? Email,
    string? Organization,
    string? PlanName,
    int? PriceCents);
