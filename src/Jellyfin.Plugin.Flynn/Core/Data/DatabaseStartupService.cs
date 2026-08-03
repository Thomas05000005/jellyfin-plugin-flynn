using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Flynn.Core.Data;

/// <summary>
/// Brings the schema up to date once, while the server starts.
/// <para>
/// Doing it here rather than lazily on first use means a broken schema is reported at boot, in the
/// server log, instead of surfacing much later as an odd failure inside whichever module happened
/// to touch storage first.
/// </para>
/// </summary>
public sealed class DatabaseStartupService : IHostedService
{
    private readonly SchemaMigrator _migrator;
    private readonly DatabaseReadiness _readiness;
    private readonly ILogger<DatabaseStartupService> _logger;

    /// <summary>Initializes a new instance of the <see cref="DatabaseStartupService"/> class.</summary>
    /// <param name="migrator">The migrator.</param>
    /// <param name="readiness">Where the outcome is recorded.</param>
    /// <param name="logger">Logger.</param>
    public DatabaseStartupService(
        SchemaMigrator migrator,
        DatabaseReadiness readiness,
        ILogger<DatabaseStartupService> logger)
    {
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _readiness = readiness ?? throw new ArgumentNullException(nameof(readiness));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var version = await _migrator.MigrateAsync(Migrations.All, cancellationToken).ConfigureAwait(false);
            _readiness.MarkReady();
            _logger.LogInformation("Flynn database ready at schema version {Version}.", version);
        }
#pragma warning disable CA1031 // A plugin must never prevent the server from starting.
        catch (Exception ex)
        {
            _readiness.MarkFailed("The Flynn database could not be opened or migrated.");
            _logger.LogError(
                ex,
                "Flynn could not bring its database up. Modules that need storage will report as "
                + "unavailable; the rest of the server is unaffected.");
        }
#pragma warning restore CA1031
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
