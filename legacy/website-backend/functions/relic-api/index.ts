import Stripe from "npm:stripe@22.6.2";
import {
  createClient,
  type SupabaseClient,
  type User,
} from "npm:@supabase/supabase-js@2.116.0";
import {
  addCalendarYear,
  hasStripeKeyPrefix,
  isOneTimePrice,
  isStripeObjectInMode,
  isSafeCheckoutReturnUrl,
  metadataMatchesOffer,
  parsePaymentMode,
} from "./logic.ts";

const env = Deno.env.toObject();
const supabaseUrl = env.SUPABASE_URL ?? "";
const publishableKey = env.SUPABASE_PUBLISHABLE_KEY ?? env.SUPABASE_ANON_KEY ??
  "";
const serviceKey = env.SUPABASE_SERVICE_ROLE_KEY ?? "";
const paymentMode = parsePaymentMode(env.RELIC_PAYMENT_MODE);
const isTestMode = paymentMode === "test";
const productCode = isTestMode ? "winshot-test" : "relic-screenshot";
const stripeSecret = env.STRIPE_SECRET_KEY ?? "";
const stripeWebhookSecret = env.STRIPE_WEBHOOK_SECRET ?? "";
const priceId = env.STRIPE_PRICE_ID ?? "";
const currency = (env.RELIC_CURRENCY ?? "").toLowerCase();
const amountCents = Number(env.RELIC_AMOUNT_CENTS ?? "");
const appMajor = Number(env.RELIC_APPLICATION_MAJOR ?? "");
const edition = env.RELIC_EDITION ?? "";
const enabled =
  (env.RELIC_COMMERCE_ENABLED ?? "false").toLowerCase() === "true";
const siteOrigin = env.RELIC_SITE_ORIGIN ?? "";
const successUrl = env.RELIC_SUCCESS_URL ?? "";
const cancelUrl = env.RELIC_CANCEL_URL ?? "";
const supportReady =
  (env.RELIC_SUPPORT_READY ?? "false").toLowerCase() === "true";
const supportEmail = env.RELIC_SUPPORT_EMAIL ?? "";
const allowedDownloadHosts = (env.RELIC_ALLOWED_DOWNLOAD_HOSTS ?? "").split(",")
  .map((x) => x.trim()).filter(Boolean);
const testAccountEmails = new Set(
  (env.RELIC_TEST_ACCOUNT_EMAILS ?? "").split(",")
    .map((x) => x.trim().toLowerCase())
    .filter((x) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(x)),
);
const stripe = paymentMode && hasStripeKeyPrefix(stripeSecret, paymentMode)
  ? new Stripe(stripeSecret)
  : null;
const db: SupabaseClient | null = supabaseUrl && serviceKey
  ? createClient(supabaseUrl, serviceKey, {
    auth: { persistSession: false, autoRefreshToken: false },
  })
  : null;

const EMBEDDED_PUBLIC_KEY =
  "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEd+pHiTSnx9FlsUpzBLhe44f4O/k+uovoJIrB/tEOmgxYcKArAcbVMRa1qVzTavDUgcN9f3EPv9CtluOoK6x6fw==";
const testSigningPrivate = env.RELIC_TEST_SIGNING_PRIVATE_KEY_PKCS8_BASE64 ??
  "";
const testSigningPublic = env.RELIC_TEST_SIGNING_PUBLIC_KEY_SPKI_BASE64 ?? "";
const jsonHeaders = {
  "content-type": "application/json; charset=utf-8",
  "x-content-type-options": "nosniff",
};

class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
    public code = status === 401
      ? "auth_required"
      : status === 404
      ? "not_found"
      : status === 503
      ? "unavailable"
      : "request_failed",
  ) {
    super(message);
  }
}

