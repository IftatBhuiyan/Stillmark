-- Relic Screenshot licensing core.
-- Tables stay in a private schema. Edge Functions call the narrowly granted public RPCs.

create schema if not exists private;

create table private.purchase_orders (
  id uuid primary key default gen_random_uuid(),
  stripe_checkout_session_id text not null unique check (length(stripe_checkout_session_id) between 1 and 255),
  stripe_payment_intent_id text not null unique check (length(stripe_payment_intent_id) between 1 and 255),
  stripe_price_id text not null check (length(stripe_price_id) between 1 and 255),
  user_id uuid not null references auth.users(id) on delete restrict,
  original_email text not null check (length(original_email) between 3 and 320),
  purchased_at timestamptz not null,
  updates_until timestamptz not null,
  eligible_release text not null check (length(eligible_release) between 1 and 100),
  amount_cents integer not null check (amount_cents = 3000),
  currency text not null check (lower(currency) = 'usd'),
  seat_limit smallint not null default 1 check (seat_limit = 1),
  status text not null default 'paid' check (status in ('paid', 'refunded', 'disputed', 'chargeback', 'revoked')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  check (updates_until = (((purchased_at at time zone 'UTC') + interval '1 year') at time zone 'UTC'))
);

create table private.activations (
  id uuid primary key default gen_random_uuid(),
  license_id uuid not null references private.purchase_orders(id) on delete cascade,
  device_id text not null check (device_id ~ '^[0-9a-f]{64}$'),
  device_name text not null default 'Windows computer' check (char_length(device_name) between 1 and 80),
  created_at timestamptz not null default now(),
  last_refreshed_at timestamptz not null default now(),
  revoked_at timestamptz,
  revoked_reason text
);

create unique index activations_one_active_device
  on private.activations (license_id, device_id)
  where revoked_at is null;

create index activations_license_active_idx
  on private.activations (license_id)
  where revoked_at is null;

create table private.webhook_events (
  id uuid primary key default gen_random_uuid(),
  provider_event_id text not null unique check (length(provider_event_id) between 1 and 255),
  stripe_checkout_session_id text,
  stripe_payment_intent_id text,
  event_type text not null check (length(event_type) between 1 and 120),
  event_status text not null check (length(event_status) between 1 and 80),
  payload_hash text not null check (length(payload_hash) between 1 and 255),
  occurred_at timestamptz not null,
  received_at timestamptz not null default now(),
  check (stripe_checkout_session_id is null or length(stripe_checkout_session_id) between 1 and 255),
  check (stripe_payment_intent_id is null or length(stripe_payment_intent_id) between 1 and 255)
);

create table private.payment_blocks (
  id uuid primary key default gen_random_uuid(),
  provider_event_id text not null unique references private.webhook_events(provider_event_id) on delete restrict,
  stripe_checkout_session_id text,
  stripe_payment_intent_id text,
  order_id uuid references private.purchase_orders(id) on delete restrict,
  reason text not null check (reason in ('refunded', 'disputed', 'chargeback')),
  blocked_at timestamptz not null default now(),
  check (stripe_payment_intent_id is not null),
  check (length(stripe_payment_intent_id) between 1 and 255),
  check (stripe_checkout_session_id is null or length(stripe_checkout_session_id) between 1 and 255),
  check (stripe_payment_intent_id is null or length(stripe_payment_intent_id) between 1 and 255)
);

create index payment_blocks_session_idx on private.payment_blocks (stripe_checkout_session_id);
create index payment_blocks_intent_idx on private.payment_blocks (stripe_payment_intent_id);

create table private.outbox (
  id uuid primary key default gen_random_uuid(),
  dedupe_key text not null unique check (length(dedupe_key) between 1 and 255),
  event_type text not null check (length(event_type) between 1 and 120),
  order_id uuid references private.purchase_orders(id) on delete restrict,
  activation_id uuid references private.activations(id) on delete restrict,
  payload jsonb not null default '{}'::jsonb,
  created_at timestamptz not null default now(),
  delivered_at timestamptz,
  check (payload::text !~* '(sk_(live|test)_|whsec_|bearer )'),
  check (not (payload ?| array['token', 'secret', 'license_key', 'licenseKey', 'api_key', 'apiKey']))
);

create index outbox_pending_idx on private.outbox (created_at) where delivered_at is null;

create table private.published_releases (
  version text primary key check (version ~ '^[0-9]+[.][0-9]+[.][0-9]+$'),
  released_at timestamptz not null,
  download_url text not null check (download_url ~ '^https://[^[:space:]]+$'),
  sha256 text not null check (sha256 ~ '^[0-9a-f]{64}$')
);

alter table private.purchase_orders enable row level security;
alter table private.activations enable row level security;
alter table private.webhook_events enable row level security;
alter table private.payment_blocks enable row level security;
alter table private.outbox enable row level security;
alter table private.published_releases enable row level security;

revoke all on schema private from public, anon, authenticated, service_role;
revoke all on all tables in schema private from public, anon, authenticated, service_role;

create or replace function public.relic_fulfill_purchase(
  p_stripe_checkout_session_id text,
  p_stripe_payment_intent_id text,
  p_stripe_price_id text,
  p_user_id uuid,
  p_original_email text,
  p_purchased_at timestamptz,
  p_updates_until timestamptz,
  p_eligible_release text,
  p_amount_cents integer,
  p_currency text
)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_order private.purchase_orders%rowtype;
  v_blocked boolean;
begin
  if p_stripe_checkout_session_id is null or length(p_stripe_checkout_session_id) not between 1 and 255
     or p_stripe_payment_intent_id is null or length(p_stripe_payment_intent_id) not between 1 and 255
     or p_stripe_price_id is null or length(p_stripe_price_id) not between 1 and 255
     or p_user_id is null or p_original_email is null or length(p_original_email) not between 3 and 320
     or p_purchased_at is null or p_updates_until is null
     or p_eligible_release is null or length(p_eligible_release) not between 1 and 100
     or p_amount_cents is null or p_currency is null or length(p_currency) = 0 then
    raise exception using errcode = '22023', message = 'purchase fields are incomplete or out of range';
  end if;
  if p_amount_cents <> 3000 or lower(p_currency) <> 'usd' then
    raise exception using errcode = '22023', message = 'configured price must be 3000 USD cents';
  end if;
  if p_updates_until <> (((p_purchased_at at time zone 'UTC') + interval '1 year') at time zone 'UTC') then
    raise exception using errcode = '22023', message = 'updates_until must be exactly one year after purchase';
  end if;

  perform pg_advisory_xact_lock(hashtextextended(p_stripe_payment_intent_id, 0));

  select exists (
    select 1 from private.payment_blocks
    where stripe_payment_intent_id = p_stripe_payment_intent_id
  ) into v_blocked;
  if v_blocked then
    raise exception using errcode = 'P0001', message = 'payment is blocked before fulfillment';
  end if;

  select * into v_order from private.purchase_orders
  where stripe_checkout_session_id = p_stripe_checkout_session_id
     or stripe_payment_intent_id = p_stripe_payment_intent_id
  for update;

  if not found then
    begin
      insert into private.purchase_orders (
        stripe_checkout_session_id, stripe_payment_intent_id, stripe_price_id,
        user_id, original_email, purchased_at, updates_until, eligible_release,
        amount_cents, currency
      ) values (
        p_stripe_checkout_session_id, p_stripe_payment_intent_id, p_stripe_price_id,
        p_user_id, p_original_email, p_purchased_at, p_updates_until, p_eligible_release,
        p_amount_cents, lower(p_currency)
      ) returning * into v_order;
    exception when unique_violation then
      select * into v_order from private.purchase_orders
      where stripe_checkout_session_id = p_stripe_checkout_session_id
         or stripe_payment_intent_id = p_stripe_payment_intent_id
      for update;
    end;
  end if;

  if v_order.id is null then
    raise exception using errcode = '40001', message = 'purchase fulfillment conflicted; retry';
  end if;
  if v_order.stripe_checkout_session_id <> p_stripe_checkout_session_id
     or v_order.stripe_payment_intent_id <> p_stripe_payment_intent_id
     or v_order.stripe_price_id <> p_stripe_price_id
     or v_order.user_id <> p_user_id
     or v_order.original_email <> p_original_email
     or v_order.purchased_at <> p_purchased_at
     or v_order.updates_until <> p_updates_until
     or v_order.eligible_release <> p_eligible_release
     or v_order.amount_cents <> p_amount_cents
     or v_order.currency <> lower(p_currency) then
    raise exception using errcode = 'P0001', message = 'immutable purchase fields conflict';
  end if;

  insert into private.outbox (dedupe_key, event_type, order_id, payload)
  values (
    'license-issued:' || v_order.id,
    'license.issued',
    v_order.id,
    jsonb_build_object('order_id', v_order.id, 'user_id', v_order.user_id)
  ) on conflict (dedupe_key) do nothing;

  return jsonb_build_object(
    'order_id', v_order.id,
    'user_id', v_order.user_id,
    'status', v_order.status,
    'updates_until', v_order.updates_until,
    'eligible_release', v_order.eligible_release,
    'seat_limit', v_order.seat_limit
  );
end;
$$;

create or replace function public.relic_activate_license(
  p_license_id uuid,
  p_user_id uuid,
  p_device_id text,
  p_device_name text
)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_order private.purchase_orders%rowtype;
  v_activation private.activations%rowtype;
  v_active_count integer;
  v_created boolean := false;
begin
  if p_license_id is null or p_user_id is null or p_device_id is null or p_device_id !~ '^[0-9a-f]{64}$' then
    raise exception using errcode = '22023', message = 'device_id must be lowercase 64-character hex';
  end if;
  if p_device_name is null or char_length(p_device_name) not between 1 and 80 then
    raise exception using errcode = '22023', message = 'device_name must be 1 to 80 characters';
  end if;

  select * into v_order from private.purchase_orders where id = p_license_id for update;
  if not found or v_order.user_id <> p_user_id then
    raise exception using errcode = '42501', message = 'license is not owned by this account';
  end if;
  if v_order.status <> 'paid' then
    raise exception using errcode = 'P0001', message = 'license is not active for activation';
  end if;

  select * into v_activation from private.activations
  where license_id = p_license_id and device_id = p_device_id and revoked_at is null
  for update;
  if found then
    update private.activations set device_name = p_device_name, last_refreshed_at = now()
    where id = v_activation.id returning * into v_activation;
  else
    select count(*) into v_active_count from private.activations where license_id = p_license_id and revoked_at is null;
    if v_active_count >= v_order.seat_limit then
      raise exception using errcode = 'P0002', message = 'license seat limit reached';
    end if;
    insert into private.activations (license_id, device_id, device_name)
    values (p_license_id, p_device_id, p_device_name)
    returning * into v_activation;
    v_created := true;
  end if;

  if v_created then
    insert into private.outbox (dedupe_key, event_type, order_id, activation_id, payload)
    values (
      'activation-created:' || v_activation.id,
      'activation.created', v_order.id, v_activation.id,
      jsonb_build_object('order_id', v_order.id, 'activation_id', v_activation.id, 'user_id', p_user_id)
    ) on conflict (dedupe_key) do nothing;
  end if;

  return jsonb_build_object(
    'activation_id', v_activation.id,
    'license_id', v_activation.license_id,
    'device_id', v_activation.device_id,
    'device_name', v_activation.device_name,
    'last_refreshed_at', v_activation.last_refreshed_at,
    'purchased_at', v_order.purchased_at,
    'eligible_release', v_order.eligible_release,
    'updates_until', v_order.updates_until
  );
end;
$$;

create or replace function public.relic_refresh_activation(
  p_activation_id uuid,
  p_user_id uuid,
  p_device_id text
)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_license_id uuid;
  v_activation private.activations%rowtype;
  v_order private.purchase_orders%rowtype;
begin
  if p_activation_id is null or p_user_id is null or p_device_id is null then
    raise exception using errcode = '22023', message = 'activation identity is required';
  end if;
  select a.license_id into v_license_id from private.activations a where a.id = p_activation_id;
  if not found then
    raise exception using errcode = '42501', message = 'activation is not owned by this account/device';
  end if;
  select o.* into v_order from private.purchase_orders o where o.id = v_license_id for update;
  select a.* into v_activation from private.activations a where a.id = p_activation_id for update;
  if not found or v_activation.license_id <> v_order.id or v_order.user_id <> p_user_id or v_activation.device_id <> p_device_id then
    raise exception using errcode = '42501', message = 'activation is not owned by this account/device';
  end if;
  if v_activation.revoked_at is not null or v_order.status <> 'paid' then
    raise exception using errcode = 'P0001', message = 'activation is no longer active';
  end if;
  update private.activations set last_refreshed_at = now()
  where id = v_activation.id returning * into v_activation;
  return jsonb_build_object(
    'activation_id', v_activation.id,
    'license_id', v_activation.license_id,
    'device_id', v_activation.device_id,
    'device_name', v_activation.device_name,
    'last_refreshed_at', v_activation.last_refreshed_at,
    'purchased_at', v_order.purchased_at,
    'eligible_release', v_order.eligible_release,
    'updates_until', v_order.updates_until
  );
end;
$$;

create or replace function public.relic_revoke_activation(
  p_activation_id uuid,
  p_user_id uuid
)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_license_id uuid;
  v_activation private.activations%rowtype;
  v_order private.purchase_orders%rowtype;
begin
  if p_activation_id is null or p_user_id is null then
    raise exception using errcode = '22023', message = 'activation identity is required';
  end if;
  select a.license_id into v_license_id from private.activations a where a.id = p_activation_id;
  if not found then
    raise exception using errcode = '42501', message = 'activation is not owned by this account';
  end if;
  select o.* into v_order from private.purchase_orders o where o.id = v_license_id for update;
  select a.* into v_activation from private.activations a where a.id = p_activation_id for update;
  if not found or v_activation.license_id <> v_order.id or v_order.user_id <> p_user_id then
    raise exception using errcode = '42501', message = 'activation is not owned by this account';
  end if;
  if v_activation.revoked_at is null then
    update private.activations set revoked_at = now(), revoked_reason = 'account_deactivated'
    where id = v_activation.id returning * into v_activation;
    insert into private.outbox (dedupe_key, event_type, order_id, activation_id, payload)
    values ('activation-revoked:' || v_activation.id || ':' || v_activation.revoked_at, 'activation.revoked', v_order.id, v_activation.id, jsonb_build_object('order_id', v_order.id, 'activation_id', v_activation.id, 'user_id', p_user_id))
    on conflict (dedupe_key) do nothing;
  end if;
  return true;
end;
$$;

create or replace function public.relic_list_account_licenses(p_user_id uuid)
returns jsonb
language sql
security definer
set search_path = ''
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
    'license_id', o.id,
    'status', o.status,
    'purchased_at', o.purchased_at,
    'updates_until', o.updates_until,
    'eligible_release', o.eligible_release,
    'seat_limit', o.seat_limit,
    'amount_cents', o.amount_cents,
    'currency', o.currency
  ) order by o.purchased_at desc), '[]'::jsonb)
  from private.purchase_orders o
  where o.user_id = p_user_id;
