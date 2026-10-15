import { assert, assertEquals } from "jsr:@std/assert@1.0.14";
import {
  addCalendarYear,
  hasStripeKeyPrefix,
  isOneTimePrice,
  isStripeObjectInMode,
  isSafeCheckoutReturnUrl,
  metadataMatchesOffer,
  parsePaymentMode,
} from "../functions/relic-api/logic.ts";

Deno.test("checkout returns support hosted sandbox without allowing other origins", () => {
  const origin = "https://stillmark-7zo.pages.dev";
  assert(isSafeCheckoutReturnUrl(`${origin}/manage-license.html?checkout=success`, origin, "test"));
  assert(isSafeCheckoutReturnUrl(`${origin}/manage-license.html`, origin, "live"));
  assert(!isSafeCheckoutReturnUrl("https://other.pages.dev/", origin, "test"));
  assert(!isSafeCheckoutReturnUrl("https://user:password@stillmark-7zo.pages.dev/", origin, "test"));
  assert(!isSafeCheckoutReturnUrl(`${origin}/#secret`, origin, "test"));
  assert(!isSafeCheckoutReturnUrl(`${origin}/`, origin, null));
  const local = "http://127.0.0.1:8765";
  assert(isSafeCheckoutReturnUrl(`${local}/manage-license.html`, local, "test"));
  assert(!isSafeCheckoutReturnUrl(`${local}/manage-license.html`, local, "live"));
  assert(!isSafeCheckoutReturnUrl("http://example.com/", "http://example.com", "test"));
});

Deno.test("purchase cutoff clamps leap day to February 28", () => {
  const purchase = new Date("2028-02-29T23:59:59.123Z");
  assertEquals(
    addCalendarYear(purchase).toISOString(),
    "2029-02-28T23:59:59.123Z",
  );
});

Deno.test("only active one-time exact USD price is eligible", () => {
  assert(
    isOneTimePrice(
      { active: true, type: "one_time", unit_amount: 3000, currency: "usd" },
      3000,
      "usd",
    ),
  );
  assert(
    !isOneTimePrice(
      { active: true, type: "recurring", unit_amount: 3000, currency: "usd" },
      3000,
      "usd",
    ),
  );
  assert(
    !isOneTimePrice(
      { active: false, type: "one_time", unit_amount: 3000, currency: "usd" },
      3000,
      "usd",
    ),
  );
});

Deno.test("payment mode and Stripe key prefixes fail closed", () => {
  assertEquals(parsePaymentMode("test"), "test");
  assertEquals(parsePaymentMode("live"), "live");
  assertEquals(parsePaymentMode(undefined), null);
  assertEquals(parsePaymentMode("sandbox"), null);
  assert(hasStripeKeyPrefix("sk_test_123", "test"));
  assert(hasStripeKeyPrefix("rk_live_123", "live"));
  assert(!hasStripeKeyPrefix("sk_live_123", "test"));
  assert(!hasStripeKeyPrefix("whsec_123", "live"));
});

Deno.test("Stripe livemode and offer metadata are exact", () => {
  assert(isStripeObjectInMode({ livemode: false }, "test"));
  assert(!isStripeObjectInMode({ livemode: true }, "test"));
  assert(
    isOneTimePrice(
      {
        active: true,
        type: "one_time",
        unit_amount: 3000,
        currency: "usd",
        livemode: false,
      },
      3000,
      "usd",
      false,
    ),
  );
  assert(
    !isOneTimePrice(
      {
        active: true,
        type: "one_time",
        unit_amount: 3000,
        currency: "usd",
        livemode: true,
      },
      3000,
      "usd",
      false,
    ),
  );
  assert(
    metadataMatchesOffer(
      { product: "winshot-test", offer: "v1", edition: "pro" },
      "winshot-test",
      "v1",
      "pro",
    ),
  );
  assert(
    !metadataMatchesOffer(
      { product: "relic-screenshot", offer: "v1", edition: "pro" },
      "winshot-test",
      "v1",
      "pro",
    ),
  );
});
