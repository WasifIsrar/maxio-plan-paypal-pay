# Maxio + PayPal Billing Desk (C#)

ASP.NET Core Minimal API demo that wires **Maxio Advanced Billing** and **PayPal** together — same flow as the TypeScript app:

1. Create a **customer** and **plan (product)** in Maxio  
2. Create a **remittance subscription** and resolve the open invoice  
3. Collect payment with **PayPal Orders** (create → approve → capture)  
4. **Record the payment** on the Maxio subscription  
5. Show **paid status** and **next billing date**

Also includes a typed Maxio **422** path: `SdkException<CreateCustomerError>` caught via `TryGetCustomerErrorResponse` / `TryGetRawError`.

---

## Prerequisites

- [.NET SDK 8+](https://dotnet.microsoft.com/download) (this project targets `net10.0`; 10.x SDK recommended)
- Optional for live mode:
  - Maxio Advanced Billing site + API key
  - PayPal Developer REST app (Sandbox is fine)

---

## Quick start

```bash
cd maxio-paypal-billing-csharp
cp .env.example .env
# optional: fill Maxio + PayPal credentials in .env
dotnet restore
dotnet run
```

Open **http://localhost:5080**

| Mode | When | Behavior |
| --- | --- | --- |
| **Demo** | Maxio or PayPal env vars missing | Full UI with fake IDs |
| **Live** | Both Maxio and PayPal configured | Real APIs |

---

## Configure credentials

Edit `.env` (loaded at startup) or set environment variables:

| Variable | Purpose |
| --- | --- |
| `MAXIO_API_KEY` | Maxio API key (Basic Auth username) |
| `MAXIO_SITE` | Site subdomain (`https://{site}.chargify.com`) |
| `MAXIO_PASSWORD` | Usually `X` |
| `MAXIO_ENVIRONMENT` | `US` (default) or `EU` |
| `MAXIO_PRODUCT_FAMILY_ID` | Optional existing family id |
| `PAYPAL_CLIENT_ID` / `PAYPAL_CLIENT_SECRET` | PayPal REST app |
| `PAYPAL_ENVIRONMENT` | `Sandbox` (default) or `production` |
| `PORT` | HTTP port (default `5080`) |
| `APP_BASE_URL` | Must match the browser URL (PayPal return/cancel) |

Vendor SDKs are already under `vendor/` (project references):

- `vendor/maxio-advanced-billing-csharp-sdk`
- `vendor/paypal-csharp-sdk`

---

## Use the app

1. Open http://localhost:5080  
2. Click **Create invoice & pay with PayPal**  
3. In live mode, approve payment in PayPal Sandbox  
4. Return page shows paid status + next billing date  

**Typed error demo:** click **Show typed Maxio 422 error** (requires Maxio credentials).

---

## Project layout

```
maxio-paypal-billing-csharp/
├── Program.cs
├── Services/
│   ├── BillingFlowService.cs   # checkout + typed error handling
│   ├── SessionStore.cs
│   └── EnvLoader.cs
├── wwwroot/                    # UI
├── vendor/                     # Maxio + PayPal C# SDKs
├── .env.example
└── README.md
```

---

## HTTP API

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/api/health` | Credential / demo status |
| `POST` | `/api/checkout/start` | Maxio setup + PayPal order |
| `GET` | `/api/checkout/complete?sessionId=&token=` | Capture + record payment |
| `POST` | `/api/errors/customer-422` | Provoke typed Maxio 422 |

---

## Notes

- Invoice listing uses a format-tolerant raw Maxio fetch (Relationship Invoicing **and** legacy Statements).
- Payment recording uses `RecordPaymentForSubscription` (does not require an invoice `uid`).
- Cancellation token args on the SDKs are named `ct:` (not `cancellationToken:`).

---

## License

ISC
