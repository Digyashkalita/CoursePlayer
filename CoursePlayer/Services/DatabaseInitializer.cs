using CoursePlayer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace CoursePlayer.Services;

/// <summary>
/// Applies pending EF Core migrations at startup.
/// </summary>
public interface IDatabaseInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDatabaseInitializer"/>
public sealed class DatabaseInitializer : IDatabaseInitializer
{
    private readonly IDbContextFactory<CoursePlayerDbContext> _contextFactory;
    private readonly IAppPaths _paths;
    private readonly ILogger<DatabaseInitializer> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public DatabaseInitializer(
        IDbContextFactory<CoursePlayerDbContext> contextFactory,
        IAppPaths paths,
        ILogger<DatabaseInitializer> logger)
    {
        _contextFactory = contextFactory;
        _paths = paths;
        _logger = logger;

        // The db file can be briefly locked by a previous instance shutting down, or by
        // a virus scanner touching a freshly created file.
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<SqliteException>()
                    .Handle<IOException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(400),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Migration attempt failed, retrying ({Attempt}).",
                        args.AttemptNumber + 1);
                    return default;
                },
            })
            .Build();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();

        await _retryPipeline.ExecuteAsync(async token =>
        {
            await using var context = await _contextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);

            var pending = (await context.Database
                .GetPendingMigrationsAsync(token)
                .ConfigureAwait(false)).ToArray();

            if (pending.Length == 0)
            {
                _logger.LogInformation("Database schema is current at {Path}.", _paths.DatabasePath);
                return;
            }

            _logger.LogInformation(
                "Applying {Count} migration(s): {Migrations}",
                pending.Length,
                string.Join(", ", pending));

            await context.Database.MigrateAsync(token).ConfigureAwait(false);

            _logger.LogInformation("Database ready at {Path}.", _paths.DatabasePath);
        }, cancellationToken).ConfigureAwait(false);
    }
}