function headers(req: Request): Headers {
  const origin = req.headers.get("origin");
  const h = new Headers(jsonHeaders);
  const localTestOrigin = isTestMode &&
    (origin === "http://127.0.0.1:8765" || origin === "http://localhost:8765");
  if (
    origin && (origin === siteOrigin ||
      localTestOrigin ||
      (!enabled && !siteOrigin && origin === "http://127.0.0.1:8765"))
  ) {
    h.set("access-control-allow-origin", origin);
    h.set("vary", "Origin");
    h.set(
      "access-control-allow-headers",
      "authorization, apikey, content-type, x-client-info",
    );
    h.set("access-control-allow-methods", "GET,POST,DELETE,OPTIONS");
  }
  return h;
}
function response(
  body: unknown,
  req: Request,
  status = 200,
  noStore = true,
): Response {
  const h = headers(req);
  if (noStore) {
    h.set("cache-control", "no-store");
    h.set("referrer-policy", "no-referrer");
  }
  return new Response(JSON.stringify(body), { status, headers: h });
}
function empty(req: Request, status = 204): Response {
  return new Response(null, { status, headers: headers(req) });
}
function assertEnabled() {
  if (!enabled || !paymentMode) {
    throw new ApiError(503, "Commerce is unavailable.");
  }
}
function requireConfig() {
  if (
    !paymentMode || !stripe || !db ||
    !/^whsec_[A-Za-z0-9]+$/.test(stripeWebhookSecret) ||
    !/^price_[A-Za-z0-9]+$/.test(priceId) || currency !== "usd" ||
    amountCents !== 3000 || !Number.isInteger(appMajor) || appMajor < 1 ||
    !edition || !successUrl.includes("{CHECKOUT_SESSION_ID}")
  ) throw new ApiError(503, "Commerce is not configured.");
}
function offerConfigReady() {
  return !!paymentMode && !!stripe && !!db &&
    /^whsec_[A-Za-z0-9]+$/.test(stripeWebhookSecret) &&
    /^price_[A-Za-z0-9]+$/.test(priceId) && currency === "usd" &&
    amountCents === 3000 && Number.isInteger(appMajor) && appMajor >= 1 &&
    !!edition && successUrl.includes("{CHECKOUT_SESSION_ID}");
}
async function offerReady() {
  if (!offerConfigReady()) return false;
  if (
    !supportReady || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(supportEmail) ||
    !safeSameOriginUrl(successUrl) || !safeSameOriginUrl(cancelUrl)
  ) return false;
  if (!isTestMode && !allowedDownloadHosts.length) return false;
  try {
    const configuredPrice = await stripe!.prices.retrieve(priceId);
    if (
      !isStripeObjectInMode(configuredPrice, paymentMode!) ||
      !isOneTimePrice(configuredPrice, amountCents, currency, !isTestMode)
    ) return false;
    if (isTestMode) return true;
    const releases = await rpc("relic_list_releases", {});
    return releases.length > 0 && releases.every(safeRelease);
  } catch {
    return false;
  }
}
function safeSameOriginUrl(value: string) {
  return isSafeCheckoutReturnUrl(value, siteOrigin, paymentMode);
}
function safeRelease(value: Record<string, unknown>) {
  try {
    const rawUrl = String(prop(value, "download_url", "downloadUrl") ?? "");
    const target = new URL(rawUrl);
    return typeof prop(value, "version") === "string" &&
      !!prop(value, "released_at", "releasedAt") &&
      /^[a-f0-9]{64}$/i.test(String(prop(value, "sha256") ?? "")) &&
      target.protocol === "https:" &&
      !target.username && !target.password &&
      allowedDownloadHosts.includes(target.hostname);
  } catch {
    return false;
  }
}
function bearer(req: Request): string {
  const value = req.headers.get("authorization") ?? "";
  if (!/^Bearer [^\s]{16,4096}$/i.test(value)) {
    throw new ApiError(401, "Authentication required.");
  }
  return value.slice(7).trim();
}
async function account(
  req: Request,
  requireTestAllowlist = isTestMode,
): Promise<{ id: string; email: string }> {
  if (!db) throw new ApiError(503, "Account service is unavailable.");
  const { data, error } = await db.auth.getUser(bearer(req));
  const user = data.user as User | null;
  if (
    error || !user?.id || !user.email || !user.email_confirmed_at ||
    !/^[0-9a-f-]{36}$/i.test(user.id)
  ) throw new ApiError(401, "Authentication required.");
  const email = user.email.trim().toLowerCase();
  if (requireTestAllowlist && !testAccountEmails.has(email)) {
    throw new ApiError(403, "Test commerce access is restricted.");
  }
  return { id: user.id, email };
}
async function body(
  req: Request,
  max = 16_384,
): Promise<Record<string, unknown>> {
  const text = await boundedText(req, max);
  try {
    const value = JSON.parse(text);
    if (!value || typeof value !== "object" || Array.isArray(value)) {
      throw new Error();
    }
    return value;
  } catch {
    throw new ApiError(400, "Invalid JSON.");
  }
}
async function boundedText(req: Request, max: number): Promise<string> {
  if (Number(req.headers.get("content-length") ?? "0") > max) {
    throw new ApiError(413, "Request too large.");
  }
  if (!req.body) return "";
  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const next = await reader.read();
      if (next.done) break;
      total += next.value.byteLength;
      if (total > max) {
        await reader.cancel();
        throw new ApiError(413, "Request too large.");
      }
      chunks.push(next.value);
    }
  } finally {
    reader.releaseLock();
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.length;
  }
  return new TextDecoder().decode(bytes);
}
function stringValue(value: unknown, max: number): string {
  if (typeof value !== "string" || value.length === 0 || value.length > max) {
    throw new ApiError(400, "Invalid request.");
  }
  return value;
}
function uuid(value: unknown): string {
  const v = stringValue(value, 64);
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i
      .test(v)
  ) throw new ApiError(400, "Invalid request.");
  return v;
}
function device(value: unknown): string {
  const v = stringValue(value, 64);
  if (!/^[0-9a-f]{64}$/.test(v)) throw new ApiError(400, "Invalid request.");
  return v;
}
function deviceName(value: unknown): string {
  const v = stringValue(value, 80).trim();
  if (!v || [...v].some((c) => c < " " || c === "\u007f")) {
    throw new ApiError(400, "Invalid request.");
  }
  return v;
}
function prop(row: Record<string, unknown>, ...names: string[]): unknown {
  for (const name of names) if (name in row) return row[name];
  return undefined;
}
function rows(data: unknown): Record<string, unknown>[] {
  return Array.isArray(data)
    ? data.filter((x) => x && typeof x === "object") as Record<
      string,
      unknown
    >[]
    : data && typeof data === "object"
    ? [data as Record<string, unknown>]
    : [];
}
async function rpc(
  name: string,
  args: Record<string, unknown>,
): Promise<Record<string, unknown>[]> {
  if (!db) throw new ApiError(503, "Account service is unavailable.");
  const { data, error } = await db.rpc(name, args);
  if (error?.code === "P0002" && name === "relic_activate_license") {
    throw new ApiError(409, "This license is in use on another computer.");
  }
  if (error) throw new ApiError(500, "Account operation failed.");
  return rows(data);
}
async function rpcBoolean(
  name: string,
  args: Record<string, unknown>,
): Promise<boolean> {
  if (!db) throw new ApiError(503, "Account service is unavailable.");
  const { data, error } = await db.rpc(name, args);
  if (error) throw new ApiError(500, "Account operation failed.");
  return data === true || Array.isArray(data) && data.length > 0;
}
function licenseDto(
  row: Record<string, unknown>,
  devices: Record<string, unknown>[],
) {
  const id = prop(row, "id", "license_id");
  return {
    id,
    isTest: prop(row, "is_test") === true,
    purchasedAt: prop(row, "purchased_at", "purchasedAt"),
    updatesUntil: prop(row, "updates_until", "updatesUntil"),
    seatLimit: prop(row, "seat_limit", "seatLimit") ?? 1,
    status: prop(row, "status") ?? "paid",
    devices: devices.filter((d) =>
      prop(d, "license_id", "licenseId") === id &&
      prop(d, "revoked_at", "revokedAt") == null
    )
      .map(deviceDto),
  };
}
function deviceDto(row: Record<string, unknown>) {
  return {
    id: prop(row, "activation_id", "activationId"),
    name: prop(row, "device_name", "deviceName"),
    activatedAt: prop(row, "activated_at", "activatedAt"),
  };
}
async function accountData(userId: string) {
  const licenses = await rpc("relic_list_account_licenses", {
    p_user_id: userId,
    p_test_mode: isTestMode,
  });
  const devices = await rpc("relic_list_account_activations", {
    p_user_id: userId,
    p_test_mode: isTestMode,
  });
  return licenses.map((row) => licenseDto(row, devices));
}
async function paidSession(
  session: Stripe.Checkout.Session,
): Promise<
  {
    userId: string;
    email: string;
    amount: number;
    currency: string;
    price: string;
  }
