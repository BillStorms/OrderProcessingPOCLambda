#!/usr/bin/env bash
# validate-cloud.sh  — end-to-end cloud validation for order-processing-dev
set -euo pipefail

API="https://w96fogenwb.execute-api.us-east-1.amazonaws.com/dev"
POOL_ID="us-east-1_9QjfBxZam"
CLIENT_ID="7b8vm20msn1u5ou4qo9c5eingl"
PASS=0
FAIL=0

ok()   { echo "    ✅  $*"; PASS=$((PASS+1)); }
fail() { echo "    ❌  $*"; FAIL=$((FAIL+1)); }
check_code() { local got=$1 want=$2 label=$3
  if [ "$got" == "$want" ]; then ok "$label (HTTP $got)"; else fail "$label (got HTTP $got, want $want)"; fi
}

echo ""
echo "========================================================"
echo "  Order Processing System — Cloud Validation Suite"
echo "========================================================"

# ── Auth token ─────────────────────────────────────────────────────────────
echo ""
echo "[auth] Obtaining Cognito JWT..."
TOKEN=$(aws cognito-idp initiate-auth \
  --auth-flow USER_PASSWORD_AUTH \
  --client-id "$CLIENT_ID" \
  --auth-parameters USERNAME=test-dev@example.com,PASSWORD=Deploy@2026! \
  --query AuthenticationResult.IdToken \
  --output text)
echo "       Token obtained: ${TOKEN:0:30}..."

# ── Test 1: POST without JWT → 401 ─────────────────────────────────────────
echo ""
echo "[1] Unauthenticated POST /orders → 401"
CODE=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API/api/v1/orders" \
  -H "Content-Type: application/json" \
  -d '{"customerId":"x","customerName":"y","items":[{"productId":"p","quantity":1}]}')
check_code "$CODE" "401" "Cognito auth blocks unauthenticated POST"

# ── Test 2: GET without JWT → 401 ──────────────────────────────────────────
echo ""
echo "[2] Unauthenticated GET /orders/x → 401"
CODE=$(curl -s -o /dev/null -w "%{http_code}" "$API/api/v1/orders/does-not-exist")
check_code "$CODE" "401" "Cognito auth blocks unauthenticated GET"

# ── Test 3: PATCH /status without X-Internal-Token → 401 ───────────────────
echo ""
echo "[3] PATCH /orders/x/status without X-Internal-Token → 401"
CODE=$(curl -s -o /dev/null -w "%{http_code}" -X PATCH \
  "$API/api/v1/orders/some-id/status" \
  -H "Content-Type: application/json" \
  -d '{"status":"Shipped"}')
check_code "$CODE" "401" "Internal-token gate blocks unauthenticated PATCH"

# ── Test 4: Health check ────────────────────────────────────────────────────
echo ""
echo "[4] GET /health → 200"
CODE=$(curl -s -o /dev/null -w "%{http_code}" "$API/health" \
  -H "Authorization: Bearer $TOKEN")
check_code "$CODE" "200" "Health endpoint responds OK"

# ── Test 5: 404 for unknown order ───────────────────────────────────────────
echo ""
echo "[5] GET /orders/00000000-… → 404"
CODE=$(curl -s -o /dev/null -w "%{http_code}" \
  "$API/api/v1/orders/00000000-0000-0000-0000-000000000000" \
  -H "Authorization: Bearer $TOKEN")
check_code "$CODE" "404" "Non-existent order returns 404"

# ── Test 6: Full happy-path (POST → SQS → Shipped) ─────────────────────────
echo ""
echo "[6] Full end-to-end: POST order → SQS → Fulfillment Lambda → Shipped"

CREATE_RESP=$(curl -s -X POST "$API/api/v1/orders" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"customerId":"e2e-cust","customerName":"E2E Test","items":[{"productId":"sku-A","quantity":3}]}')

ORDER_ID=$(echo "$CREATE_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin)['orderId'])" 2>/dev/null || true)
CREATE_STATUS=$(echo "$CREATE_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin)['status'])" 2>/dev/null || true)

if [ -z "$ORDER_ID" ]; then
  fail "POST /orders did not return an orderId. Response: $CREATE_RESP"