$$;

create or replace function public.relic_list_account_activations(p_user_id uuid)
returns jsonb
language sql
security definer
set search_path = ''
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
    'activation_id', a.id,
    'license_id', a.license_id,
    'device_id', a.device_id,
    'device_name', a.device_name,
    'activated_at', a.created_at,
    'created_at', a.created_at,
    'last_refreshed_at', a.last_refreshed_at,
    'revoked_at', a.revoked_at,
    'revoked_reason', a.revoked_reason
  ) order by a.created_at desc), '[]'::jsonb)
  from private.activations a join private.purchase_orders o on o.id = a.license_id
  where o.user_id = p_user_id;
$$;

create or replace function public.relic_list_releases()
returns jsonb
language sql
security definer
set search_path = ''
as $$
  select coalesce(jsonb_agg(jsonb_build_object(
    'version', r.version,
    'released_at', r.released_at,
    'download_url', r.download_url,
    'sha256', r.sha256
  ) order by r.released_at desc, r.version desc), '[]'::jsonb)
  from private.published_releases r;
$$;

create or replace function public.relic_record_payment_event(
  p_provider_event_id text,
  p_stripe_checkout_session_id text,
  p_stripe_payment_intent_id text,
  p_event_type text,
  p_event_status text,
  p_occurred_at timestamptz,
  p_payload_hash text
)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_event private.webhook_events%rowtype;
  v_order private.purchase_orders%rowtype;
  v_matching_orders integer;
  v_reason text;
