const form = document.getElementById('checkoutForm');
const startBtn = document.getElementById('startBtn');
const errorBtn = document.getElementById('errorBtn');
const logEl = document.getElementById('log');
const modeBadge = document.getElementById('modeBadge');

function showLog(value, isError = false) {
  logEl.hidden = false;
  logEl.classList.toggle('log-error', isError);
  logEl.textContent =
    typeof value === 'string' ? value : JSON.stringify(value, null, 2);
}

async function refreshMode() {
  try {
    const res = await fetch('/api/health');
    const data = await res.json();
    if (data.demoMode) {
      modeBadge.textContent =
        'Demo mode — add Maxio + PayPal credentials in .env for live calls';
      modeBadge.className = 'badge demo';
    } else {
      modeBadge.textContent =
        'Live mode — Maxio and PayPal credentials detected';
      modeBadge.className = 'badge live';
    }
  } catch {
    modeBadge.textContent = 'Could not reach API';
    modeBadge.className = 'badge';
  }
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  startBtn.disabled = true;
  startBtn.textContent = 'Creating Maxio customer, plan & invoice…';
  logEl.hidden = true;

  const fd = new FormData(form);
  const payload = Object.fromEntries(fd.entries());
  payload.priceCents = Number(payload.priceCents);

  try {
    const res = await fetch('/api/checkout/start', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    });
    const data = await res.json();
    if (!res.ok) {
      showLog(data, true);
      throw new Error(data.error || 'Checkout start failed');
    }

    showLog(data.summary);
    startBtn.textContent = 'Redirecting to PayPal…';
    window.location.href = data.approveUrl;
  } catch (err) {
    if (logEl.hidden) {
      showLog(err instanceof Error ? err.message : String(err), true);
    }
    startBtn.disabled = false;
    startBtn.textContent = 'Create invoice & pay with PayPal';
  }
});

errorBtn.addEventListener('click', async () => {
  errorBtn.disabled = true;
  errorBtn.textContent = 'Calling Maxio with invalid customer…';
  try {
    const res = await fetch('/api/errors/customer-422', { method: 'POST' });
    const data = await res.json();
    showLog(
      {
        httpStatus: res.status,
        note: 'Caught SdkException<CreateCustomerError> via typed TryGet*',
        ...data,
      },
      true
    );
  } catch (err) {
    showLog(err instanceof Error ? err.message : String(err), true);
  } finally {
    errorBtn.disabled = false;
    errorBtn.textContent = 'Show typed Maxio 422 error';
  }
});

refreshMode();

if (new URLSearchParams(location.search).get('canceled')) {
  showLog('PayPal checkout was canceled. Start again when ready.');
}
