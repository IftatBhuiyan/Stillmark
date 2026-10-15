export function addCalendarYear(date: Date): Date {
  const result = new Date(date.getTime());
  const month = result.getUTCMonth();
  const day = result.getUTCDate();
  result.setUTCDate(1);
  result.setUTCFullYear(result.getUTCFullYear() + 1);
  result.setUTCMonth(month);
  const lastDay = new Date(Date.UTC(result.getUTCFullYear(), month + 1, 0))
    .getUTCDate();
  result.setUTCDate(Math.min(day, lastDay));
  return result;
}

export function isOneTimePrice(
  price: {
    active?: boolean;
    type?: string;
    unit_amount?: number | null;
    currency?: string;
    livemode?: boolean;
  },
  amount: number,
  currency: string,
  expectedLiveMode?: boolean,
): boolean {
  return price.active === true && price.type === "one_time" &&
    price.unit_amount === amount && price.currency === currency &&
    (expectedLiveMode === undefined || price.livemode === expectedLiveMode);
}

export type PaymentMode = "test" | "live";

export function isSafeCheckoutReturnUrl(
  value: string,
  siteOrigin: string,
  mode: PaymentMode | null,
): boolean {
  try {
    const target = new URL(value);
    if (!mode || target.origin !== siteOrigin || target.username ||
      target.password || target.hash) return false;
    if (target.protocol === "https:") return true;
    return mode === "test" && target.protocol === "http:" &&
      target.port === "8765" &&
      (target.hostname === "localhost" || target.hostname === "127.0.0.1");
  } catch {
    return false;
  }
}

export function parsePaymentMode(
  value: string | undefined,
): PaymentMode | null {
  return value === "test" || value === "live" ? value : null;
}

export function hasStripeKeyPrefix(value: string, mode: PaymentMode): boolean {
  const prefix = mode === "test" ? "(?:sk|rk)_test_" : "(?:sk|rk)_live_";
  return new RegExp(`^${prefix}[A-Za-z0-9]+$`).test(value);
}

export function isStripeObjectInMode(
  object: { livemode?: boolean } | null | undefined,
  mode: PaymentMode,
): boolean {
  return object?.livemode === (mode === "live");
}

export function metadataMatchesOffer(
  metadata: Record<string, string> | null | undefined,
  product: string,
  offer: string,
  edition: string,
): boolean {
  return metadata?.product === product && metadata.offer === offer &&
    metadata.edition === edition;
}