begin
  p_stripe_checkout_session_id := nullif(p_stripe_checkout_session_id, '');
  p_stripe_payment_intent_id := nullif(p_stripe_payment_intent_id, '');
  if p_provider_event_id is null or length(p_provider_event_id) not between 1 and 255
     or p_event_type is null or length(p_event_type) not between 1 and 120
     or p_event_status is null or length(p_event_status) not between 1 and 80
     or p_occurred_at is null or p_payload_hash is null or length(p_payload_hash) not between 1 and 255 then
    raise exception using errcode = '22023', message = 'payment event fields are incomplete or out of range';
  end if;
  if (p_stripe_checkout_session_id is not null and length(p_stripe_checkout_session_id) not between 1 and 255)
     or (p_stripe_payment_intent_id is not null and length(p_stripe_payment_intent_id) not between 1 and 255) then
    raise exception using errcode = '22023', message = 'Stripe identifiers are out of range';
  end if;

  v_reason := case
    when lower(p_event_type) in ('chargeback', 'charge.dispute.funds_withdrawn') or lower(p_event_status) = 'chargeback' then 'chargeback'
    when lower(p_event_type) in ('charge.dispute.created', 'dispute', 'disputed') or lower(p_event_status) = 'disputed' then 'disputed'
    when lower(p_event_type) in ('charge.refunded', 'refund', 'refunded') and lower(p_event_status) in ('blocked', 'refunded', 'confirmed') then 'refunded'
    when lower(p_event_status) = 'refunded' then 'refunded'
    else null
  end;
  if v_reason is not null then
    if p_stripe_payment_intent_id is null then
      raise exception using errcode = '22023', message = 'blocked payment event requires a Stripe payment intent';
    end if;
    perform pg_advisory_xact_lock(hashtextextended(p_stripe_payment_intent_id, 0));
  elsif p_stripe_payment_intent_id is not null then
    perform pg_advisory_xact_lock(hashtextextended(p_stripe_payment_intent_id, 0));
  end if;

  insert into private.webhook_events (provider_event_id, stripe_checkout_session_id, stripe_payment_intent_id, event_type, event_status, occurred_at, payload_hash)
  values (p_provider_event_id, p_stripe_checkout_session_id, p_stripe_payment_intent_id, p_event_type, p_event_status, p_occurred_at, p_payload_hash)
  on conflict (provider_event_id) do nothing returning * into v_event;
  if not found then
    select * into v_event from private.webhook_events where provider_event_id = p_provider_event_id;
    if v_event.stripe_checkout_session_id is distinct from p_stripe_checkout_session_id
       or v_event.stripe_payment_intent_id is distinct from p_stripe_payment_intent_id
       or v_event.event_type is distinct from p_event_type
       or v_event.event_status is distinct from p_event_status
       or v_event.occurred_at is distinct from p_occurred_at
       or v_event.payload_hash is distinct from p_payload_hash then
      raise exception using errcode = 'P0001', message = 'provider event identity or payload conflicts';
    end if;
    return jsonb_build_object('event_id', v_event.id, 'provider_event_id', v_event.provider_event_id, 'duplicate', true);
  end if;

  if v_reason is not null then
    select count(*) into v_matching_orders
    from private.purchase_orders
    where (p_stripe_checkout_session_id is not null and stripe_checkout_session_id = p_stripe_checkout_session_id)
       or (p_stripe_payment_intent_id is not null and stripe_payment_intent_id = p_stripe_payment_intent_id);
    if v_matching_orders > 1 then
      raise exception using errcode = 'P0001', message = 'payment identifiers map to different purchases';
    end if;
    select * into v_order from private.purchase_orders
    where (p_stripe_checkout_session_id is not null and stripe_checkout_session_id = p_stripe_checkout_session_id)
       or (p_stripe_payment_intent_id is not null and stripe_payment_intent_id = p_stripe_payment_intent_id)
    for update;
    insert into private.payment_blocks (provider_event_id, stripe_checkout_session_id, stripe_payment_intent_id, order_id, reason)
    values (p_provider_event_id, p_stripe_checkout_session_id, p_stripe_payment_intent_id, v_order.id, v_reason)
    on conflict (provider_event_id) do nothing;
    if v_order.id is not null then
      update private.purchase_orders set status = v_reason, updated_at = now() where id = v_order.id;
      update private.activations set revoked_at = coalesce(revoked_at, now()), revoked_reason = 'payment_' || v_reason where license_id = v_order.id and revoked_at is null;
      insert into private.outbox (dedupe_key, event_type, order_id, payload)
      values ('payment-blocked:' || v_order.id || ':' || p_provider_event_id, 'payment.blocked', v_order.id, jsonb_build_object('order_id', v_order.id, 'reason', v_reason, 'provider_event_id', p_provider_event_id))
      on conflict (dedupe_key) do nothing;
    end if;
  end if;

  return jsonb_build_object('event_id', v_event.id, 'provider_event_id', v_event.provider_event_id, 'blocked_reason', v_reason, 'duplicate', false);
