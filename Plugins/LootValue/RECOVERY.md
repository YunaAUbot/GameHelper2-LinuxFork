# Installed source recovery and stash scan fix (2026-09-10)

The deployed LootValue.dll contained Ritual reward-grid support, incremental panel
scans and unique-specific pricing that were absent from this checkout. Its original
build source was unavailable. Production classes were reconstructed with ILSpy 11
using the deployed host assemblies to resolve types. A decompiler artifact in the
price/currency tuple was replaced with the equivalent typed tuple expression.
Existing assembly identity and host project references are retained.

Functional correction: classify scrollbar track and thumb geometry before reading
the proposed content rectangle. Ordinary panels can have hidden/zero-size content
children and wide decorative elements where the heuristic expects a scrollbar.
Previously that missing content rectangle produced Unavailable; three deferred
attempts then aborted the entire panel scan, so no initial stash snapshot appeared.
Now non-scroll layouts are rejected early without deferring their item traversal.
Unreadable plausible scroll containers still defer safely. Existing scrollbar offset
and clipping checks are preserved. Ritual grids still bypass scroll probing.

The recovered source retains ShowRitualOverlay, UniqueMinValueEx, separate Ritual
scan scheduling and shared NinjaPricer pricing. Source recovery is substantial in
diff size, so a build alone does not prove full behavioural equivalence. Validation:
full host/plugin solution build; 14 pricing/geometry/traversal/retention tests. Stash
and Ritual UI still need confirmation in the live game after restarting GameHelper.

Backups and validation logs are in the task's lootvalue-stash-fix directory under
PoE2. Use this restored source for future builds; do not deploy the earlier source
that lacks Ritual support. No pricing thresholds or user settings were changed.
