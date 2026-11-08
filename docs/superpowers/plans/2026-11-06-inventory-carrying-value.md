# Inventory carrying-value checkpoint (P1, unresolved)

Working branch: `fix/security-first-admin-20261105`, draft PR #24.
Last verified code commit: `fe7741b1923758d126604e5657bae10bb0544524`.
Windows CI: https://github.com/omarnet0109-jpg/OfflineStore/actions/runs/42228935696
213 Python tests, 63 domain checks, native financial/return/migration/recovery/location scenarios, WPF x86/runtime and both installer preservation checks passed on that code commit. Artifact uploads failed due to repository artifact storage quota; no artifacts were deleted. Physical Windows 10 and printer testing remain unperformed.

## Confirmed defect; do not mark ready

`StoreService.Post` independently calculates `Amounts.CostTotal(line.QuantityMilli, productCosts[line.ProductId])` for every sale line (approximately lines 464–469). Selling does not maintain an exact per-product remaining carrying amount. `ApplyMovingAverageCost` derives new values from a rounded unit average.

Reproduction on an isolated activated database:
1. Purchase one base unit at 100 cents.
2. Purchase two base units at zero cents (supported purchase input).
3. The three units have average 33333 millicents and total booked inventory 100 cents.
4. Sell each unit separately: each snapshot/COGS is 33 cents.
5. Quantity reaches zero, but INVENTORY retains 1 cent (100 − 99). Splitting quantities into separate lines can also change total COGS.

A second precision issue: 1018 base units with total receipt cost 28 cents round to unit average 28 millicents, whose rounded extended value is 29 cents. Merely subtracting rounded before/after average-based values cannot guarantee agreement with booked receipt costs.

Independent read-only review confirmed both calculations. No fix has been implemented for this defect. The execution environment became unavailable immediately after investigation; all earlier stable code is already on GitHub.

## Resume safely

1. Read actual PR HEAD and this checkpoint; do not redo schema020–022 work.
2. Add a failing native C# regression for both reproductions, split lines and full/partial returns, using temporary databases only.
3. Design an additive exact per-product carrying-value ledger (quantity + total cents) as authoritative; use rounded unit average for display/compatibility. Allocate outgoing carrying cost proportionally from remaining quantity/value and exhaust the full remaining value on final depletion.
4. Preserve every historical sale/return cost snapshot. Add explicit valuation evidence/method for future snapshots; update integrity/backup checks and runtime smoke assertions for the new method.
5. Route purchases, goods receipts, sales and both return types through one transaction-aware valuation implementation. Sale returns restore original snapshot cost; purchase returns consume current carrying value with original-price variance retained separately.
6. Seed existing inventory only through a reviewed compatible migration. If existing inventory book and valuation disagree, preserve history, require explicit review and post a documented adjustment; never silently rewrite old COGS, opening balances or invoices.
7. Test overflow, fractional quantities, grouped lines, crash rollback, concurrent posting and migrations from 9/10/11/20/21/22. Commit/publish stable stages; verify latest Windows CI. Keep PR draft; no merge/release.

An alternative rounding-reserve adjustment was considered but NOT implemented: it preserves average-based COGS policy and can leave residual rounding expense over a sale/return cycle. Do not present it as exact original carrying-cost conservation.

Other explicit limitations remain: legacy invoices lacking cost/payment evidence require reviewed migration; tax mode is nonrecoverable capitalized input tax, not configured recoverable VAT; downloadable new installers are blocked by artifact quota.
