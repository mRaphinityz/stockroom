# API walkthrough

The app serves a generated OpenAPI document at `/openapi/v1.json` after sign-in. The API uses JSON, Identity cookies, and an antiforgery header. The following examples use a **local Development instance** with demo accounts.

## Sign in from a terminal

These examples use `curl` and Python 3 to extract JSON values. Keep the temporary cookie file private and delete it when finished.

```sh
base_url=http://localhost:5080
cookie_file=$(mktemp)
csrf_token=$(curl -sS -c "$cookie_file" "$base_url/api/session" |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["csrfToken"])')

curl -sS -b "$cookie_file" -c "$cookie_file" \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf_token" \
  -d '{"email":"manager@stockroom.local","password":"Demo.Manager42!"}' \
  "$base_url/api/auth/login"

# Refresh the token after signing in: it is now bound to this identity.
csrf_token=$(curl -sS -b "$cookie_file" -c "$cookie_file" "$base_url/api/session" |
  python3 -c 'import json,sys; print(json.load(sys.stdin)["csrfToken"])')

curl -sS -b "$cookie_file" "$base_url/api/products?page=1&pageSize=10"
```

## Create a product

SKU values are normalized to uppercase. Price is in USD cents. Stock and reorder point must be between 0 and 1,000,000; price must be between 0 and 100,000,000 cents.

```sh
curl -sS -b "$cookie_file" \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf_token" \
  -d '{"sku":"DEMO-KEYBOARD","name":"Demo keyboard","priceCents":12999,"onHand":5,"reorderPoint":2}' \
  "$base_url/api/products"
```

Save the returned product `id` as `product_id` for the following requests. Reusing the same SKU returns 409.

## Reserve an order

```sh
# Substitute the product ID from the previous response.
product_id=YOUR_PRODUCT_ID
curl -sS -i -b "$cookie_file" \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf_token" \
  -H 'Idempotency-Key: terminal-order-001' \
  -d "{\"customer\":\"Northstar Studio\",\"lines\":[{\"productId\":\"$product_id\",\"quantity\":2}]}" \
  "$base_url/api/orders"
```

The first request receives 201 and an order ID. Repeat it unchanged with the same key: it receives 200, `Idempotent-Replayed: true`, and the same order ID. Stock is reserved once. Changing its quantity while retaining the key returns 409. Use a new key for a new order.

An order can contain 1–25 distinct products, each with quantity 1–10,000. Duplicate product lines are rejected. An unavailable item causes the entire reservation to roll back.

## Fulfill or cancel

```sh
order_id=YOUR_ORDER_ID
curl -sS -b "$cookie_file" -X POST -H "X-CSRF-TOKEN: $csrf_token" \
  "$base_url/api/orders/$order_id/fulfill"

# For a different reserved order, cancel instead:
# curl -sS -b "$cookie_file" -X POST -H "X-CSRF-TOKEN: $csrf_token" \
#   "$base_url/api/orders/$order_id/cancel"
```

Only reserved orders accept either operation. Fulfillment reduces both on-hand and reserved counts. Cancellation reduces reserved counts and keeps physical stock unchanged. Repeating either terminal action returns 409.

## Adjust stock

Read the product first and use its current `version` as `expectedVersion`.

```sh
curl -sS -b "$cookie_file" "$base_url/api/products/$product_id"

# Substitute the version from that response.
curl -sS -b "$cookie_file" \
  -H 'Content-Type: application/json' -H "X-CSRF-TOKEN: $csrf_token" \
  -d '{"delta":10,"reason":"Supplier delivery","expectedVersion":1}' \
  "$base_url/api/products/$product_id/adjustments"
```

A stale version returns 409. Negative adjustments cannot reduce on-hand stock below the reserved count. A successful adjustment increments the version and creates an activity event.

## Endpoint reference

| Method | Path | Access |
| --- | --- | --- |
| GET | `/api/session` | Public; current identity and CSRF token |
| POST | `/api/auth/login` | Public; CSRF token and login rate limit |
| POST | `/api/auth/logout` | Signed in |
| GET | `/api/dashboard` | Signed in |
| GET | `/api/products?search=&page=1&pageSize=100` | Signed in |
| GET | `/api/products/{id}` | Signed in |
| POST | `/api/products` | Manager |
| POST | `/api/products/{id}/adjustments` | Manager |
| GET | `/api/orders?status=Reserved&page=1&pageSize=50` | Signed in |
| GET | `/api/orders/{id}` | Signed in |
| POST | `/api/orders` | Manager; idempotency key required |
| POST | `/api/orders/{id}/fulfill` | Manager |
| POST | `/api/orders/{id}/cancel` | Manager |
| GET | `/api/activity?page=1&pageSize=50` | Signed in |
| GET | `/openapi/v1.json` | Signed in |
| GET | `/health` | Public; process liveness |
| GET | `/health/ready` | Public; database connectivity |

All `/api` writes require an antiforgery token. List responses include `items`, `total`, `page`, and `pageSize`; page size is clamped to 1–100. Error responses use Problem Details when available: 400 for validation, 401 for an unauthenticated request, 403 for a missing role, 404 for a missing resource, 409 for a business conflict, and 429 for login rate limiting.

## Sign out and clean up

```sh
curl -sS -b "$cookie_file" -X POST -H "X-CSRF-TOKEN: $csrf_token" \
  "$base_url/api/auth/logout"
rm "$cookie_file"
```
