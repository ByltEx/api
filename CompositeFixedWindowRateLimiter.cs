using System.Threading.RateLimiting;

namespace ByltEx.Api;

/// <summary>
/// Enforces multiple fixed-window limits; a request is allowed only if every window has capacity.
/// </summary>
internal sealed class CompositeFixedWindowRateLimiter : RateLimiter
{
    private readonly FixedWindowRateLimiter[] _limiters;
    private int _disposed;

    public CompositeFixedWindowRateLimiter(IEnumerable<FixedWindowRateLimiterOptions> windows)
    {
        _limiters = windows
            .Select(options => new FixedWindowRateLimiter(options))
            .ToArray();
    }

    public override RateLimiterStatistics? GetStatistics() => null;

    public override TimeSpan? IdleDuration => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var acquired = new List<RateLimitLease>(_limiters.Length);
        foreach (var limiter in _limiters)
        {
            var lease = limiter.AttemptAcquire(permitCount);
            if (!lease.IsAcquired)
            {
                foreach (var acquiredLease in acquired)
                {
                    acquiredLease.Dispose();
                }

                return lease;
            }

            acquired.Add(lease);
        }

        return new CompositeLease(acquired);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        return AcquireAllAsync(permitCount, cancellationToken);
    }

    private async ValueTask<RateLimitLease> AcquireAllAsync(int permitCount, CancellationToken cancellationToken)
    {
        var acquired = new List<RateLimitLease>(_limiters.Length);
        foreach (var limiter in _limiters)
        {
            var lease = await limiter.AcquireAsync(permitCount, cancellationToken).ConfigureAwait(false);
            if (!lease.IsAcquired)
            {
                foreach (var acquiredLease in acquired)
                {
                    acquiredLease.Dispose();
                }

                return lease;
            }

            acquired.Add(lease);
        }

        return new CompositeLease(acquired);
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing)
        {
            foreach (var limiter in _limiters)
            {
                limiter.Dispose();
            }
        }
    }

    private sealed class CompositeLease(List<RateLimitLease> leases) : RateLimitLease
    {
        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames =>
            leases.SelectMany(lease => lease.MetadataNames).Distinct();

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            var retryAfter = TimeSpan.Zero;
            var found = false;

            foreach (var lease in leases)
            {
                if (!lease.TryGetMetadata(metadataName, out var value))
                {
                    continue;
                }

                found = true;
                if (metadataName == MetadataName.RetryAfter.Name && value is TimeSpan retry)
                {
                    if (retry > retryAfter)
                    {
                        retryAfter = retry;
                    }
                }
                else
                {
                    metadata = value;
                    return true;
                }
            }

            if (found && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            return found;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }

            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }
}
