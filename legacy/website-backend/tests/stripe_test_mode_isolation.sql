-- Focused rollback coverage for Stripe test/live isolation and payment ordering.
-- The synthetic Auth users exist only inside this transaction; no mail is sent.
begin;

insert into auth.users (id, email, created_at, updated_at)
values
  ('5f5e1001-0000-4000-8000-000000000001', 'stripe-mode-owner@example.invalid', timezone('utc', now()), timezone('utc', now())),
  ('5f5e1001-0000-4000-8000-000000000002', 'stripe-mode-other@example.invalid', timezone('utc', now()), timezone('utc', now()));

set local role service_role;

do $$
declare
  v_owner uuid;
  v_other uuid;
  v_live_order uuid;
  v_test_order uuid;
  v_activation uuid;
  v_reactivated uuid;
  v_failed boolean;
  v_result jsonb;
  v_live_licenses jsonb;
  v_test_licenses jsonb;
  v_test_activations jsonb;
  v_live_session text := 'cs_mode_live_order';
  v_live_intent text := 'pi_mode_live_order';
  v_test_session text := 'cs_mode_test_order';
  v_test_intent text := 'pi_mode_test_order';
  v_refund_session text := 'cs_mode_refund_before_fulfill';
  v_refund_intent text := 'pi_mode_refund_before_fulfill';
  v_purchase_at timestamptz := '2026-01-01 00:00:00+00';
  v_updates_until timestamptz := '2027-01-01 00:00:00+00';
