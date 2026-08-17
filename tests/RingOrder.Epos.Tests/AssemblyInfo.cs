using Xunit;

// Test classes run one at a time.
//
// Almost every class here tears down with SqliteConnection.ClearAllPools(),
// which is process-wide: run two classes at once and one class's teardown pulls
// pooled connections out from under another class mid-test. It surfaced as
// BundleImportTests failing roughly one run in ten while passing on its own —
// the worst shape of flake, because the test that fails is not the one at fault.
//
// The whole suite runs in about a second, so serialising costs nothing worth
// measuring. The deeper fix is for each class to clear only its own pool, which
// EposDb.Dispose already does correctly; the ClearAllPools calls beside it are
// collateral damage and can go whenever someone is in there.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
