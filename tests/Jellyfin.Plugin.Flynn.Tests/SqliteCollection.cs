using Xunit;

namespace Jellyfin.Plugin.Flynn.Tests;

/// <summary>
/// Every test class that opens a Flynn database belongs here, and they never run at the same time.
/// <para>
/// Each such class calls <c>SqliteConnection.ClearAllPools()</c> when it tears down, so that
/// Windows will let it delete its temporary directory. That call is <b>process-wide</b>: it closes
/// pooled connections belonging to every other test running at that moment, not just its own. With
/// xUnit's default parallelism across classes, one class finishing could take a connection out from
/// under another mid-query.
/// </para>
/// <para>
/// It surfaced the way this kind of thing always does -- two unrelated tests failing together in a
/// full run and passing on their own. Serialising them is the fix rather than removing the pool
/// cleanup, because without it the temporary databases stay locked and the directories leak.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    /// <summary>The collection name, referenced by every database-backed test class.</summary>
    public const string Name = "flynn-sqlite";
}
