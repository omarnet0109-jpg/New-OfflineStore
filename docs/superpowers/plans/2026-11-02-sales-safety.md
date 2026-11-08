# Sales safety repair plan

Goal: fix reproducible ledger and barcode defects without adding product features.
Scope: OfflineStore main b3d9c47, WPF/.NET Framework 4.8, SQLite; existing data must remain intact.

- [ ] Reproduce direct POSTED insert, incorrect line amount, and draft identity mutation using real SQLite regression tests.
- [ ] Add migration 005 with insert/state/amount guards; wire automatic pre-upgrade backup and embedded resource. Do not rewrite historical invoices.
- [ ] Reproduce corrupt barcode handling; discard invalid frames until the next delimiter. Clear stale selection when lookup fails.
- [ ] Run full Python suite and Windows CI C# tests/build. Document runtime/hardware limitations and existing missing features.
- [ ] Publish one focused GitHub branch and PR; preserve main and bootstrap archive.

Review focus: exact half-cent rounding, large integer prices, rollback after invalid SQL, preservation of old ledger and users, scanner recovery after malformed input.
