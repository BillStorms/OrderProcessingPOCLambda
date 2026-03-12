#!/usr/bin/env bash
set -euo pipefail

TOKEN=$(aws cognito-idp initiate-auth \
  --auth-flow USER_PASSWORD_AUTH \
  --client-id "7b8vm20msn1u5ou4qo9c5eingl" \
  --auth-parameters 'USERNAME=test-dev@example.com,PASSWORD=Deploy@2026!' \
  --query 'AuthenticationResult.IdToken' \
  --output text)

if [[ -z "$TOKEN" || "$TOKEN" == "None" ]]; then
  echo "Failed to obtain token." >&2
  exit 1
fi

printf '%s\n' "$TOKEN"
