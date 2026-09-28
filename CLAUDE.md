# CodeCompass — project instructions

## ComputeWarden: gate intensive work

This project opts in to the ComputeWarden heavy-compute gate. Before starting any
machine-saturating operation, `computewarden_acquire` (owner, description, lease_seconds);
if it's not AVAILABLE (or status is UNKNOWN), don't start — report the blocker and ask
whether to wait/retry or proceed. `computewarden_release` when done (crash-safe via lease expiry).

Gate these CodeCompass operations (they peg most/all cores or sustain heavy disk I/O):
- `dotnet build -c Release` clean/parallel builds, and `dotnet test` of the full suite
- `check.ps1 -Big` and `make-release.ps1` (they generate 300 MB+ corpora, index them, and run parallel)
- a full `codecompass index` of a large tree
- `probe-repo.ps1` batteries (repeated index/refs over a big repo)

Do NOT gate ordinary work: single queries, small edits, targeted single-test runs, reads/searches.
