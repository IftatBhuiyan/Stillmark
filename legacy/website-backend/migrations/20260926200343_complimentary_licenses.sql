-- Complimentary entitlements are explicit zero-dollar grants, never Stripe payments.
-- Only the database owner can insert grants; existing private-schema access stays unchanged.
alter table private.purchase_orders
  add column grant_reason text,
  alter column stripe_checkout_session_id drop not null,
  alter column stripe_payment_intent_id drop not null,
  alter column stripe_price_id drop not null,
  drop constraint purchase_orders_amount_cents_check,
  drop constraint purchase_orders_status_check;
alter table private.purchase_orders
  add constraint purchase_orders_status_check
    check (status in ('paid', 'complimentary', 'refunded', 'disputed', 'chargeback', 'revoked')),
  add constraint purchase_orders_funding_check check (
    (grant_reason is null and amount_cents = 3000 and status <> 'complimentary'
      and stripe_checkout_session_id is not null and stripe_payment_intent_id is not null and stripe_price_id is not null)
    or
    (grant_reason is not null and char_length(grant_reason) between 1 and 500
      and amount_cents = 0 and status in ('complimentary', 'revoked')
      and stripe_checkout_session_id is null and stripe_payment_intent_id is null and stripe_price_id is null)
  );
create or replace function public.relic_activate_license(
  p_license_id uuid,
  p_user_id uuid,
  p_device_id text,
  p_device_name text,
  p_test_mode boolean default false
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
  p_test_mode := coalesce(p_test_mode, false);
  if p_license_id is null or p_user_id is null or p_device_id is null or p_device_id !~ '^[0-9a-f]{64}$' then
    raise exception using errcode = '22023', message = 'device_id must be lowercase 64-character hex';
  end if;
  if p_device_name is null or char_length(p_device_name) not between 1 and 80 then
    raise exception using errcode = '22023', message = 'device_name must be 1 to 80 characters';
  end if;

  select * into v_order from private.purchase_orders
  where id = p_license_id and is_test = p_test_mode for update;
  if not found or v_order.user_id <> p_user_id then
    raise exception using errcode = '42501', message = 'license is not owned by this account';
  end if;
  if v_order.status not in ('paid', 'complimentary') then
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
    insert into private.outbox (is_test, dedupe_key, event_type, order_id, activation_id, payload)
    values (
      p_test_mode, 'activation-created:' || v_activation.id, 'activation.created', v_order.id, v_activation.id,
      jsonb_build_object('order_id', v_order.id, 'activation_id', v_activation.id, 'user_id', p_user_id)
    ) on conflict (is_test, dedupe_key) do nothing;
  end if;

  return jsonb_build_object(
    'activation_id', v_activation.id,
    'license_id', v_activation.license_id,
    'device_id', v_activation.device_id,
    'device_name', v_activation.device_name,
    'last_refreshed_at', v_activation.last_refreshed_at,
    'purchased_at', v_order.purchased_at,
    'eligible_release', v_order.eligible_release,
    'updates_until', v_order.updates_until,
    'is_test', v_order.is_test
  );
end;
$$;

create or replace function public.relic_refresh_activation(
  p_activation_id uuid,
  p_user_id uuid,
  p_device_id text,
  p_test_mode boolean default false
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
  p_test_mode := coalesce(p_test_mode, false);
  if p_activation_id is null or p_user_id is null or p_device_id is null then
    raise exception using errcode = '22023', message = 'activation identity is required';
  end if;
  select a.license_id into v_license_id
  from private.activations a
  join private.purchase_orders o on o.id = a.license_id
  where a.id = p_activation_id and o.is_test = p_test_mode;
  if not found then
    raise exception using errcode = '42501', message = 'activation is not owned by this account/device';
  end if;
  select o.* into v_order from private.purchase_orders o where o.id = v_license_id and o.is_test = p_test_mode for update;
  select a.* into v_activation from private.activations a where a.id = p_activation_id for update;
  if not found or v_activation.license_id <> v_order.id or v_order.user_id <> p_user_id or v_activation.device_id <> p_device_id then
    raise exception using errcode = '42501', message = 'activation is not owned by this account/device';
  end if;
  if v_activation.revoked_at is not null or v_order.status not in ('paid', 'complimentary') then
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
    'updates_until', v_order.updates_until,
    'is_test', v_order.is_test
  );
end;
$$;

