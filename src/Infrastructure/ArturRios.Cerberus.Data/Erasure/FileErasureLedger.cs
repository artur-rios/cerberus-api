using ArturRios.Cerberus.Domain.Operations;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class FileErasureLedger : IErasureLedger
{
    private readonly string _directory;

    public FileErasureLedger(string directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Durable ledger requires the approved Linux host.");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Ledger path must be absolute.", nameof(directory));
        _directory = Path.GetFullPath(directory);
        if (!Directory.Exists(_directory))
            throw new IOException("Durable ledger storage must be provisioned before startup.");
        for (var current = new DirectoryInfo(_directory); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) throw new IOException("Ledger path must not contain symbolic links.");
        var mode = File.GetUnixFileMode(_directory);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException("Ledger directory must be private to the operator account.");
        SyncDirectory();
    }

    public async Task RecordAsync(ErasureEntry entry, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Durable ledger requires the approved Linux host.");
        Validate(entry);
        var legacy = Path.Combine(_directory, entry.ResourceId.ToString("N") + ".json");
        if (File.Exists(legacy))
        {
            var original = await ReadEntryAsync(legacy, cancellationToken);
            if (original.ResourceKind == entry.ResourceKind)
            {
                SyncDirectory();
                return;
            }
        }
        var destination = Path.Combine(_directory, entry.ResourceKind + "-" + entry.ResourceId.ToString("N") + ".json");
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                await JsonSerializer.SerializeAsync(file, entry, cancellationToken: cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination))
            {
                var existing = await ReadEntryAsync(destination, cancellationToken);
                if (existing.ResourceKind != entry.ResourceKind)
                    throw new InvalidDataException("Conflicting ledger resource kind.");
            }
            SyncDirectory();
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async IAsyncEnumerable<ErasureEntry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await ReadEntryAsync(file, cancellationToken);
        }
    }

    private static async Task<ErasureEntry> ReadEntryAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new InvalidDataException("Ledger record must not be a symbolic link.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 4096) throw new InvalidDataException("Invalid ledger record.");
        ErasureEntry? entry;
        try { entry = await JsonSerializer.DeserializeAsync<ErasureEntry>(file, cancellationToken: cancellationToken); }
        catch (JsonException) { throw new InvalidDataException("Invalid ledger record."); }
        if (entry is null) throw new InvalidDataException("Invalid ledger record.");
        Validate(entry);
        var filename = Path.GetFileNameWithoutExtension(path);
        if (filename != entry.ResourceId.ToString("N") && filename != entry.ResourceKind + "-" + entry.ResourceId.ToString("N"))
            throw new InvalidDataException("Ledger identifier does not match its record.");
        return entry;
    }

    private static void Validate(ErasureEntry entry)
    {
        if (!TerminalResourceIdentity.IsValid(entry.ResourceKind, entry.ResourceId)
            || entry.DeletedAt.Offset != TimeSpan.Zero || entry.DeletedAt < DateTimeOffset.UnixEpoch)
            throw new InvalidDataException("Invalid ledger record.");
    }

    private void SyncDirectory()
    {
        using var handle = new SafeFileHandle((IntPtr)Open(_directory, 0x10000), ownsHandle: true);
        if (handle.IsInvalid || Fsync(handle) != 0) throw new IOException("Cannot durably commit the erasure ledger.");
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(SafeFileHandle handle);
}
