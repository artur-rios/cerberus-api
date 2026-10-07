using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Data.Tests;

public class ErasureLedgerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cerberus-ledger-test-").FullName;

    [FunctionalFact]
    public void GivenMissingProvisionedStorage_WhenOpeningLedger_ThenFailWithoutRecreatingIt()
    {
        var missing = Path.Combine(_directory, "missing", "ledger");
        Assert.Throws<IOException>(() => new FileErasureLedger(missing));
        Assert.False(Directory.Exists(Path.Combine(_directory, "missing")));
    }

    [FunctionalFact]
    public async Task GivenRecordedDeletion_WhenReopeningLedger_ThenRetainIdentifier()
    {
        var entry = new ErasureEntry(Guid.NewGuid(), "record", DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        await new FileErasureLedger(_directory).RecordAsync(entry, default);
        var entries = new List<ErasureEntry>();
        await foreach (var recovered in new FileErasureLedger(_directory).ReadAsync(default)) entries.Add(recovered);

        Assert.Single(entries);
        Assert.Equal(entry, entries[0]);
    }

    [FunctionalFact]
    public async Task GivenConcurrentDuplicateDeletion_WhenRecording_ThenKeepOneOriginalEntry()
    {
        var entry = new ErasureEntry(Guid.NewGuid(), "account", DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => new FileErasureLedger(_directory).RecordAsync(entry, default)));
        var entries = new List<ErasureEntry>();
        await foreach (var recovered in new FileErasureLedger(_directory).ReadAsync(default)) entries.Add(recovered);

        Assert.Single(entries);
        Assert.Equal(entry, entries[0]);
    }

    [FunctionalFact]
    public async Task GivenCorruptLedger_WhenReading_ThenFailClosed()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"), "invalid-json-secret");
        var exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in new FileErasureLedger(_directory).ReadAsync(default)) { }
        });
        Assert.DoesNotContain("invalid-json-secret", exception.Message);
    }

    [FunctionalFact]
    public async Task GivenInterruptedTemporaryWrite_WhenReading_ThenIgnoreUncommittedFile()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "interrupted.tmp"), "partial");
        var entries = new List<ErasureEntry>();
        await foreach (var entry in new FileErasureLedger(_directory).ReadAsync(default)) entries.Add(entry);
        Assert.Empty(entries);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
