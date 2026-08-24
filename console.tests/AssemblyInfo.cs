using Xunit;

// The "UI Automation" collection (UiAutomationFixture.cs) drives a real WPF window through
// synthetic mouse/keyboard input timed against Thread.Sleep waits. xUnit parallelizes different
// test collections against each other by default; letting the ~330 fast, CPU-cheap unit tests in
// every other collection run concurrently with that collection was reproducibly starving the
// real app's UI thread of CPU under full-suite load, causing synthetic clicks to silently miss
// (confirmed: 100% reliable in isolation, reproducibly failing when run alongside the rest of the
// suite). Serializing the whole assembly removes the actual cause instead of adding more retries
// on top of a race the retries can't fully cover.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