begin
  v_owner := '5f5e1001-0000-4000-8000-000000000001';
  v_other := '5f5e1001-0000-4000-8000-000000000002';

  v_live_order := (public.relic_fulfill_purchase(
    v_live_session, v_live_intent, 'price_mode_live', v_owner,
    'stripe-mode-owner@example.invalid', v_purchase_at, v_updates_until,
    'release-1', 3000, 'usd', false
  )->>'order_id')::uuid;
  v_test_order := (public.relic_fulfill_purchase(
    v_test_session, v_test_intent, 'price_mode_test', v_owner,
    'stripe-mode-owner@example.invalid', v_purchase_at, v_updates_until,
    'release-1', 3000, 'usd', true
  )->>'order_id')::uuid;
  if v_live_order = v_test_order then
    raise exception 'live and test fulfillment returned the same order';
  end if;

  v_live_licenses := public.relic_list_account_licenses(v_owner, false);
  v_test_licenses := public.relic_list_account_licenses(v_owner, true);
  if not (v_live_licenses @> jsonb_build_array(jsonb_build_object('license_id', v_live_order, 'is_test', false))) then
    raise exception 'live account listing omitted the live order';
  end if;
  if v_live_licenses @> jsonb_build_array(jsonb_build_object('license_id', v_test_order)) then
    raise exception 'live account listing exposed the test order';
  end if;
  if not (v_test_licenses @> jsonb_build_array(jsonb_build_object('license_id', v_test_order, 'is_test', true))) then
    raise exception 'test account listing omitted the test order';
  end if;
  if v_test_licenses @> jsonb_build_array(jsonb_build_object('license_id', v_live_order)) then
    raise exception 'test account listing exposed the live order';
  end if;
  if public.relic_list_account_licenses(v_other, false) <> '[]'::jsonb or
     public.relic_list_account_licenses(v_other, true) <> '[]'::jsonb then
    raise exception 'account listing crossed user ownership';
  end if;

  v_failed := false;
  begin
    perform public.relic_fulfill_purchase(
      v_live_session, v_live_intent, 'price_mode_live', v_owner,
      'stripe-mode-owner@example.invalid', v_purchase_at, v_updates_until,
      'release-1', 3000, 'usd', true
    );
  exception when sqlstate 'P0001' then
    v_failed := sqlerrm = 'Stripe purchase identifiers belong to another mode';
  end;
  if not v_failed then
    raise exception 'test fulfillment reused live Stripe identifiers';
  end if;

  v_activation := (public.relic_activate_license(
    v_test_order, v_owner, repeat('a', 64), 'Test computer A', true
  )->>'activation_id')::uuid;
  v_failed := false;
  begin
    perform public.relic_activate_license(
      v_test_order, v_owner, repeat('b', 64), 'Test computer B', true
    );
  exception when sqlstate 'P0002' then
    v_failed := sqlerrm = 'license seat limit reached';
  end;
  if not v_failed then
    raise exception 'one-seat limit allowed a second active device';
  end if;

  if not public.relic_revoke_activation(v_activation, v_owner, true) then
    raise exception 'test activation could not be deactivated';
  end if;
  v_reactivated := (public.relic_activate_license(
    v_test_order, v_owner, repeat('b', 64), 'Test computer B', true
  )->>'activation_id')::uuid;
  if v_reactivated = v_activation then
    raise exception 'reactivation reused a revoked activation row';
  end if;
  v_test_activations := public.relic_list_account_activations(v_owner, true);
  if not exists (
    select 1 from jsonb_array_elements(v_test_activations) as activation_row
    where activation_row->>'activation_id' = v_reactivated::text and activation_row->>'revoked_at' is null
  ) then
    raise exception 'reactivated test device is not active in the test listing';
  end if;

  v_result := public.relic_record_payment_event(
    'evt_mode_partial_refund', v_test_session, v_test_intent,
    'charge.refunded', 'partial_or_pending',
    '2026-01-02 00:00:00+00', 'hash_mode_partial', true
  );
  if v_result->>'blocked_reason' is not null then
    raise exception 'partial refund revoked a test license';
  end if;
  v_result := public.relic_record_payment_event(
    'evt_mode_partial_refund', v_test_session, v_test_intent,
    'charge.refunded', 'partial_or_pending',
    '2026-01-02 00:00:00+00', 'hash_mode_partial', true
  );
  if v_result->>'duplicate' <> 'true' then
    raise exception 'duplicate partial refund event was not idempotent';
  end if;
  if (public.relic_list_account_licenses(v_owner, true)->0->>'status') <> 'paid' then
    raise exception 'partial refund changed the test license status';
  end if;

  v_result := public.relic_record_payment_event(
    'evt_mode_full_refund', v_test_session, v_test_intent,
    'refund.updated', 'refunded',
    '2026-01-03 00:00:00+00', 'hash_mode_full', true
  );
  if v_result->>'blocked_reason' <> 'refunded' then
    raise exception 'full refund was not recorded as blocking';
  end if;
  if not exists (
    select 1 from jsonb_array_elements(public.relic_list_account_licenses(v_owner, true)) as license_row
    where license_row->>'license_id' = v_test_order::text and license_row->>'status' = 'refunded'
  ) then
    raise exception 'full refund did not revoke the test license status';
  end if;
  if exists (
    select 1 from jsonb_array_elements(public.relic_list_account_activations(v_owner, true)) as activation_row
    where activation_row->>'activation_id' = v_reactivated::text and activation_row->>'revoked_at' is null
  ) then
    raise exception 'full refund left the test activation active';
  end if;

  v_result := public.relic_record_payment_event(
    'evt_mode_refund_before_fulfill', v_refund_session, v_refund_intent,
    'refund.created', 'refunded',
    '2026-01-04 00:00:00+00', 'hash_mode_before', true
  );
  if v_result->>'blocked_reason' <> 'refunded' then
    raise exception 'refund-before-fulfill event was not blocking';
  end if;
  v_failed := false;
  begin
    perform public.relic_fulfill_purchase(
      v_refund_session, v_refund_intent, 'price_mode_test_refund', v_owner,
      'stripe-mode-owner@example.invalid', v_purchase_at, v_updates_until,
      'release-1', 3000, 'usd', true
    );
  exception when sqlstate 'P0001' then
    v_failed := sqlerrm = 'payment is blocked before fulfillment';
  end;
  if not v_failed then
    raise exception 'refund-before-fulfill allowed a test license';
  end if;

  raise notice 'PASS Stripe mode filtering, one-seat serialization, reactivation, refund ordering, and event idempotency';
end;
$$;

rollback;

do $$
begin
  if exists (select 1 from auth.users where id = '5f5e1001-0000-4000-8000-000000000001'::uuid) or
     exists (select 1 from auth.users where id = '5f5e1001-0000-4000-8000-000000000002'::uuid) then
    raise exception 'rollback test left synthetic Auth fixtures behind';
  end if;
end;
$$;
