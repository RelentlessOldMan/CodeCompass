# CodeCompass — project instructions

## ComputeWarden

Follow the global ComputeWarden policy. Project specifics:
- Solo (timing runs, ≤10 min each): `bench-matrix.ps1` and `probe-repo.ps1` timing runs; split a longer battery into measured runs of at most 10 minutes.
- Big memory: no CodeCompass job needs `bigMemory` (indexing an 88 GB tree peaks at ~3.8 GB).
