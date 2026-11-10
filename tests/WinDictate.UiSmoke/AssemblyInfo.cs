using Xunit;

// These tests share one desktop: the keyboard focus, the clipboard and the
// test switches on IndicatorTheme. Running classes side by side would make
// them trip over each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
