# Stillmark API Edge Function

This function is disabled until `RELIC_COMMERCE_ENABLED=true` and all offer, Stripe, database, and signing settings are supplied as server-only Edge Function secrets. It uses Supabase Auth `getUser` for every account or activation request and requires a confirmed email. The service-role key and P-256 PKCS#8 signing key never go to the browser.

Required server settings for checkout and issuance:

- `SUPABASE_URL`, `SUPABASE_SERVICE_ROLE_KEY`, `SUPABASE_PUBLISHABLE_KEY`
- `RELIC_COMMERCE_ENABLED`, `RELIC_PAYMENT_MODE=test|live`, `STRIPE_SECRET_KEY`, `STRIPE_WEBHOOK_SECRET`, `STRIPE_PRICE_ID`
- `RELIC_CURRENCY=usd`, `RELIC_AMOUNT_CENTS=3000`, `RELIC_APPLICATION_MAJOR`, `RELIC_EDITION`
- `RELIC_SUCCESS_URL` containing `{CHECKOUT_SESSION_ID}`, `RELIC_CANCEL_URL`
- `RELIC_SIGNING_PRIVATE_KEY_PKCS8_BASE64` containing the P-256 signing key DER
- `RELIC_SITE_ORIGIN` (one exact HTTPS origin in production)

`GET /config` is public and exposes only the Supabase URL, publishable key, checkout state, support state, and optional download hosts. `GET /releases` is empty until a verified release catalog is configured. Account and activation routes are bearer-only and return no private signing material except the newly issued key in an activation response. Stripe webhooks verify the raw body signature with a five-minute tolerance and re-fetch sessions before checking the exact configured one-time USD offer.

The signed v3 activation lease preserves the original purchase issue and update cutoff and adds a 30-day lease (`lat`/`exp`). A released, refunded, or disputed activation cannot refresh; an already delivered offline key remains usable until its lease expires.

## Sandbox integration

The September 21 test offer is Stripe product `prod_VIa6iYQPInFXDs`, price
`price_1UHywSPjhkrZs66d9c6uOZPu` in sandbox account `acct_1UHyo7PjhkrZs66d`.
It is a one-off USD 30.00 price. These identifiers are public configuration, not credentials.
Do not reuse them for live checkout.

The sandbox webhook is `we_1UHz0WPjhkrZs66dcnKzglky`, using snapshot events and
API version `2026-08-26.dahlia`, matching Stripe SDK 22.6.2. Its restricted API key
and webhook secret are stored in project `kwbjlzozcaeqzdzcexzq` Edge Function secrets.
The previous September 12 sandbox is not the connected account.

Test mode additionally requires `RELIC_TEST_ACCOUNT_EMAILS` (a comma-separated allowlist of
confirmed account emails), `RELIC_TEST_SIGNING_PRIVATE_KEY_PKCS8_BASE64`, and
`RELIC_TEST_SIGNING_PUBLIC_KEY_SPKI_BASE64`. The test signing pair must be separate from
the production key. Test activation payloads use `winshot-test` and cannot unlock the
released app. Production payloads retain the app's existing `winshot` identity.

For the local preview, set the origin to `http://127.0.0.1:8765`, success URL to
`http://127.0.0.1:8765/manage-license.html?checkout=success&session_id={CHECKOUT_SESSION_ID}`,
and cancel URL to `http://127.0.0.1:8765/manage-license.html?checkout=cancelled`.
Test mode does not require a published release archive. Live mode requires HTTPS,
configured support, approved download hosts, and a nonempty validated release archive.

The connected hosted sandbox uses `https://stillmark-7zo.pages.dev` as its exact
origin, with the same account return paths above. HTTPS same-origin return URLs
are supported in both modes; HTTP is limited to the listed local test addresses.
Only `iftatbhuiyan100@gmail.com` is allowlisted for test purchases. Separate test
signing keys are stored server-side; public checkout remains disabled.

Hosted Auth's Site URL is `https://stillmark-7zo.pages.dev/manage-license`.
Both signup confirmation and magic-link/OTP email templates use
`supabase/templates/sign-in-code.html` with subject `Your Stillmark sign-in code`.
Hosted Auth uses Resend SMTP (`smtp.resend.com:465`, username `resend`) with sender
`Stillmark <accounts@getstillmark.com>`. Its sending-only credential is scoped to
`getstillmark.com` and stored in Supabase, not in this repository. Resend verified
the domain and reported the owner's sign-in email delivered on September 21.
The three Resend DNS records use 60-second TTLs. Supabase retains the 60-second
per-user send interval; custom SMTP starts at 30 emails per hour. Local Auth
settings use preview port 8765.

The public config never enables checkout in test mode. Only an authenticated allowlisted
account receives the test purchase control. Stripe credentials and object `livemode`
must match the explicit server mode. Every licensing RPC includes the mode and the
database persists `is_test` separately for orders, events, blocks, and delivery work.

The backend restricted key needs Checkout Sessions write and Charges/Refunds,
Payment Intents, Products, and Prices read. No payout access is needed. Configure a
snapshot webhook at `/functions/v1/relic-api/stripe-webhook` for
`checkout.session.completed`, `checkout.session.async_payment_succeeded`,
`charge.refunded`, `refund.created`, `refund.updated`, `charge.dispute.created`, and
`charge.dispute.funds_withdrawn`. Match its API version to the pinned Stripe SDK.
No raw card details, webhook bodies, or secret keys belong in logs or this repository.

Creating the sandbox product or deploying this function is not proof of fulfillment.
Verify an actual sandbox checkout, webhook retries, full/partial refunds, one-seat
activation, transfer, and issued-key verification before enabling live sales.

## Receipts and license delivery

Checkout supplies the authenticated account email as `payment_intent_data.receipt_email`.
Use Stripe's built-in payment receipts and enable refund receipts in Stripe's customer-email
settings. See https://docs.stripe.com/receipts. Test-mode receipts require manual sending;
no email delivery has been verified by local tests.

License access and eligible downloads are delivered through the signed-in account. The desktop
app obtains its device-bound activation from this API. Activation keys are not emailed. The
existing durable outbox records license/device/payment events; a separate custom email worker
is not needed for this initial account-based flow and has not been implemented. Supabase sign-in
codes use the verified Resend SMTP connection described above.

The receipt-email and hosted sandbox return changes were deployed on September 21
(Edge Function version 5). Five logic tests and the function type check passed.
The hosted origin passes CORS and unsigned webhook requests return 400. These
An authenticated sandbox checkout completed on September 21 with Stripe's test card.
The signed webhook was recorded as `checkout.session.completed`, and the account
displayed a sandbox license with updates through September 21, 2027. This verifies
checkout, event delivery, and account license issuance. Refund handling and device
activation still need end-to-end verification. Resend separately confirmed delivery
of a real sign-in code through the hosted Auth flow.
