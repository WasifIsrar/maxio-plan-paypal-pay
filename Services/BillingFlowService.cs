using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MaxioAdvancedBilling;
using MaxioAdvancedBilling.Core.Authentication.Basic;
using MaxioAdvancedBilling.Core.ErrorResponse;
using MaxioAdvancedBilling.Core.Exceptions;
using MaxioAdvancedBilling.Errors;
using MaxioAdvancedBilling.Models;
using MaxioAdvancedBilling.Models.Enums;
using MaxioAdvancedBilling.Servers;
using Paypal;
using Paypal.Core.Authentication.OAuth2.ClientCredentials;
using Paypal.Models;
using Paypal.Models.Enums;
using CreateSubscriptionRequest = MaxioAdvancedBilling.Models.CreateSubscriptionRequest;
using IntervalUnit = MaxioAdvancedBilling.Models.Enums.IntervalUnit;
using PaypalServers = Paypal.Servers;

namespace MaxioPaypalBilling.Services;

public sealed record StartCheckoutInput(
    string FirstName,
    string LastName,
    string Email,
    string? Organization,
    string? PlanName,
    int? PriceCents);

public sealed class MaxioTypedApiError : Exception
{
    public int StatusCode { get; }
    public string ErrorType { get; }
    public object? Errors { get; }

    public MaxioTypedApiError(string message, int statusCode, string errorType, object? errors)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        Errors = errors;
    }
}

public sealed class BillingFlowService
{
    private readonly IConfiguration _config;
    private readonly SessionStore _sessions;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly MaxioAdvancedBillingClient? _maxio;
    private readonly PaypalClient? _paypal;

    public BillingFlowService(
        IConfiguration config,
        SessionStore sessions,
        IHttpClientFactory httpClientFactory,
        MaxioAdvancedBillingClient? maxio = null,
        PaypalClient? paypal = null)
    {
        _config = config;
        _sessions = sessions;
        _httpClientFactory = httpClientFactory;
        _maxio = maxio;
        _paypal = paypal;
    }

    public bool IsMaxioConfigured =>
        !string.IsNullOrWhiteSpace(Cfg("MAXIO_API_KEY")) &&
        !string.IsNullOrWhiteSpace(Cfg("MAXIO_SITE"));

    public bool IsPayPalConfigured =>
        !string.IsNullOrWhiteSpace(Cfg("PAYPAL_CLIENT_ID")) &&
        !string.IsNullOrWhiteSpace(Cfg("PAYPAL_CLIENT_SECRET"));

    public bool DemoMode => !(IsMaxioConfigured && IsPayPalConfigured);

    private string AppBaseUrl =>
        (Cfg("APP_BASE_URL") ?? "http://localhost:5080").TrimEnd('/');

    private string? Cfg(string key) =>
        _config[key] ??
        _config[$"Maxio:{key.Replace("MAXIO_", "")}"] ??
        _config[$"PayPal:{key.Replace("PAYPAL_", "")}"] ??
        Environment.GetEnvironmentVariable(key);

    private static string MoneyFromCents(long cents) => (cents / 100m).ToString("0.00");

    public object GetConfigStatus() => new
    {
        ok = true,
        maxioConfigured = IsMaxioConfigured,
        paypalConfigured = IsPayPalConfigured,
        demoMode = DemoMode
    };

