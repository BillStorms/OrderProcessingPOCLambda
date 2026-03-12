# Order Processing POC - High Level Design (HLD)


## 1. Purpose

This document describes the high-level architecture of the Order Processing POC. The solution demonstrates an event-driven order workflow using AWS serverless components with .NET 8.

## 2. Scope and Goals

### In Scope

- Create and read orders via HTTP API.
- Asynchronous fulfillment triggered by SQS events.
- Idempotent processing for both consumer and status update paths.
- Cloud deployment via AWS SAM.

### Out of Scope

- Production-grade identity/tenant model.
- Advanced resiliency patterns (saga orchestration, compensating transactions).
- Full observability stack (distributed tracing, dashboards, alert routing).

## 3. System Context

Primary flow:

1. Client creates an order through API Gateway.
2. Order Service persists the order and publishes `OrderCreatedEvent` to SQS.
3. Fulfillment Lambda consumes the message, processes shipment, and updates order status.
4. Client queries order state.

## 4. Architecture Overview

![](http://localhost:63342/markdownPreview/1910435792/docs)

`Client   -> API Gateway HTTP API    -> Order Service Lambda (ASP.NET Core)      -> DynamoDB Orders table (persistence)      -> SQS Order Events queue (publish) SQS Order Events queue   -> Fulfillment Service Lambda (SQS trigger)    -> DynamoDB Idempotency table (dedupe)    -> PATCH Order Service /status/internal SQS DLQ captures poison messages after retry policy.`

## 5. Major Components

### API Layer

- API Gateway HTTP API routes requests to `order-service-dev` Lambda.
- Cognito authorizer protects user-facing API routes.
- Swagger endpoints are exposed for documentation/testing.

### Order Service Lambda

- Hosts ASP.NET Core API.
- Handles order creation and reads.
- Publishes order events through `SqsEventPublisher`.
- Supports two status update paths:
    - Public, Swagger-visible: `PATCH /api/v1/orders/{orderId}/status` (Bearer auth).
    - Internal, hidden: `PATCH /api/v1/orders/{orderId}/status/internal` (X-Internal-Token).

### Fulfillment Service Lambda

- Triggered by SQS batch events.
- Performs idempotency claim before processing.
- Sends status transitions (`Processing`, then `Shipped`/`Failed`) to internal status route.

### Data Stores

- `orders-{env}` DynamoDB table stores serialized order payload.
- `order-idempotency-{env}` DynamoDB table stores processed event claims with TTL.

### Messaging

- `order-events-{env}` SQS queue transports order-created events.
- `order-events-dlq-{env}` captures events that exceed retry policy.

## 6. Security Model

### External API Security

- Cognito JWT protects public API routes.
- Rate limiting and security headers applied in API middleware.

### Internal Service Security

- Internal status route bypasses Cognito and is secured by `X-Internal-Token`.
- Fulfillment Lambda includes `X-Internal-Token` header for internal PATCH calls.

## 7. Reliability and Idempotency

- Fulfillment consumer idempotency: event claim in DynamoDB prevents duplicate shipment processing.
- Status update idempotency: event IDs are passed to order update logic for dedupe.
- SQS redrive policy routes repeatedly failing messages to DLQ.

## 8. Deployment View

- Infrastructure defined in `template.yaml`.
- Build and deploy performed via SAM (`deploy-lambda.sh` in local workflow).
- Environment-specific resources use `${Environment}` suffixes.

## 9. Operational Considerations

- Logs: CloudWatch logs for both Lambda functions.
- Health endpoint: `/health` exposed by Order Service API.
- Validation scripts (local-only) support smoke tests post-deploy.

## 10. Risks and Known Tradeoffs (POC)

- Internal token is static unless overridden per environment.
- Public Swagger is useful for demo, but should be further restricted in production.
- Order payload is stored as serialized JSON in DynamoDB for simplicity over query flexibility.

## 11. Success Criteria

The architecture is considered successful for POC when:

- Orders can be created and retrieved through public API.
- Fulfillment updates status asynchronously through SQS and internal route.
- Duplicate/replayed events do not produce duplicate effective state transitions.
- Deployment is repeatable via SAM with environment-based configuration.

--- 

# Order Processing POC - Low Level Design (LLD)

## 1. Purpose

This document defines the implementation-level design for the Lambda-based Order Processing POC. It maps runtime behavior to concrete code modules, request contracts, event schemas, and operational procedures.

## 2. Code-Level Component Map

### Order Service API Host (Lambda)

- Entry point: `lambda/OrderService.Lambda/Program.cs`
- Controller: `order-service/OrderService.Api/Controllers/OrderController.cs`
- Service: `order-service/OrderService.Service/Services/OrderService.cs`
- Repository: `order-service/OrderService.Infrastructure/Repositories/DynamoDbOrderRepository.cs`
- Publisher: `lambda/OrderService.Lambda/Infrastructure/SqsEventPublisher.cs`

### Fulfillment Service (Lambda)

- Handler: `lambda/FulfillmentService.Lambda/FulfillmentHandler.cs`
- Shipping provider: `lambda/FulfillmentService.Lambda/Services/MockShippingProvider.cs`
- Idempotency store (shared): `Shared/LambdaMigration.Shared/Idempotency/DynamoDbIdempotencyStore.cs`

### Shared Contracts

- Event: `Shared/LambdaMigration.Shared/Events/OrderCreatedEvent.cs`
- DTO model pieces: `Shared/LambdaMigration.Shared/Models/*`

## 3. API Contracts

Base route prefix: `/api/v1/orders`

### 3.1 Create Order

- Route: `POST /api/v1/orders`
- Auth: Cognito Bearer token (API Gateway authorizer)
- Request body:
    
    ![](http://localhost:63342/markdownPreview/920543691/docs)
    
    `{   "customerId": "cust-1",   "customerName": "Alice",   "items": [    { "productId": "sku-1", "quantity": 1 }  ] }`
    
- Response (`200`):
    
    ![](http://localhost:63342/markdownPreview/920543691/docs)
    
    `{   "orderId": "guid",   "status": "Created" }`
    

### 3.2 Get Order

- Route: `GET /api/v1/orders/{orderId}`
- Auth: Cognito Bearer token
- Response (`200`):
    
    ![](http://localhost:63342/markdownPreview/920543691/docs)
    
    `{   "orderId": "guid",   "status": "Created|Pending|Processing|Shipped|Failed",   "fulfillment": {    "trackingNumber": "string|null",     "carrier": "string|null",     "shippedAt": "ISO8601|null",     "errorMessage": "string|null"   } }`
    

### 3.3 Manual Status Update (Swagger-visible)

- Route: `PATCH /api/v1/orders/{orderId}/status`
- Auth: Cognito Bearer token
- Request body (`UpdateOrderStatusRequestDto`):
    
    ![](http://localhost:63342/markdownPreview/920543691/docs)
    
    `{   "status": "Created",   "trackingNumber": null,   "carrier": null,   "shippedAt": null,   "errorMessage": null }`
    
- Allowed `status` enum values:
    - `Created`
    - `Pending`
    - `Processing`
    - `Shipped`
    - `Failed`
- Response: `204` on success, `404` if order does not exist.

### 3.4 Internal Status Update (hidden from Swagger)

- Route: `PATCH /api/v1/orders/{orderId}/status/internal`
- Auth: `X-Internal-Token` header must match `INTERNAL_TOKEN` env var.
- Optional dedupe header: `X-Event-Id`.
- Response: `204` on success, `401` when token is missing/invalid.

## 4. Message Contract (SQS)

### OrderCreatedEvent payload

![](http://localhost:63342/markdownPreview/920543691/docs)

`{   "eventType": "OrderCreated",   "eventId": "guid",   "orderId": "guid",   "customer": {    "customerId": "string",     "customerName": "string"   },   "items": [    { "productId": "string", "quantity": 1 }  ],   "createdAt": "ISO8601",   "correlationId": "string" }`

Published by `SqsEventPublisher` to `order-events-{env}` queue.

## 5. Processing and State Transitions

### 5.1 Order Creation Path

1. Controller receives create request.
2. Service validates request and builds domain `Order`.
3. Repository persists order (`orders-{env}` table).
4. Event publisher enqueues `OrderCreatedEvent`.

### 5.2 Fulfillment Path

1. SQS triggers fulfillment Lambda.
2. Handler deserializes event and runs idempotency claim.
3. Handler patches internal status route to `Processing` using event suffix key (`#processing`).
4. Shipping provider returns success/failure.
5. Handler patches internal status route to `Shipped` or `Failed` using unique event suffix key.

## 6. Idempotency Design

### 6.1 Consumer idempotency

- Store: `order-idempotency-{env}` DynamoDB table.
- Key: event ID.
- Operation: conditional `PutItem` with `attribute_not_exists(EventId)`.
- TTL (`ExpiresAt`) removes stale claims.

### 6.2 Status update idempotency

- Order service repo method `TryMarkEventProcessedAsync` stores namespaced event IDs.
- Internal header `X-Event-Id` used to avoid duplicate status transitions.
- Fulfillment uses suffixed event IDs (`#processing`, `#shipped`, `#failed`) so sequential transitions are not blocked.

## 7. Persistence Model

### Orders table item

- Partition key: `OrderId`
- Payload field: serialized full `Order` JSON in `Payload`

Tradeoff: simplified implementation for POC over query-optimized schema.

## 8. Security Details

- Public API routes protected by Cognito authorizer.
- Internal status route excluded from Swagger and guarded by shared secret header.
- Security headers and rate limiting are enabled in API pipeline.

## 9. Deployment Design

Defined in `template.yaml`:

- `AWS::Serverless::Function`: `OrderServiceFunction`, `FulfillmentServiceFunction`
- `AWS::SQS::Queue`: main queue + DLQ
- `AWS::DynamoDB::Table`: orders + idempotency
- `AWS::Serverless::HttpApi`: route definitions, Cognito authorizer, internal route override

## 10. Observability and Operations

- CloudWatch logs for both Lambdas.
- `/health` endpoint for service checks.
- DLQ depth check used for failure monitoring.

## 11. Runbook (POC)

### Common smoke checks

1. Get Bearer token and call `POST /orders`.
2. Verify `GET /orders/{id}` returns `Created` then transitions asynchronously.
3. Test manual patch with `PATCH /orders/{id}/status` in Swagger.
4. Verify internal route rejects requests without `X-Internal-Token`.

### Common failure diagnostics

- `401 Missing or invalid X-Internal-Token`: internal route called without valid token.
- Order stuck in `Processing`: inspect fulfillment logs and internal PATCH responses.
- DLQ growth: inspect failed messages and replay strategy.

## 12. Testing Coverage Snapshot

- `lambda/Lambda.Tests/FulfillmentHandlerTests.cs`: fulfillment behavior, mixed-batch failures, duplicates.
- `lambda/Lambda.Tests/InMemoryIdempotencyStoreTests.cs`: idempotency store behavior and concurrency.
- `order-service/OrderService.Tests/*`: repository and service-level behavior.
