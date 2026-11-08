# Exact inventory carrying value — schema023

- Preserve schema020–022 and all historical cost/journal/return rows.
- Keep product quantity + remaining carrying cents authoritative; receipt adds actual allocated net cost. Rounded unit average is a compatibility/display value derived from total/quantity.
- Issue cost = rounded remaining value × issued quantity / remaining quantity; full depletion consumes exactly all remaining value. Persist immutable before-quantity/value evidence for new sale and purchase-return lines.
- Sales returns restore original invoice snapshot cost; purchase returns consume current carrying value and retain original-price variance.
- Migrate in one transaction, seeding legacy values from the previous valuation policy. Compare with active monetary inventory; any historic difference blocks stock mutation until administrator reviews and explicitly approves a dated/audited variance. Never silently change prior COGS or documents.
- One valuation implementation covers sales, legacy purchases, supply vouchers and both returns, in each source transaction. Validate quantities/evidence/journal agreement on startup and backup acceptance.
- Regression gates: repeated/split sales, large quantities/unit-average quantization, partial/full returns, zero-cost stock, injected failures, stale review, duplicate submission, service permissions, nonempty migrations and Windows x86 runtime.

Progress: native failing regression94af192 reproduced the residual money defect on Windows run42248212928. Implementation and isolated migration/rollback/grouping/returns/concurrency tests are now added; 215 local Python tests pass. Windows verification of the implementation is pending.

Windows implementation run42249856671 passed depletion/COGS checks, then exposed an existing zero-value purchase-return eligibility gap. Additive schema024 makes verified post-cutover zero-money sources returnable without inventing a journal or admitting legacy sources. SQL red regression captured before the fix. History validation streams grouped evidence with bounded memory; normal posting avoids replaying history. Windows verification pending.
