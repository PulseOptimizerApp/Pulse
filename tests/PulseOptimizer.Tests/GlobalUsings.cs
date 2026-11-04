global using System.IO;

// Translation tests swap the app-wide translation table, so tests run one at a time.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
