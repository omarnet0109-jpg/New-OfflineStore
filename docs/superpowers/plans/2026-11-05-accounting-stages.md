# Accounting implementation stages

Scope: Windows 10, x86, .NET Framework 4.8, SQLite; no automatic historical financial backfill.

1. Foundation: additive journal schema, integer piastres, balanced atomic posting, immutable posted entries, unique source keys; administrator-only manual/opening API. No automatic postings from existing invoices yet.
2. Counterparties and settlements: customer records, customer receipts and supplier payments, balances derived from entries, cash movements and idempotent document links in one SQLite transaction.
3. Returns: reference original invoice lines; cumulative returned quantity cannot exceed the net original quantity; reverse original cost/tax/payment basis with corresponding stock movement.
4. Expenses and profits: expense/cash entries and reports using precise cost snapshots; clearly distinguish legacy estimated costs.
5. Activation: explicit cutover and reconciled opening balances. Historical invoice totals do not establish historical cash collections or debts; never invent these balances.
6. Database location: separate checked move with closed connections and WAL handling; independent of the accounting rollout.

Foundation choice: a balanced journal is the shared money ledger. Separate unconnected cash/debt/expense totals would create reconciliation defects. Existing stock documents stay authoritative for quantities.

Checks per stage: failing behavior test, implementation, SQLite atomicity/immutability tests, Windows integration, full regression suite, commit. No main merge or release without approval.

Review focus: duplicate submission, integer overflow, missing counterparties, interruption before commit, and migration without changing historical amounts.

## Current continuation

- Agreed: overpayments become separate party advances; return cash/debt split is explicit; new purchase VAT is capitalized. Purchase-return stock uses current average with a separate return variance account (engineering decision delegated by owner).
- Stage 015 adds settlements, advances, categorized cash expenses, party statements and journal-only profit/trial reports. New cash/credit sales and receipts post finances in the SAME stock transaction after explicit activation. Old invoices are not backfilled.
- Opening and activation are atomic, including an explicit genuine zero opening. Do not split these steps; interruption must roll both back.
- Local checks: 194 Python tests passed (unchanged binary icon check unavailable locally). New C# integration/domain and WPF checks require Windows CI before declaring this batch validated.
- Remaining stages: allocation/application of advances, invoice-specific allocations and returns, database relocation, remaining report/receipt integration. Draft PR24; no main merge or release.

Relocation continuation: stable per-user pointer with target identity; stage/admin cancel; startup-only verified WAL-aware snapshot, resumable hash journal and atomic pointer; retain original DB/sidecars and backup settings. Managed opens revalidate target identity and fail if an established file disappears. New isolated Windows tests cover interruption and reconnect.
Windows ab9585b: C# finance/invoice/cutover/upgrade tests, WPF x86 build and 58 domain checks passed. Artifact upload failed because GitHub storage quota was exhausted; preview uploads must not prevent remaining safety/runtime checks. No artifacts were deleted.

Stage 016: explicit customer/supplier advance application, bounded by both current debt and advance; immutable metadata matches the balanced noncash journal. It never changes profit or cash. New SQLite and C# tests cover mismatch, overapplication and duplicate submission. Updated the pre-existing fixed screenshot-count assertion for the additional accounting screen. Returns and invoice allocation remain the next financial group; do not claim these complete.

Stage 017: explicit append-only allocation of recorded customer/supplier payments and advance applications to their financial invoices. Bounds enforce the same party, correct debt side, receipt amount and original invoice debt; unique reference prevents retries from allocating twice. No FIFO assumption, no inferred legacy debts. The UI distinguishes party balances from invoice balances awaiting allocation. Local: 199 Python checks passed; Windows tests for partial allocation, duplicate and over-allocation added. Returns remain unimplemented and must use these allocation bounds before reversing debt.

Cutover hardening: reviewed inventory must equal current recorded moving-average valuation. Existing foundation-only opening can be reconciled by an explicit difference adjustment inside the activation transaction; do not duplicate its balances or edit its history. UI preloads existing journal balances and requires confirmation of any adjustment. Zero opening remains supported. Prior income/expense-bearing ledgers need reviewed migration instead of guessed closure. PR workflow concurrency cancels obsolete PR runs to reduce unnecessary CI use.

Stage 018 implements sale/purchase returns and return receipts/history for financially linked invoices with precise cost evidence. Manual cash/debt split is bounded by original assigned payments, invoice remaining and actual party debt. Stock and avg, journal, allocations and audit share the transaction. Purchase return variance is separate from net sales in P&L. Tiny zero-money returns remain supported. Local203 Python passed; Windows regression run pending. Stage017/cutover/relocation head45296a2 completed full Windows CI42142313309 successfully.

Stage 019 closes cash-control gaps: payments, expenses, cash purchases and sale refunds cannot spend more than posted cash. Explicit owner capital funding provides a documented equity source without inventing sales or profit. Zero-cash noncash operations remain valid. New partial/credit sale capability is administrator-only; normal employee cash sales retain SALE permission. Tests cover empty cash, duplicate funding, refund after funding, employee rights and competing expenses.

Location hardening after review: a durable identity/phase guard blocks missing/stale pointer fallback to retained data; revalidate every managed open. Authenticated relink never copies an older snapshot over current data. Once the guarded destination is ready, cancellation cannot rewind to source, including when the destination is absent. Startup offers admin relink or explicit authenticated backup restore into a new empty folder; successful explicit restore gets a fresh identity and retires old locations. Added isolated lost-pointer, newer-target, disconnected-destination, missing-source-after-copy, denied-employee and alternate-restore tests. Stage019 head128760f passed full Windows CI42145304630; latest location group pending Windows verification.
