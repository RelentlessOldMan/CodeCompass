// Several tests mutate process-global state - most notably the CODECOMPASS_FORCE_NETWORK environment
// variable (NetworkPathTests, FileWalkerTests' retry tests, DiagnosticsTests' network-skip test) - which
// leaks across concurrently-running test classes. A network-forced env leaking into, say, the doctor
// unresolved-include test flips that code down its network path and fails it intermittently. Disable
// xUnit's cross-class parallelism so these tests can't race; the suite is fast enough that serial is fine.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
