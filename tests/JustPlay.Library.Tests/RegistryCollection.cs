using Xunit;

namespace JustPlay.Library.Tests;

/// <summary>
/// Test classes that redirect <see cref="LibraryIndexRegistry.Location"/> run one at a time.
///
/// <para>The registry answers a question about the MACHINE, so it is a settable static rather than
/// an injected service - a deliberate choice, and the right one for three apps that must not
/// disagree about which folders are indexed. The cost is that two test classes pointing it at two
/// different temp files at the same time will read each other's, which xUnit's default per-class
/// parallelism makes a coin toss.</para>
///
/// <para>A named collection is the narrow fix: only these classes serialise, and the other 190-odd
/// Library tests keep running in parallel.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class RegistryCollection
{
    public const string Name = "LibraryIndexRegistry";
}
