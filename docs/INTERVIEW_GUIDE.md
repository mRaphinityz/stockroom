# Make this a project you can explain

The useful resume signal is your ability to demonstrate behavior, explain a decision, and change the code. Read the implementation, run the failure cases, and make a contribution you can walk through without a script.

## A focused learning path

1. **Trace a request.** Start at the order form in `wwwroot/app.js`, follow `POST /api/orders` in `Endpoints/ApiEndpoints.cs`, and read `InventoryService.Reserve`. Identify exactly where a failure rolls back stock.
2. **Run a boundary case.** Create a product with one unit, then run the competing-order test. Explain why reading the count and saving a new count without a database condition can oversell.
3. **Compare two kinds of conflicts.** A reused idempotency key protects retries. A stale version protects manual edits. Explain why those solve different problems.
4. **Extend one workflow yourself.** A good first contribution is partial returns: define how many units may be returned, preserve the original fulfilled order, record the returned units, and test over-return and repeat-request failures.
5. **Record a demo.** Show a reservation, fulfillment, rejected oversell, activity log, and viewer account in under three minutes.

## Questions to practice

| Interview question | What to be ready to show |
| --- | --- |
| How do you prevent two orders taking the last item? | Conditional update, affected-row check, transaction, and the concurrent test |
| What if the third product is unavailable? | Earlier updates and events roll back with the transaction |
| What if the browser loses the response? | Reuse the same key and payload; show the original order ID returned |
| Why not store money as a floating-point number? | Integer cents and historical order-line price snapshots |
| Can a viewer bypass the UI? | Server-side role policy and the 403 integration test |
| Why use an antiforgery token with cookies? | The browser sends cookies automatically; writes need a token tied to the session |
| Why SQLite? When would you replace it? | Simple local setup, real relational tests, serialized writers, provider-specific concurrency review |
| What would fail at 100,000 orders? | Materialized dashboard reads and catalog loading; show how you would move aggregation and filtering to SQL |
| Is this multi-tenant? | No. Everyone shares one workspace; true tenant isolation needs a different authorization/data model |
| What did you personally contribute? | Name the feature, tradeoff, bug, and test you understand. Be accurate about tools and assistance |

## Resume wording to adapt

Use this after you can explain the implementation and have made it your own:

> Built a full-stack inventory and order-management application with C#, ASP.NET Core, EF Core, and SQLite; implemented transactional stock reservations, idempotent order creation, role-based access, and an audit trail, verified through integration and browser tests.

Keep the resume entry to one or two bullets with a repository link and, when available, a deployed demo. Add measured results only after collecting them. The repository includes correctness tests; it does not establish production usage, performance improvements, or business impact.

## Suggested next feature: partial returns

Write a small issue before coding:

- A fulfilled order can have some units returned.
- Total returned quantity cannot exceed the fulfilled quantity for each line.
- A repeated return request does not add stock twice.
- Each successful return increases on-hand stock and records the authenticated actor.
- Failed multi-line returns save nothing.

This builds on the existing model while creating a design decision and test story that are yours to discuss.
