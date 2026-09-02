using CoursePlayer.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace CoursePlayer.Services;

/// <summary>
/// Runs every write against the database inside a transaction, retrying transient
/// SQLite lock contention. Callers get all-or-nothing semantics: a crash or exception
/// part-way through a multi-entity write (an import, say) leaves the file untouched.
/// </summary>
public interface IDatabaseWriter
{
    /// <summary>Runs <paramref name="work"/> in a transaction and commits, or rolls back on failure.</summary>
    Task ExecuteAsync(
        Func<CoursePlayerDbContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="ExecuteAsync(Func{CoursePlayerDbContext, CancellationToken, Task}, CancellationToken)"/>
    Task<TResult> ExecuteAsync<TResult>(
        Func<CoursePlayerDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Read-only access. No transaction, no retry-on-write semantics.</summary>
    Task<TResult> QueryAsync<TResult>(
        Func<CoursePlayerDbContext, CancellationToken, Task<TResult>> query,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDatabaseWriter"/>
public sealed class DatabaseWriter : IDatabaseWriter
{
    private readonly IDbContextFactory<CoursePlayerDbContext> _contextFactory;
    private readonly ILogger<DatabaseWriter> _logger;
    private readonly ResiliencePipeline _retryPipeline;

    public DatabaseWriter(
        IDbContextFactory<CoursePlayerDbContext> contextFactory,
        ILogger<DatabaseWriter> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;

        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<SqliteException>(IsTransient),
                MaxRetryAttempts = 4,
                Delay = TimeSpan.FromMilliseconds(150),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Database busy, retry {Attempt} in {Delay}.",
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    return default;
                },
            })
            .Build();
    }

    public Task ExecuteAsync(
        Func<CoursePlayerDbContext, CancellationToken, Task> work,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async (context, token) =>
        {
            await work(context, token).ConfigureAwait(false);
            return null;
        }, cancellationToken);

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CoursePlayerDbContext, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default)
    {
        return await _retryPipeline.ExecuteAsync(async token =>
        {
            await using var context = await _contextFactory
                .CreateDbContextAsync(token)
                .ConfigureAwait(false);

            await using var transaction = await context.Database
                .BeginTransactionAsync(token)
                .ConfigureAwait(false);

            try
            {
                var result = await work(context, token).ConfigureAwait(false);
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex)
            {
                // Roll back explicitly and log; disposing would roll back anyway, but we
                // want the failure recorded even when the rollback itself misbehaves.
                _logger.LogError(ex, "Database write failed; rolling back.");
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Rollback failed.");
                }

                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TResult> QueryAsync<TResult>(
        Func<CoursePlayerDbContext, CancellationToken, Task<TResult>> query,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        return await query(context, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransient(SqliteException exception) =>
        exception.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */;
}
