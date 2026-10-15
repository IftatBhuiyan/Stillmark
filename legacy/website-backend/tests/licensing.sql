-- Transactional licensing checks for a fresh local Supabase project.
-- Synthetic Auth users and all licensing rows are rolled back at the end.
begin;

insert into auth.users (
  id, email, created_at, updated_at
) values
  ('00000000-0000-4000-8000-000000000001', 'licensing-owner@example.test', timezone('utc', now()), timezone('utc', now())),
  ('00000000-0000-4000-8000-000000000002', 'licensing-other@example.test', timezone('utc', now()), timezone('utc', now()));

set local role anon;

do $$
declare
  v_failed boolean := false;
begin
  begin
    perform public.relic_list_account_licenses('00000000-0000-4000-8000-000000000001'::uuid);
  exception when insufficient_privilege then
    v_failed := true;
  end;
  if not v_failed then raise exception 'anon can call service-only licensing RPC'; end if;
  v_failed := false;
  begin
    perform 1 from private.purchase_orders;
  exception when insufficient_privilege then
    v_failed := true;
  end;
  if not v_failed then raise exception 'anon can read private licensing tables'; end if;
end;
$$;

set local role authenticated;

do $$
declare
  v_failed boolean := false;
begin
  begin
    perform public.relic_list_account_licenses('00000000-0000-4000-8000-000000000001'::uuid);
  exception when insufficient_privilege then
    v_failed := true;
  end;
  if not v_failed then raise exception 'authenticated can call service-only licensing RPC'; end if;
  v_failed := false;
  begin
    perform 1 from private.purchase_orders;
  exception when insufficient_privilege then
    v_failed := true;
  end;
  if not v_failed then raise exception 'authenticated can read private licensing tables'; end if;
end;
$$;

set local role service_role;

do $$
declare
  v_owner uuid;
  v_other uuid;
  v_order uuid;
  v_activation uuid;
  v_again uuid;
  v_failed boolean;
  v_session text := 'cs_test_relic_license_order';
  v_intent text := 'pi_test_relic_license_order';
  v_refund_session text := 'cs_test_relic_refund_before_paid';
  v_refund_intent text := 'pi_test_relic_refund_before_paid';
begin
  v_owner := '00000000-0000-4000-8000-000000000001';
  v_other := '00000000-0000-4000-8000-000000000002';

  v_failed := false;
  begin
    insert into private.purchase_orders (
      stripe_checkout_session_id, stripe_payment_intent_id, stripe_price_id, user_id,
      original_email, purchased_at, updates_until, eligible_release, amount_cents, currency
    ) values (
      'cs_test_direct_insert', 'pi_test_direct_insert', 'price_test_relic_30_usd', v_owner,
      'owner@example.test', '2026-01-01 00:00:00+00', '2027-01-01 00:00:00+00', 'release-1', 3000, 'usd'
    );
  exception when insufficient_privilege then
    v_failed := true;
  end;
  if not v_failed then raise exception 'service_role can write licensing tables directly'; end if;

  v_order := (public.relic_fulfill_purchase(
    v_session, v_intent, 'price_test_relic_30_usd', v_owner, 'owner@example.test',
    '2026-01-01 00:00:00+00', '2027-01-01 00:00:00+00', 'release-1', 3000, 'usd'
  )->>'order_id')::uuid;
  if not (public.relic_list_account_licenses(v_owner) @> jsonb_build_array(jsonb_build_object('license_id', v_order))) then
    raise exception 'owner cannot read own license';
  end if;
  if public.relic_list_account_licenses(v_other) @> jsonb_build_array(jsonb_build_object('license_id', v_order)) then
    raise exception 'other account can read owner license';
  end if;

  v_failed := false;
  begin
    perform public.relic_activate_license(v_order, v_other, repeat('a', 64), 'Other computer');
  exception when others then
    v_failed := true;
  end;
  if not v_failed then raise exception 'other account activated owner license'; end if;

  v_failed := false;
  begin
    perform public.relic_activate_license(v_order, null, repeat('c', 64), 'Null owner');
  exception when others then
    v_failed := true;
  end;
  if not v_failed then raise exception 'NULL owner bypassed activation ownership check'; end if;

  v_activation := (public.relic_activate_license(v_order, v_owner, repeat('a', 64), 'Owner computer')->>'activation_id')::uuid;
  v_again := (public.relic_activate_license(v_order, v_owner, repeat('a', 64), 'Owner computer renamed')->>'activation_id')::uuid;
  if v_again <> v_activation then raise exception 'same-device activation was not idempotent'; end if;

  -- The RPC locks the purchase order before counting active devices, so two concurrent
  -- callers cannot both pass this one-seat check. This transaction verifies the guard path.
  v_failed := false;
  begin
    perform public.relic_activate_license(v_order, v_owner, repeat('b', 64), 'Second computer');
  exception when others then
    v_failed := true;
  end;
  if not v_failed then raise exception 'second device bypassed one-seat limit'; end if;
  if not public.relic_revoke_activation(v_activation, v_owner) then
    raise exception 'owner could not revoke its activation';
  end if;

  if (public.relic_record_payment_event(
    'evt_test_relic_partial_refund', v_session, v_intent,
    'charge.refunded', 'partial_or_pending', '2026-01-03 00:00:00+00', 'hash_test_partial'
  )->>'blocked_reason') is not null then
    raise exception 'partial refund was treated as a license-blocking refund';
  end if;

  perform public.relic_record_payment_event(
    'evt_test_relic_refund_before_paid', v_refund_session, v_refund_intent,
    'charge.refunded', 'refunded', '2026-01-02 00:00:00+00', 'hash_test_refund'
  );
  v_failed := false;
  begin
    perform public.relic_record_payment_event(
      'evt_test_relic_refund_before_paid', v_refund_session, v_refund_intent,
      'charge.refunded', 'refunded', '2026-01-02 00:00:00+00', 'changed_hash'
    );
  exception when others then
    v_failed := true;
  end;
  if not v_failed then raise exception 'conflicting duplicate webhook event was accepted'; end if;
  v_failed := false;
  begin
    perform public.relic_fulfill_purchase(
      v_refund_session, v_refund_intent, 'price_test_relic_30_usd', v_owner, 'owner@example.test',
      '2026-01-02 00:00:00+00', '2027-01-02 00:00:00+00', 'release-1', 3000, 'usd'
    );
  exception when others then
    v_failed := true;
  end;
  if not v_failed then raise exception 'refund-before-paid did not block fulfillment'; end if;

  raise notice 'PASS licensing isolation, one-seat idempotency, and refund-before-paid checks';
end;
$$;

rollback;
