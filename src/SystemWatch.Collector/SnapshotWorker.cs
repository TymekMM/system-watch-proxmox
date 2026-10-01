using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Independent read-only probe functions; live Linux adapters are wired separately.</summary>
public sealed record SnapshotProbeSet(
    Func<string, CancellationToken, Task<ZfsInventoryResult>> Zfs,
    Func<CancellationToken, Task<SystemMetrics>> System,
    Func<string, CancellationToken, Task<SensorMeasurements>> Ipmi,
    Func<string, CancellationToken, Task<SensorMeasurements>> Hba,
    Func<string, CancellationToken, Task<HwmonPlatformResult>> PlatformHwmon,
    Func<IReadOnlyList<Device>, string, CancellationToken, Task<List<Temperature>>> NvmeHwmon,
    Func<IReadOnlyList<Device>, string, CancellationToken, Task<DiskTemperatureResult>> Smart);

/// <summary>One publisher and seven independently timed caches for a single process.</summary>
public sealed class SnapshotWorker
{
    private readonly SemaphoreSlim tickGate = new(1, 1);
    private readonly ISourceClock clock;
    private readonly AtomicSnapshotPublisher publisher;
    private readonly SnapshotAssembler assembler;
    private readonly SourceCache<ZfsInventoryResult> zfs;
    private readonly SourceCache<SystemMetrics> system;
    private readonly SourceCache<SensorMeasurements> ipmi;
    private readonly SourceCache<SensorMeasurements> hba;
    private readonly SourceCache<HwmonPlatformResult> platform;
    private readonly SourceCache<List<Temperature>> nvme;
    private readonly SourceCache<DiskTemperatureResult> smart;

    public SnapshotWorker(string hostname, ISourceClock clock, SnapshotProbeSet probes,
        AtomicSnapshotPublisher publisher, string? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        ArgumentNullException.ThrowIfNull(probes);
        assembler = new SnapshotAssembler(hostname, instanceId ?? Guid.NewGuid().ToString(), clock);
        zfs = new("zfs", "zfs", TimeSpan.FromSeconds(1),
            token => probes.Zfs(clock.UtcNow, token), clock);
        system = new("system", "system", TimeSpan.FromSeconds(1), probes.System, clock);
        ipmi = new("ipmi", "ipmi", TimeSpan.FromSeconds(5),
            token => probes.Ipmi(clock.UtcNow, token), clock);
        hba = new("hba:perccli", "perccli", TimeSpan.FromSeconds(2),
            token => probes.Hba(clock.UtcNow, token), clock);
        platform = new("hwmon:platform", "hwmon", TimeSpan.FromSeconds(1),
            token => probes.PlatformHwmon(clock.UtcNow, token), clock,
            result => result.Source);
        nvme = new("hwmon:nvme", "hwmon", TimeSpan.FromSeconds(1),
            token => probes.NvmeHwmon(PhysicalDisks(), clock.UtcNow, token), clock,
            readings => readings.Any(t => t.ValueCelsius is null) ? new Source
            {
                Id = "hwmon:nvme", Kind = "hwmon", RefreshSeconds = 1,
                State = "partial", LastAttemptAt = null, LastSuccessAt = null,
                DurationMs = null,
                Error = new SourceError
                {
                    Code = "channel_read_failed",
                    Message = "At least one NVMe channel was unavailable",
                },
            } : null);
        smart = new("smart", "smart", TimeSpan.FromSeconds(10),
            token => probes.Smart(PhysicalDisks(), clock.UtcNow, token), clock,
            result => result.Sources.FirstOrDefault(source => source.Id == "smart"));
    }

    private IReadOnlyList<Device> PhysicalDisks()
    {
        var inventory = zfs.Capture();
        return inventory.Payload is not null && inventory.Status.State == "ok"
            ? inventory.Payload.Devices
            : throw new InvalidDataException("ZFS/disk inventory unavailable");
    }

    public async Task<SnapshotDocument> TickAsync(CancellationToken cancellationToken = default)
    {
        await tickGate.WaitAsync(cancellationToken);
        try
        {
            await zfs.UpdateIfDueAsync(cancellationToken);
            await Task.WhenAll(
                system.UpdateIfDueAsync(cancellationToken),
                ipmi.UpdateIfDueAsync(cancellationToken),
                hba.UpdateIfDueAsync(cancellationToken),
                platform.UpdateIfDueAsync(cancellationToken),
                nvme.UpdateIfDueAsync(cancellationToken),
                smart.UpdateIfDueAsync(cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            return await PublishCurrentAsync(cancellationToken);
        }
        finally { tickGate.Release(); }
    }

    private async Task<SnapshotDocument> PublishCurrentAsync(CancellationToken cancellationToken)
    {
        var parts = new SnapshotParts(
            CachedSnapshotPart<ZfsInventoryResult>.From(zfs),
            CachedSnapshotPart<SystemMetrics>.From(system),
            CachedSnapshotPart<SensorMeasurements>.From(ipmi),
            CachedSnapshotPart<SensorMeasurements>.From(hba),
            CachedSnapshotPart<HwmonPlatformResult>.From(platform),
            CachedSnapshotPart<List<Temperature>>.From(nvme),
            CachedSnapshotPart<DiskTemperatureResult>.From(smart));
        var document = assembler.Assemble(parts);
        await publisher.PublishAsync(document, cancellationToken);
        return document;
    }

    private static async Task PollSourceAsync<T>(SourceCache<T> cache,
        CancellationTokenSource stop) where T : class
    {
        try
        {
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                await cache.UpdateIfDueAsync(stop.Token);
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
            }
        }
        catch
        {
            stop.Cancel(); // An unexpected loop failure must stop the whole worker.
            throw;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // One publisher; each source has one loop and never overlaps its own probe.
        await tickGate.WaitAsync(cancellationToken);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? collectors = null;
        try
        {
            // Disk probes need an inventory before their first attempt.
            await zfs.UpdateIfDueAsync(stop.Token);
            collectors = Task.WhenAll(
                PollSourceAsync(zfs, stop), PollSourceAsync(system, stop),
                PollSourceAsync(ipmi, stop), PollSourceAsync(hba, stop),
                PollSourceAsync(platform, stop), PollSourceAsync(nvme, stop),
                PollSourceAsync(smart, stop));
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                await PublishCurrentAsync(stop.Token);
                var delay = Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                var completed = await Task.WhenAny(collectors, delay);
                await completed; // Surface collector faults as well as cancellation.
            }
        }
        finally
        {
            stop.Cancel();
            try
            {
                if (collectors is not null) await collectors;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { tickGate.Release(); }
        }
    }
}