> {
  const metadata = session.metadata ?? {};
  const userId = metadata.owner_user_id ?? metadata.user_id ?? "";
  const email = metadata.owner_email ?? "";
  const line = session.line_items?.data?.length === 1
    ? session.line_items.data[0]
    : null;
  const actualPrice = line?.price?.id ?? "";
  const actualAmount = line?.price?.unit_amount ?? 0;
  const configuredPrice = await stripe!.prices.retrieve(priceId);
  if (
    !isStripeObjectInMode(session, paymentMode!) ||
    !isStripeObjectInMode(configuredPrice, paymentMode!) ||
    !isStripeObjectInMode(line?.price, paymentMode!) ||
    !isOneTimePrice(configuredPrice, amountCents, currency, !isTestMode) ||
    session.mode !== "payment" ||
    session.status !== "complete" || session.payment_status !== "paid" ||
    typeof session.payment_intent !== "string" || actualPrice !== priceId ||
    session.line_items?.has_more !== false ||
    session.amount_subtotal !== amountCents ||
    (session.amount_total ?? 0) < amountCents ||
    (session.total_details?.amount_discount ?? 0) !== 0 ||
    actualAmount !== amountCents || line?.quantity !== 1 ||
    session.currency !== currency || !/^[0-9a-f-]{36}$/i.test(userId) ||
    !email ||
    !metadataMatchesOffer(metadata, productCode, `v${appMajor}`, edition)
  ) throw new ApiError(400, "Session is not fulfillable.");
  return {
    userId,
    email: email.trim().toLowerCase(),
    amount: actualAmount,
    currency: session.currency,
    price: actualPrice,
  };
}
function base64Url(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}
function fromBase64(value: string): Uint8Array {
  const binary = atob(value.replaceAll("-", "+").replaceAll("_", "/"));
  return Uint8Array.from(binary, (c) => c.charCodeAt(0));
}
function arrayBuffer(bytes: Uint8Array): ArrayBuffer {
  const buffer = new ArrayBuffer(bytes.length);
  new Uint8Array(buffer).set(bytes);
  return buffer;
}
async function signingKey(): Promise<CryptoKey | null> {
  const secret = isTestMode
    ? testSigningPrivate
    : env.RELIC_SIGNING_PRIVATE_KEY_PKCS8_BASE64 ?? "";
  const publicKeyBase64 = isTestMode ? testSigningPublic : EMBEDDED_PUBLIC_KEY;
  if (
    !secret || !publicKeyBase64 ||
    isTestMode && publicKeyBase64 === EMBEDDED_PUBLIC_KEY
  ) return null;
  try {
    const key = await crypto.subtle.importKey(
      "pkcs8",
      arrayBuffer(fromBase64(secret)),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["sign"],
    );
    const verifier = await crypto.subtle.importKey(
      "spki",
      arrayBuffer(fromBase64(publicKeyBase64)),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    const probe = new TextEncoder().encode("relic-signing-key-check");
    const signature = await crypto.subtle.sign(
      { name: "ECDSA", hash: "SHA-256" },
      key,
      probe,
    );
    return await crypto.subtle.verify(
        { name: "ECDSA", hash: "SHA-256" },
        verifier,
        signature,
        probe,
      )
      ? key
      : null;
  } catch {
    return null;
  }
}
function derToP1363(signature: Uint8Array): Uint8Array {
  if (signature.length === 64) return signature;
  if (signature[0] !== 0x30) throw new Error("Invalid signature");
  let i = 2;
  if (signature[i++] !== 2) throw new Error("Invalid signature");
  const rl = signature[i++];
  const r = signature.slice(i, i + rl);
  i += rl;
  if (signature[i++] !== 2) throw new Error("Invalid signature");
  const sl = signature[i++];
  const s = signature.slice(i, i + sl);
  const out = new Uint8Array(64);
  out.set(r.slice(Math.max(0, r.length - 32)), 32 - Math.min(32, r.length));
  out.set(s.slice(Math.max(0, s.length - 32)), 64 - Math.min(32, s.length));
  return out;
}
async function issueKey(
  row: Record<string, unknown>,
  activation: Record<string, unknown>,
): Promise<string> {
  if (!Number.isInteger(appMajor) || appMajor < 1 || !edition) {
    throw new ApiError(503, "License issuance is unavailable.");
  }
  const key = await signingKey();
  if (!key) {
    throw new ApiError(503, "License issuance is unavailable.");
  }
  const purchased = new Date(String(prop(row, "purchased_at", "purchasedAt")));
  const updates = new Date(String(prop(row, "updates_until", "updatesUntil")));
  const now = new Date();
  const payload: Record<string, unknown> = {
    v: 3,
    id: String(prop(row, "id", "license_id")),
    // This is the app's cryptographic identity, not the Stripe product label.
    p: isTestMode ? "winshot-test" : "winshot",
    ed: String(prop(row, "edition") ?? edition),
    maj: Number(prop(row, "application_major", "applicationMajor") ?? appMajor),
    iat: Math.floor(purchased.getTime() / 1000),
    upto: Math.floor(updates.getTime() / 1000),
    dev: String(prop(activation, "device_id", "deviceId")),
    aid: String(prop(activation, "activation_id", "activationId")),
    lat: Math.floor(now.getTime() / 1000),
    exp: Math.floor((now.getTime() + 30 * 86400000) / 1000),
  };
  const name = prop(row, "name");
  const email = prop(row, "email");
  if (typeof name === "string" && name) payload.n = name;
  if (typeof email === "string" && email) payload.e = email;
  const encoded = base64Url(new TextEncoder().encode(JSON.stringify(payload)));
  const signature = derToP1363(
    new Uint8Array(
      await crypto.subtle.sign(
        { name: "ECDSA", hash: "SHA-256" },
        key,
        new TextEncoder().encode(encoded),
      ),
    ),
  );
  return `${encoded}.${base64Url(signature)}`;
}
function base64(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s);
}
async function handle(req: Request): Promise<Response> {
  if (req.method === "OPTIONS") return empty(req);
  const url = new URL(req.url);
  const path = (() => {
    const prefixes = ["/functions/v1/relic-api", "/relic-api"];
    const prefix = prefixes.find((candidate) =>
      url.pathname === candidate || url.pathname.startsWith(`${candidate}/`)
    );
    return (prefix ? url.pathname.slice(prefix.length) : url.pathname).replace(
      /\/$/,
      "",
    ) || "/";
  })();
  if (path === "/config" && req.method === "GET") {
    // Validate staging without opening checkout. The owner-controlled switch stays separate.
    const checkoutReady = !isTestMode && await offerReady() && !!(await signingKey());
    return response(
      {
        authUrl: supabaseUrl,
        publishableKey,
        checkoutEnabled: enabled && checkoutReady,
        checkoutReady,
        supportEmail: supportEmail || undefined,
        allowedDownloadHosts: allowedDownloadHosts.length
          ? allowedDownloadHosts
          : undefined,
        supportReady,
      },
      req,
      200,
      false,
    );
  }
  if (path === "/releases" && req.method === "GET") {
    const releases = db
      ? (await rpc("relic_list_releases", {})).filter(safeRelease).map((
        release,
      ) => ({
        version: prop(release, "version"),
        releasedAt: prop(release, "released_at", "releasedAt"),
        downloadUrl: prop(release, "download_url", "downloadUrl"),
        sha256: prop(release, "sha256"),
      }))
      : [];
    return response({ releases }, req, 200, false);
  }
  if (path === "/stripe-webhook" && req.method === "POST") {
    if (
      !paymentMode || !stripe || !db ||
      !/^whsec_[A-Za-z0-9]+$/.test(stripeWebhookSecret)
    ) {
      throw new ApiError(503, "Webhook is unavailable.");
    }
    const raw = await boundedText(req, 262_144);
    let event: Stripe.Event;
    try {
      event = await stripe.webhooks.constructEventAsync(
        raw,
        req.headers.get("stripe-signature") ?? "",
        stripeWebhookSecret,
        300,
      );
    } catch {
      throw new ApiError(400, "Invalid webhook signature.");
    }
    if (!isStripeObjectInMode(event, paymentMode)) {
      return response({ received: true }, req, 200, false);
    }
    const supportedEvents = new Set([
      "checkout.session.completed",
      "checkout.session.async_payment_succeeded",
      "charge.refunded",
      "refund.created",
      "refund.updated",
      "charge.dispute.created",
      "charge.dispute.funds_withdrawn",
    ]);
    if (!supportedEvents.has(event.type)) {
      return response({ received: true }, req, 200, false);
    }
    const object = event.data.object as Stripe.Checkout.Session & {
      payment_intent?: string | null;
    };
    let eventStatus = "received";
    let checkoutSessionId: string | null = null;
    let paymentIntentId: string | null = null;
    if (event.type.startsWith("checkout.session.")) {
      const session = await stripe.checkout.sessions.retrieve(object.id, {
        expand: ["line_items.data.price"],
      });
      if (!isStripeObjectInMode(session, paymentMode)) {
        return response({ received: true }, req, 200, false);
      }
      if (
        !metadataMatchesOffer(
          session.metadata,
          productCode,
          `v${appMajor}`,
          edition,
        )
      ) {
        return response({ received: true }, req, 200, false);
      }
      if (
        isTestMode && !testAccountEmails.has(
          String(session.metadata?.owner_email ?? "").trim().toLowerCase(),
        )
      ) {
        return response({ received: true }, req, 200, false);
      }
      checkoutSessionId = session.id;
      paymentIntentId = session.payment_intent
        ? String(session.payment_intent)
        : null;
      if (
        event.type === "checkout.session.completed" &&
          object.payment_status === "paid" ||
        event.type === "checkout.session.async_payment_succeeded"
      ) {
        const verified = await paidSession(session);
        const purchasedAt = new Date(event.created * 1000);
        await rpc("relic_fulfill_purchase", {
          p_stripe_checkout_session_id: session.id,
          p_stripe_payment_intent_id: String(session.payment_intent),
          p_stripe_price_id: verified.price,
          p_user_id: verified.userId,
          p_original_email: verified.email,
          p_purchased_at: purchasedAt.toISOString(),
          p_updates_until: addCalendarYear(purchasedAt).toISOString(),
          p_eligible_release: `v${appMajor}`,
          p_amount_cents: verified.amount,
          p_currency: verified.currency,
          p_test_mode: isTestMode,
        });
      }
    } else if (
      [
        "charge.refunded",
        "refund.created",
        "refund.updated",
        "charge.dispute.created",
        "charge.dispute.funds_withdrawn",
      ]
        .includes(event.type)
    ) {
      const paymentObject = event.data.object as unknown as Record<
        string,
        unknown
      >;
      paymentIntentId = String(paymentObject.payment_intent ?? "") || null;
      if (!paymentIntentId) {
        return response({ received: true }, req, 200, false);
      }
      // An upstream timeout or permission error must remain retryable. Acknowledging
      // it here would permanently lose a refund or dispute notification.
      const paymentIntent = await stripe.paymentIntents.retrieve(
        paymentIntentId,
      );
      if (
        !isStripeObjectInMode(paymentIntent, paymentMode) ||
        !metadataMatchesOffer(
          paymentIntent.metadata,
          productCode,
          `v${appMajor}`,
          edition,
        )
      ) {
        return response({ received: true }, req, 200, false);
      }
      // Stripe marks Charge.refunded only after the whole charge is refunded. A partial
      // refund must be recorded for support without revoking the customer's one-seat license.
      let confirmedBlock = event.type.startsWith("charge.dispute") ||
        event.type === "charge.refunded" && paymentObject.refunded === true;
      if (
        ["refund.created", "refund.updated"].includes(event.type) &&
        paymentObject.status === "succeeded"
      ) {
        const chargeId = String(paymentObject.charge ?? "");
        if (chargeId) {
          const charge = await stripe.charges.retrieve(chargeId);
          confirmedBlock = charge.refunded === true;
        }
      }
      eventStatus = confirmedBlock ? "blocked" : "partial_or_pending";
    }
    const hash = await crypto.subtle.digest(
      "SHA-256",
      new TextEncoder().encode(raw),
    );
    await rpc("relic_record_payment_event", {
      p_provider_event_id: event.id,
      p_stripe_checkout_session_id: checkoutSessionId,
      p_stripe_payment_intent_id: paymentIntentId,
      p_event_type: event.type,
      p_event_status: eventStatus,
      p_occurred_at: new Date(event.created * 1000).toISOString(),
      p_payload_hash: base64(new Uint8Array(hash)),
      p_test_mode: isTestMode,
    });
    return response({ received: true }, req, 200, false);
  }
  if (path === "/checkout" && req.method === "POST") {
    assertEnabled();
    requireConfig();
    if (!await offerReady()) {
      throw new ApiError(503, "Commerce is not configured.");
    }
    if (!await signingKey()) {
      throw new ApiError(503, "License issuance is unavailable.");
    }
    const user = await account(req);
    const checkoutMetadata = {
      product: productCode,
      offer: `v${appMajor}`,
      edition,
      owner_user_id: user.id,
      owner_email: user.email,
    };
    const session = await stripe!.checkout.sessions.create({
      mode: "payment",
      adaptive_pricing: { enabled: false },
      line_items: [{ price: priceId, quantity: 1 }],
      customer_creation: "if_required",
      customer_email: user.email,
      success_url: successUrl,
      cancel_url: cancelUrl,
      metadata: checkoutMetadata,
      payment_intent_data: {
        metadata: checkoutMetadata,
        receipt_email: user.email,
      },
    }, { idempotencyKey: `checkout:${user.id}:${crypto.randomUUID()}` });
    if (!isStripeObjectInMode(session, paymentMode!)) {
      throw new ApiError(503, "Checkout is not configured.");
    }
    return response({ url: session.url }, req, 200, false);
  }
  const user = await account(req);
  if (path === "/account" && req.method === "GET") {
    return response(
      {
        email: user.email,
        licenses: await accountData(user.id),
        testCheckoutEnabled: isTestMode && enabled && await offerReady() &&
          !!(await signingKey()),
      },
      req,
    );
  }
  if (path === "/activations" && req.method === "POST") {
    if (!await signingKey()) {
      throw new ApiError(503, "License issuance is unavailable.");
    }
    const input = await body(req);
    const licenseId = uuid(input.licenseId);
    const deviceId = device(input.deviceId);
    const name = deviceName(input.deviceName);
    const activationRows = await rpc("relic_activate_license", {
      p_license_id: licenseId,
      p_user_id: user.id,
      p_device_id: deviceId,
      p_device_name: name,
      p_test_mode: isTestMode,
    });
    const activation = activationRows[0];
    if (!activation) throw new ApiError(500, "Activation failed.");
    const license = (await rpc("relic_list_account_licenses", {
      p_user_id: user.id,
      p_test_mode: isTestMode,
    })).find(
      (x) =>
        String(prop(x, "id", "license_id")).toLowerCase() ===
          licenseId.toLowerCase(),
    );
    if (!license) throw new ApiError(500, "Activation failed.");
    return response({
      licenseKey: await issueKey(license, activation),
      activationId: prop(activation, "activation_id", "activationId"),
    }, req);
  }
  const match = path.match(/^\/activations\/([^/]+)(?:\/refresh)?$/);
  if (match) {
    const activationId = uuid(match[1]);
    if (req.method === "DELETE") {
      if (
        !await rpcBoolean("relic_revoke_activation", {
          p_activation_id: activationId,
          p_user_id: user.id,
          p_test_mode: isTestMode,
        })
      ) throw new ApiError(404, "Activation not found.");
      return empty(req);
    }
    if (req.method === "POST" && path.endsWith("/refresh")) {
      if (!await signingKey()) {
        throw new ApiError(503, "License issuance is unavailable.");
      }
      const input = await body(req);
      const deviceId = device(input.deviceId);
      const activationRows = await rpc("relic_refresh_activation", {
        p_activation_id: activationId,
        p_user_id: user.id,
        p_device_id: deviceId,
        p_test_mode: isTestMode,
      });
      const activation = activationRows[0];
      if (!activation) throw new ApiError(404, "Activation not found.");
      activation.activation_id ??= activationId;
      activation.device_id ??= deviceId;
      const license = (await rpc("relic_list_account_licenses", {
        p_user_id: user.id,
        p_test_mode: isTestMode,
      })).find(
        (x) =>
          String(prop(x, "id", "license_id")) ===
            String(prop(activation, "license_id", "licenseId")),
      );
      if (!license) throw new ApiError(404, "Activation not found.");
      return response({
        licenseKey: await issueKey(license, activation),
        activationId,
      }, req);
    }
  }
  throw new ApiError(404, "Not found.");
}

Deno.serve(async (req) => {
  try {
    return await handle(req);
  } catch (error) {
    if (error instanceof ApiError) {
      return response(
        { code: error.code, error: error.message },
        req,
        error.status,
      );
    }
    console.error("Relic API request failed", {
      type: error instanceof Error ? error.name : "UnknownError",
    });
    return response(
      { code: "request_failed", error: "Request failed." },
      req,
      500,
    );
  }
});