    public async Task<object> StartCheckoutAsync(StartCheckoutInput input, CancellationToken ct)
    {
        var sessionId = Guid.NewGuid().ToString();
        var planName = string.IsNullOrWhiteSpace(input.PlanName) ? "Starter Plan" : input.PlanName.Trim();
        var priceCents = input.PriceCents ?? 2900;
        var amount = MoneyFromCents(priceCents);
        const string currency = "USD";

        if (DemoMode)
        {
            var fakeInvoice = $"inv_demo_{sessionId[..8]}";
            _sessions.Save(new CheckoutSession
            {
                Id = sessionId,
                Demo = true,
                CustomerId = 1001,
                ProductId = 2002,
                SubscriptionId = 3003,
                InvoiceUid = fakeInvoice,
                InvoiceNumber = "DEMO-1001",
                Amount = amount,
                Currency = currency,
                PlanName = planName,
                PaypalOrderId = $"DEMO-ORDER-{sessionId[..8]}",
                CustomerEmail = input.Email,
                CustomerName = $"{input.FirstName} {input.LastName}",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

            return new
            {
                sessionId,
                demo = true,
                approveUrl = $"{AppBaseUrl}/return.html?sessionId={sessionId}&token=DEMO",
                summary = new
                {
                    customerId = 1001,
                    productId = 2002,
                    subscriptionId = 3003,
                    invoiceUid = fakeInvoice,
                    invoiceNumber = "DEMO-1001",
                    amount,
                    planName,
                    paypalOrderId = $"DEMO-ORDER-{sessionId[..8]}"
                }
            };
        }

        try
        {
            var created = await CreatePlanAndCustomerAsync(input, planName, priceCents, ct);
            _sessions.Save(new CheckoutSession
            {
                Id = sessionId,
                Demo = false,
                CustomerId = created.CustomerId,
                ProductId = created.ProductId,
                ProductFamilyId = created.ProductFamilyId,
                SubscriptionId = created.SubscriptionId,
                InvoiceUid = created.InvoiceReference,
                InvoiceNumber = created.InvoiceNumber,
                InvoiceFormat = created.InvoiceFormat,
                Amount = created.Amount,
                Currency = currency,
                PlanName = created.PlanName,
                CustomerEmail = input.Email,
                CustomerName = $"{input.FirstName} {input.LastName}",
                CreatedAt = DateTimeOffset.UtcNow.ToString("O")
            });

            var paypal = await CreatePayPalOrderAsync(
                created.Amount,
                currency,
                created.InvoiceReference,
                created.InvoiceNumber,
                created.PlanName,
                sessionId,
                ct);

            _sessions.Update(sessionId, s => s.PaypalOrderId = paypal.OrderId);

            return new
            {
                sessionId,
                demo = false,
                approveUrl = paypal.ApproveUrl,
                summary = new
                {
                    customerId = created.CustomerId,
                    productId = created.ProductId,
                    subscriptionId = created.SubscriptionId,
                    invoiceUid = created.InvoiceReference,
                    invoiceNumber = created.InvoiceNumber,
                    amount = created.Amount,
                    planName = created.PlanName,
                    paypalOrderId = paypal.OrderId
                }
            };
        }
        catch (MaxioTypedApiError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Checkout start failed: {ex.Message}", ex);
        }
    }

    public async Task<object> CompleteCheckoutAsync(string sessionId, string? paypalToken, CancellationToken ct)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException("Checkout session not found. Start a new checkout.");

        if (session.Demo)
        {
            return new
            {
                demo = true,
                paidStatus = "paid",
                invoiceStatus = "paid",
                invoiceDueAmount = "0.00",
                nextBillingDate = DateTimeOffset.UtcNow.AddMonths(1).ToString("O"),
                subscriptionState = "active",
                paypalOrderStatus = "COMPLETED",
                paypalCaptureId = $"CAP-DEMO-{sessionId[..8]}",
                summary = session
            };
        }

        var orderId = paypalToken ?? session.PaypalOrderId
            ?? throw new InvalidOperationException("Missing PayPal order id");
        if (session.SubscriptionId is null)
        {
            throw new InvalidOperationException("Session is missing Maxio subscription id");
        }

        var paypal = RequirePayPal();
        var maxio = RequireMaxio();

        var captured = await paypal.Orders.CaptureOrder(
            id: orderId,
            payPalMockResponse: null,
            payPalRequestId: null,
            payPalClientMetadataId: null,
            payPalAuthAssertion: null,
            body: null,
            prefer: "return=representation",
            ct: ct);

        var captureId =
            captured.PurchaseUnits?
                .SelectMany(pu => pu.Payments?.Captures ?? Array.Empty<OrdersCapture>())
                .FirstOrDefault()
                ?.Id
            ?? captured.Id
            ?? orderId;

        await maxio.Invoices.RecordPaymentForSubscription(
            subscriptionId: session.SubscriptionId.Value,
            body: new RecordPaymentRequest
            {
                Payment = new CreatePayment
                {
                    Amount = session.Amount,
                    Memo = $"PayPal capture {captureId}",
                    PaymentDetails =
                        $"paypal_order={orderId};capture={captureId};invoice={session.InvoiceUid ?? ""}",
                    PaymentMethod = InvoicePaymentMethodType.Other
                }
            },
            ct: ct);

        var subRes = await maxio.Subscriptions.ReadSubscription(
            subscriptionId: session.SubscriptionId.Value,
            include: null,
            ct: ct);

        var subscription = subRes.Subscription;
        var balanceCents = subscription?.BalanceInCents ?? 0;
        var paid = balanceCents <= 0;

        _sessions.Update(sessionId, s => s.PaypalOrderId = orderId);

        return new
        {
            demo = false,
            paidStatus = paid ? "paid" : "partial",
            invoiceStatus = paid ? "paid" : "open",
            invoiceDueAmount = MoneyFromCents(balanceCents),
            nextBillingDate = subscription?.NextAssessmentAt?.ToString("O"),
            subscriptionState = subscription?.State?.ToString(),
            paypalOrderStatus = captured.Status?.ToString(),
            paypalCaptureId = captureId,
            summary = _sessions.Get(sessionId)
        };
    }

    /// <summary>
    /// Deliberately calls Maxio with an invalid customer so the SDK throws
    /// SdkException&lt;CreateCustomerError&gt; — caught by type via TryGetCustomerErrorResponse / TryGetRawError.
    /// </summary>
    public async Task ProvokeCustomerValidationErrorAsync(CancellationToken ct)
    {
        var maxio = RequireMaxio();
        await CreateCustomerOrThrowTypedAsync(
            maxio,
            new CreateCustomerRequest
            {
                Customer = new CreateCustomer
                {
                    FirstName = "",
                    LastName = "",
                    Email = "not-a-valid-email"
                }
            },
            ct);
        throw new InvalidOperationException("Expected Maxio to reject the invalid customer");
    }

    private async Task<int> CreateCustomerOrThrowTypedAsync(
        MaxioAdvancedBillingClient maxio,
        CreateCustomerRequest body,
        CancellationToken ct)
    {
        try
        {
            var customerRes = await maxio.Customers.CreateCustomer(body, ct: ct);
            return customerRes.Customer.Id
                ?? throw new InvalidOperationException("Maxio did not return a customer id");
        }
        catch (SdkException<CreateCustomerError> ex)
        {
            // Real API failure (HTTP 422), caught by generated error type.
            if (ex.Error.TryGetCustomerErrorResponse(out var typed))
            {
                object? errors = typed.Errors;
                var message = $"Maxio rejected the customer: {JsonSerializer.Serialize(typed.Errors)}";
                throw new MaxioTypedApiError(message, 422, "CreateCustomerError", errors);
            }

            if (ex.Error.TryGetRawError(out RawError raw))
            {
                var bodyText = raw.ReadAsString();
                object? errors = TryParseJson(bodyText) ?? bodyText;
                // Prefer the common wire shape { "errors": [ "..." ] }
                if (errors is JsonElement el &&
                    el.ValueKind == JsonValueKind.Object &&
                    el.TryGetProperty("errors", out var errProp))
                {
                    errors = JsonSerializer.Deserialize<object>(errProp.GetRawText());
                    var joined = errProp.ValueKind == JsonValueKind.Array
                        ? string.Join("; ", errProp.EnumerateArray().Select(x => x.GetString()))
                        : errProp.GetRawText();
                    throw new MaxioTypedApiError(
                        $"Maxio rejected the customer: {joined}",
                        (int)raw.StatusCode,
                        "CreateCustomerError",
                        errors);
                }

                throw new MaxioTypedApiError(
                    $"Maxio rejected the customer: {bodyText}",
                    (int)raw.StatusCode,
                    "CreateCustomerError",
                    errors);
            }

            throw new MaxioTypedApiError(
                "Maxio rejected the customer (typed CreateCustomerError)",
                422,
                "CreateCustomerError",
                null);
        }
    }

    private static object? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonSerializer.Deserialize<object>(text);
        }
        catch
        {
            return null;
        }
    }

    private async Task<(
        string ProductFamilyId,
        int ProductId,
        int CustomerId,
        int SubscriptionId,
        string InvoiceReference,
        string? InvoiceNumber,
        string InvoiceFormat,
        string Amount,
        string PlanName)> CreatePlanAndCustomerAsync(
        StartCheckoutInput input,
        string planName,
        int priceCents,
        CancellationToken ct)
    {
        var maxio = RequireMaxio();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var handle = $"starter-{stamp}";

        var productFamilyId = Cfg("MAXIO_PRODUCT_FAMILY_ID")?.Trim();
        if (string.IsNullOrWhiteSpace(productFamilyId))
        {
            var familyRes = await maxio.ProductFamilies.CreateProductFamily(
                body: new CreateProductFamilyRequest
                {
                    ProductFamily = new CreateProductFamily
                    {
                        Name = $"Hackathon Catalog {stamp}",
                        Description = "Demo catalog for Maxio + PayPal checkout"
                    }
                },
                ct: ct);
            productFamilyId = familyRes.ProductFamily?.Id?.ToString()
                ?? throw new InvalidOperationException("Maxio did not return a product family id");
        }

        var productRes = await maxio.Products.CreateProduct(
            productFamilyId: productFamilyId,
            body: new CreateOrUpdateProductRequest
            {
                Product = new CreateOrUpdateProduct
                {
                    Name = planName,
                    Description = $"{planName} billed monthly via PayPal remittance",
                    Handle = handle,
                    PriceInCents = priceCents,
                    Interval = 1,
                    IntervalUnit = IntervalUnit.Month,
                    RequireCreditCard = false,
                    AutoCreateSignupPage = false
                }
            },
            ct: ct);

        var productId = productRes.Product.Id
            ?? throw new InvalidOperationException("Maxio did not return a product id");

        var customerId = await CreateCustomerOrThrowTypedAsync(
            maxio,
            new CreateCustomerRequest
            {
                Customer = new CreateCustomer
                {
                    FirstName = input.FirstName,
                    LastName = input.LastName,
                    Email = input.Email,
                    Organization = input.Organization,
                    Reference = $"hackathon-{stamp}"
                }
            },
            ct);

        var subRes = await maxio.Subscriptions.CreateSubscription(
            body: new CreateSubscriptionRequest
            {
                Subscription = new CreateSubscription
                {
                    ProductId = productId,
                    CustomerId = customerId,
                    PaymentCollectionMethod = CollectionMethod.Remittance,
                    Reference = $"sub-{stamp}"
                }
            },
            ct: ct);

        var subscription = subRes.Subscription
            ?? throw new InvalidOperationException("Maxio did not return a subscription");
        var subscriptionId = subscription.Id
            ?? throw new InvalidOperationException("Maxio did not return a subscription id");

        var balanceCents = subscription.BalanceInCents
            ?? subscription.CurrentBillingAmountInCents
            ?? priceCents;
        if (balanceCents <= 0) balanceCents = priceCents;
        var fallbackAmount = MoneyFromCents(balanceCents);

        var invoice = await FetchOpenInvoiceAsync(subscriptionId, fallbackAmount, ct);

        return (
            productFamilyId,
            productId,
            customerId,
            subscriptionId,
            invoice.Reference,
            invoice.Number,
            invoice.Format,
            invoice.Amount,
            planName);
    }

    /// <summary>
    /// Maxio RI returns { invoices: [...] }; legacy Statements returns [{ invoice: {...} }].
    /// The generated ListInvoices only accepts RI, so we fetch + normalize ourselves.
    /// </summary>
    private async Task<(string Reference, string? Number, string Amount, string Format)> FetchOpenInvoiceAsync(
        int subscriptionId,
        string fallbackAmount,
        CancellationToken ct)
    {
        var site = Cfg("MAXIO_SITE")!;
        var apiKey = Cfg("MAXIO_API_KEY")!;
        var password = Cfg("MAXIO_PASSWORD") ?? "X";

        var client = _httpClientFactory.CreateClient();
        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://{site}.chargify.com/invoices.json?subscription_id={subscriptionId}&per_page=10");
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{apiKey}:{password}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await client.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Maxio invoice list failed: HTTP {(int)res.StatusCode}");
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("invoices", out var invoices) &&
            invoices.ValueKind == JsonValueKind.Array)
        {
            foreach (var inv in invoices.EnumerateArray())
            {
                var status = inv.TryGetProperty("status", out var st) ? st.GetString() : null;
                var due = inv.TryGetProperty("due_amount", out var da) ? da.GetString() : null;
                var open = status is "open" or "pending" ||
                           (due is not null && decimal.TryParse(due, out var d) && d > 0);
                if (!open && invoices.GetArrayLength() > 1) continue;

                if (inv.TryGetProperty("uid", out var uid) && uid.GetString() is { } uidVal)
                {
                    var number = inv.TryGetProperty("number", out var n) ? n.GetString() : null;
                    var amount = due
                        ?? (inv.TryGetProperty("total_amount", out var ta) ? ta.GetString() : null)
                        ?? fallbackAmount;
                    return (uidVal, number, amount!, "relationship");
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in root.EnumerateArray())
            {
                if (!row.TryGetProperty("invoice", out var inv)) continue;
                var state = inv.TryGetProperty("state", out var st) ? st.GetString() : null;
                var dueCents = inv.TryGetProperty("amount_due_in_cents", out var ac) ? ac.GetInt64() :
                    inv.TryGetProperty("total_amount_in_cents", out var tc) ? tc.GetInt64() : 0L;
                var unpaid = state is "unpaid" or "pending" || dueCents > 0;
                if (!unpaid) continue;

                if (inv.TryGetProperty("id", out var idEl))
                {
                    var number = inv.TryGetProperty("number", out var n) ? n.GetString() : null;
                    var amount = dueCents > 0 ? MoneyFromCents(dueCents) : fallbackAmount;
                    return (idEl.ToString(), number, amount, "legacy");
                }
            }

            // fallback to first
            if (root.GetArrayLength() > 0 &&
                root[0].TryGetProperty("invoice", out var first) &&
                first.TryGetProperty("id", out var firstId))
            {
                var number = first.TryGetProperty("number", out var n) ? n.GetString() : null;
                var dueCents = first.TryGetProperty("amount_due_in_cents", out var ac)
                    ? ac.GetInt64()
                    : 0L;
                return (
                    firstId.ToString(),
                    number,
                    dueCents > 0 ? MoneyFromCents(dueCents) : fallbackAmount,
                    "legacy");
            }
        }

        return ($"sub-{subscriptionId}", null, fallbackAmount, "legacy");
    }

    private async Task<(string OrderId, string ApproveUrl)> CreatePayPalOrderAsync(
        string amount,
        string currency,
        string invoiceReference,
        string? invoiceNumber,
        string description,
        string sessionId,
        CancellationToken ct)
    {
        var paypal = RequirePayPal();
        var body = new OrderRequest
        {
            Intent = CheckoutPaymentIntent.Capture,
            PurchaseUnits =
            [
                new PurchaseUnitRequest
                {
                    ReferenceId = invoiceReference.Length > 256
                        ? invoiceReference[..256]
                        : invoiceReference,
                    Description = description,
                    CustomId = sessionId,
                    InvoiceId = (invoiceNumber ?? invoiceReference).Length > 127
                        ? (invoiceNumber ?? invoiceReference)[..127]
                        : (invoiceNumber ?? invoiceReference),
                    Amount = new AmountWithBreakdown
                    {
                        CurrencyCode = currency,
                        Value = amount
                    }
                }
            ],
            ApplicationContext = new OrderApplicationContext
            {
                BrandName = "Maxio PayPal Billing",
                ShippingPreference = ApplicationContextShippingPreference.NoShipping,
                ReturnUrl = $"{AppBaseUrl}/return.html?sessionId={sessionId}",
                CancelUrl = $"{AppBaseUrl}/?canceled=1&sessionId={sessionId}"
            }
        };

        var order = await paypal.Orders.CreateOrder(
            payPalMockResponse: null,
            payPalRequestId: null,
            payPalPartnerAttributionId: null,
            payPalClientMetadataId: null,
            payPalAuthAssertion: null,
            body: body,
            prefer: "return=representation",
            ct: ct);

        var approveUrl = order.Links?
            .FirstOrDefault(l => string.Equals(l.Rel, "approve", StringComparison.OrdinalIgnoreCase))
            ?.Href;

        if (string.IsNullOrWhiteSpace(order.Id) || string.IsNullOrWhiteSpace(approveUrl))
        {
            throw new InvalidOperationException("PayPal order missing id or approve link");
        }

        return (order.Id, approveUrl);
    }

    private MaxioAdvancedBillingClient RequireMaxio() =>
        _maxio ?? throw new InvalidOperationException("Maxio client is not configured");

    private PaypalClient RequirePayPal() =>
        _paypal ?? throw new InvalidOperationException("PayPal client is not configured");

    public static MaxioAdvancedBillingClient? CreateMaxioClient(
        IConfiguration config,
        IHttpClientFactory httpClientFactory)
    {
        var apiKey = config["MAXIO_API_KEY"] ?? Environment.GetEnvironmentVariable("MAXIO_API_KEY");
        var site = config["MAXIO_SITE"] ?? Environment.GetEnvironmentVariable("MAXIO_SITE");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(site))
        {
            return null;
        }

        var password = config["MAXIO_PASSWORD"]
            ?? Environment.GetEnvironmentVariable("MAXIO_PASSWORD")
            ?? "X";
        var envName = (config["MAXIO_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("MAXIO_ENVIRONMENT")
            ?? "US").ToUpperInvariant();

        var options = new MaxioAdvancedBillingClientOptions
        {
            Environment = envName == "EU" ? ServerEnvironment.Eu : ServerEnvironment.Us,
            BasicAuth = new BasicAuthCredentials
            {
                Username = apiKey,
                Password = password
            }
        };

        if (options.Environment == ServerEnvironment.Eu)
        {
            options.Server.Production.Eu.Site = site;
        }
        else
        {
            options.Server.Production.Us.Site = site;
        }

        return new MaxioAdvancedBillingClient(httpClientFactory.CreateClient("maxio"), options);
    }

    public static PaypalClient? CreatePayPalClient(
        IConfiguration config,
        IHttpClientFactory httpClientFactory)
    {
        var clientId = config["PAYPAL_CLIENT_ID"] ?? Environment.GetEnvironmentVariable("PAYPAL_CLIENT_ID");
        var clientSecret = config["PAYPAL_CLIENT_SECRET"]
            ?? Environment.GetEnvironmentVariable("PAYPAL_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return null;
        }

        var envName = config["PAYPAL_ENVIRONMENT"]
            ?? Environment.GetEnvironmentVariable("PAYPAL_ENVIRONMENT")
            ?? "Sandbox";

        var options = new PaypalClientOptions
        {
            Environment = string.Equals(envName, "production", StringComparison.OrdinalIgnoreCase)
                ? PaypalServers.ServerEnvironment.Production
                : PaypalServers.ServerEnvironment.Sandbox,
            Oauth2 = new OAuth2ClientCredentials
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            }
        };

        return new PaypalClient(httpClientFactory.CreateClient("paypal"), options);
    }
}