end;
$$;

revoke all on function public.relic_fulfill_purchase(text, text, text, uuid, text, timestamptz, timestamptz, text, integer, text) from public, anon, authenticated;
revoke all on function public.relic_activate_license(uuid, uuid, text, text) from public, anon, authenticated;
revoke all on function public.relic_refresh_activation(uuid, uuid, text) from public, anon, authenticated;
revoke all on function public.relic_revoke_activation(uuid, uuid) from public, anon, authenticated;
revoke all on function public.relic_list_account_licenses(uuid) from public, anon, authenticated;
revoke all on function public.relic_list_account_activations(uuid) from public, anon, authenticated;
revoke all on function public.relic_list_releases() from public, anon, authenticated;
revoke all on function public.relic_record_payment_event(text, text, text, text, text, timestamptz, text) from public, anon, authenticated;

grant execute on function public.relic_fulfill_purchase(text, text, text, uuid, text, timestamptz, timestamptz, text, integer, text) to service_role;
grant execute on function public.relic_activate_license(uuid, uuid, text, text) to service_role;
grant execute on function public.relic_refresh_activation(uuid, uuid, text) to service_role;
grant execute on function public.relic_revoke_activation(uuid, uuid) to service_role;
grant execute on function public.relic_list_account_licenses(uuid) to service_role;
grant execute on function public.relic_list_account_activations(uuid) to service_role;
grant execute on function public.relic_list_releases() to service_role;
grant execute on function public.relic_record_payment_event(text, text, text, text, text, timestamptz, text) to service_role;