else
  ok "POST /orders → orderId=$ORDER_ID status=$CREATE_STATUS"

  echo "       Waiting for fulfillment Lambda (max 30s)..."
  FINAL_STATUS="unknown"
  for i in 1 2 3 4 5 6; do
    sleep 5
    GET_RESP=$(curl -s "$API/api/v1/orders/$ORDER_ID" -H "Authorization: Bearer $TOKEN")
    FINAL_STATUS=$(echo "$GET_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status','?'))" 2>/dev/null || true)
    echo "       Poll $i/6: status=$FINAL_STATUS"
    if [ "$FINAL_STATUS" = "Shipped" ] || [ "$FINAL_STATUS" = "Failed" ]; then break; fi
  done

  if [ "$FINAL_STATUS" = "Shipped" ]; then
    TRACKING=$(echo "$GET_RESP" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['fulfillment']['trackingNumber'])" 2>/dev/null || true)
    CARRIER=$(echo  "$GET_RESP" | python3 -c "import sys,json; d=json.load(sys.stdin); print(d['fulfillment']['carrier'])"        2>/dev/null || true)
    ok "Order transitioned Created→Processing→Shipped (tracking=$TRACKING, carrier=$CARRIER)"
  elif [ "$FINAL_STATUS" = "Failed" ]; then
    ok "Order transitioned Created→Processing→Failed (simulated carrier rejection)"
  else
    fail "Order stuck at status=$FINAL_STATUS after 30s"
  fi
fi

# ── Test 7: Idempotency — replay same SQS event, status must be unchanged ──
echo ""
echo "[7] Idempotency: replay SQS event for an already-shipped order"

REPLAY_EVENT=$(cat <<JSON
{"Records":[{"messageId":"idempotency-test-001","receiptHandle":"fake-handle","body":"{\"EventId\":\"idempotency-evt-001\",\"OrderId\":\"$ORDER_ID\",\"CorrelationId\":\"c1\",\"Customer\":{\"CustomerId\":\"c1\",\"CustomerName\":\"Test\"},\"Items\":[],\"CreatedAt\":\"2026-03-12T00:00:00Z\"}","attributes":{"ApproximateReceiveCount":"2","SentTimestamp":"0","SenderId":"x","ApproximateFirstReceiveTimestamp":"0"},"messageAttributes":{},"md5OfBody":"x","eventSource":"aws:sqs","eventSourceARN":"arn:aws:sqs:us-east-1:380646338601:order-events-dev","awsRegion":"us-east-1"}]}
JSON
)

STATUS_BEFORE=$(curl -s "$API/api/v1/orders/$ORDER_ID" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import sys,json; print(json.load(sys.stdin).get('status','?'))")

aws lambda invoke \
  --function-name fulfillment-service-dev \
  --cli-binary-format raw-in-base64-out \
  --payload "$REPLAY_EVENT" \
  /tmp/lambda-idempotency-replay.json > /dev/null 2>&1

sleep 4
STATUS_AFTER=$(curl -s "$API/api/v1/orders/$ORDER_ID" -H "Authorization: Bearer $TOKEN" \
  | python3 -c "import sys,json; print(json.load(sys.stdin).get('status','?'))")

if [ "$STATUS_BEFORE" = "$STATUS_AFTER" ]; then
  ok "Idempotency: status unchanged ($STATUS_BEFORE → $STATUS_AFTER) after duplicate SQS replay"
else
  fail "Idempotency broken: status changed ($STATUS_BEFORE → $STATUS_AFTER) on duplicate replay"
fi

# ── Test 8: DLQ empty (no poisoned messages) ────────────────────────────────
echo ""
echo "[8] DLQ should be empty (no unprocessable messages)"
DLQ_DEPTH=$(aws sqs get-queue-attributes \
  --queue-url "https://sqs.us-east-1.amazonaws.com/380646338601/order-events-dlq-dev" \
  --attribute-names ApproximateNumberOfMessages \
  --query Attributes.ApproximateNumberOfMessages \
  --output text)
if [ "$DLQ_DEPTH" = "0" ]; then
  ok "DLQ depth=$DLQ_DEPTH (no dead-lettered messages)"
else
  fail "DLQ has $DLQ_DEPTH message(s) — check fulfillment Lambda for errors"
fi

# ── Summary ─────────────────────────────────────────────────────────────────
echo ""
echo "========================================================"
echo "  Results: $PASS passed, $FAIL failed"
echo "========================================================"
if [ "$FAIL" -gt 0 ]; then exit 1; fi

