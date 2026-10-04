namespace Orion.Tests;

// Avalonia's platform/dispatcher are process-wide even with isolated test sessions.
[CollectionDefinition("Avalonia UI", DisableParallelization = true)]
public sealed class UiTestCollection;
