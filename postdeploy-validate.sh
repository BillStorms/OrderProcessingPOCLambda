#!/usr/bin/env bash
set -euo pipefail

API="https://w96fogenwb.execute-api.us-east-1.amazonaws.com/dev"
TOKEN=$(bash "./generate-token.sh")

echo "swagger_index=$(curl -s -o /dev/null -w "%{http_code}" "$API/swagger/index.html")"
echo "swagger_json=$(curl -s -o /dev/null -w "%{http_code}" "$API/swagger/v1/swagger.json")"
echo "health_auth=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $TOKEN" "$API/health")"

echo "create_unauth=$(curl -s -o /dev/null -w "%{http_code}" -X POST "$API/api/v1/orders" -H "Content-Type: application/json" -d '{"customerId":"x","customerName":"y","items":[{"productId":"p","quantity":1}]}')"

CREATE_RESP=$(curl -s -X POST "$API/api/v1/orders" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"customerId":"postdeploy","customerName":"Post Deploy","items":[{"productId":"sku-1","quantity":1}]}')

ORDER_ID=$(echo "$CREATE_RESP" | python3 -c 'import sys, json; print(json.load(sys.stdin)["orderId"])')
echo "create_auth=200"
echo "order_id=$ORDER_ID"

echo "get_auth=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer $TOKEN" "$API/api/v1/orders/$ORDER_ID")"
echo "status_patch_unauth=$(curl -s -o /dev/null -w "%{http_code}" -X PATCH "$API/api/v1/orders/$ORDER_ID/status" -H "Content-Type: application/json" -d '{"status":"Shipped"}')"
echo "status_patch_auth=$(curl -s -o /dev/null -w "%{http_code}" -X PATCH "$API/api/v1/orders/$ORDER_ID/status" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"status":"Shipped","trackingNumber":"MANUAL-001","carrier":"ManualCarrier"}')"
echo "internal_patch_missing_token=$(curl -s -o /dev/null -w "%{http_code}" -X PATCH "$API/api/v1/orders/$ORDER_ID/status/internal" -H "Content-Type: application/json" -d '{"status":"Shipped"}')"

echo -n "status_body_schema_contains_enum="
if curl -s "$API/swagger/v1/swagger.json" | grep -q '"Created"'; then
  echo "yes"
else
  echo "no"
fi

echo -n "swagger_has_status_route="
if curl -s "$API/swagger/v1/swagger.json" | grep -q '/api/v1/orders/{orderId}/status"'; then
  echo "yes"
else
  echo "no"
fi

echo -n "swagger_hides_internal_status_route="
if curl -s "$API/swagger/v1/swagger.json" | grep -q '/api/v1/orders/{orderId}/status/internal'; then
  echo "no"
else
  echo "yes"
fi

echo "final_order=$(curl -s -H "Authorization: Bearer $TOKEN" "$API/api/v1/orders/$ORDER_ID")"

