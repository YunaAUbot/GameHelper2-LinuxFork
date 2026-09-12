# Upstream sync repair — 2026-09-12

The scheduled run 34681894816 failed on conflicts in LootValue and NinjaPricer.
Git's rename detection mapped the removed embedded LootValue fetcher onto the
shared NinjaPricer implementation. The sync also incorrectly treated the entire
LootValue tree as upstream-owned and restored it even on no-op runs.

Resolution:

- Integrated the previously local shared-provider adaptation (270ee5c), preserving
  the current remote Radar implementation, and reviewed upstream through 330df7a.
- Ported f65c557's dedicated Scout API endpoint into canonical NinjaPricer and its
  vendored copy. Existing bounded JSON parsing already rejects HTML responses;
  a regression test now verifies fallback and the dedicated host.
- Accepted upstream's controller map-parent offset and 2.7.5 version update.
- Disabled merge rename inference; removed unconditional LootValue restoration
  and automatic whole-plugin conflict resolution. Conflicts list their paths and
  leave both remote refs unchanged. Successful merges still push code and the
  upstream snapshot atomically, including rewritten-history handling.
- Run local Git integration tests before each scheduled sync, protect the workflow
  itself from upstream changes, and bound the job to 15 minutes.

Validation: 47 provider tests in canonical and vendored copies; importer layout;
full Linux solution build and separate package build; Git integration scenarios
for no-op/repeated sync, clean updates, LootValue conflicts, reviewed adaptation,
ordinary conflicts, upstream history rewrite, protected-file deletion and the
extracted-fetcher rename regression.

This is not an automatic resolution of arbitrary semantic conflicts. Future
upstream changes that overlap fork-specific code still need a reviewed adaptation.
Neither the running installation nor uncommitted plugin fixes in the primary
working directory are replaced. The validation package is at
/home/auron/PoE2/upstream-sync-validation-20260912.
