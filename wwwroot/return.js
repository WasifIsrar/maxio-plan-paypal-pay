const resultEl = document.getElementById('result');
const params = new URLSearchParams(location.search);
const sessionId = params.get('sessionId');
const token = params.get('token');

function formatDate(value) {
  if (!value) return '—';
  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? value : d.toLocaleString();
}

async function run() {
  if (!sessionId) {
    resultEl.innerHTML = '<p class="error">Missing sessionId.</p>';
    return;
  }

  try {
    const qs = new URLSearchParams({ sessionId });
    if (token) qs.set('token', token);
    const res = await fetch(`/api/checkout/complete?${qs}`);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || 'Completion failed');

    resultEl.innerHTML = `
      <h2>${data.paidStatus === 'paid' ? 'Invoice paid' : 'Checkout finished'}</h2>
      <dl>
        <dt>Paid status</dt><dd>${data.paidStatus}</dd>
        <dt>Invoice status</dt><dd>${data.invoiceStatus ?? '—'}</dd>
        <dt>Amount due</dt><dd>${data.invoiceDueAmount ?? '0.00'}</dd>
        <dt>Next billing date</dt><dd>${formatDate(data.nextBillingDate)}</dd>
        <dt>Subscription</dt><dd>${data.subscriptionState ?? '—'}</dd>
        <dt>PayPal status</dt><dd>${data.paypalOrderStatus ?? '—'}</dd>
        <dt>Capture id</dt><dd>${data.paypalCaptureId ?? '—'}</dd>
        <dt>Mode</dt><dd>${data.demo ? 'Demo' : 'Live'}</dd>
      </dl>
    `;
  } catch (err) {
    resultEl.innerHTML = `<p class="error">${
      err instanceof Error ? err.message : String(err)
    }</p>`;
  }
}

run();
