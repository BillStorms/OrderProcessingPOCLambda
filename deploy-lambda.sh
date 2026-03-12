#!/usr/bin/env bash
# deploy-lambda.sh
# Builds and deploys both Lambda functions via AWS SAM.
# Prerequisites: aws-cli, sam-cli, dotnet 8 SDK, Docker (for sam build --use-container)
#
# Usage:
#   ./deploy-lambda.sh [dev|staging|prod] [<order-service-base-url>]
#
# Example:
#   ./deploy-lambda.sh dev https://abc123.execute-api.us-east-1.amazonaws.com/dev

set -euo pipefail

ENV="${1:-dev}"
ORDER_SERVICE_BASE_URL="${2:-}"
REGION="${AWS_REGION:-us-east-1}"
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
S3_BUCKET="order-sam-artifacts-${ACCOUNT_ID}-${REGION}"
STACK_NAME="order-processing-${ENV}"

echo "=== Order Processing System — SAM Deploy ==="
echo "  Environment : $ENV"
echo "  Region      : $REGION"
echo "  Account     : $ACCOUNT_ID"
echo "  Stack       : $STACK_NAME"
echo ""

# ── 1. Ensure S3 artifact bucket exists ──────────────────────────────────────
echo "[1/4] Ensuring SAM artifact bucket: $S3_BUCKET"
aws s3api create-bucket \
  --bucket "$S3_BUCKET" \
  --region "$REGION" \
  $([ "$REGION" != "us-east-1" ] && echo "--create-bucket-configuration LocationConstraint=$REGION") \
  2>/dev/null || true

# ── 2. Build both Lambda projects ────────────────────────────────────────────
echo "[2/4] Building Lambda projects"
dotnet publish lambda/OrderService.Lambda/OrderService.Lambda.csproj \
  -c Release -r linux-arm64 --self-contained false \
  -o .aws-sam/build/OrderServiceFunction

dotnet publish lambda/FulfillmentService.Lambda/FulfillmentService.Lambda.csproj \
  -c Release -r linux-arm64 --self-contained false \
  -o .aws-sam/build/FulfillmentServiceFunction

# ── 3. Package ────────────────────────────────────────────────────────────────
echo "[3/4] Packaging template"
sam package \
  --template-file template.yaml \
  --s3-bucket "$S3_BUCKET" \
  --s3-prefix "$STACK_NAME" \
  --output-template-file .aws-sam/packaged.yaml \
  --region "$REGION"

# ── 4. Deploy ─────────────────────────────────────────────────────────────────
echo "[4/4] Deploying stack: $STACK_NAME"

PARAMS="Environment=$ENV"
if [ -n "$ORDER_SERVICE_BASE_URL" ]; then
  PARAMS="$PARAMS OrderServiceBaseUrl=$ORDER_SERVICE_BASE_URL"
fi

sam deploy \
  --template-file .aws-sam/packaged.yaml \
  --stack-name "$STACK_NAME" \
  --capabilities CAPABILITY_IAM CAPABILITY_NAMED_IAM \
  --parameter-overrides "$PARAMS" \
  --region "$REGION" \
  --no-fail-on-empty-changeset

echo ""
echo "=== Deploy complete ==="
API_URL=$(aws cloudformation describe-stacks \
  --stack-name "$STACK_NAME" \
  --region "$REGION" \
  --query "Stacks[0].Outputs[?OutputKey=='ApiEndpoint'].OutputValue" \
  --output text)
echo "  API Endpoint : $API_URL"
echo "  Swagger UI   : $API_URL/swagger/index.html"

