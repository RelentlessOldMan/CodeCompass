namespace CodeCompass.Core.Storage;

/// <summary>
/// Cross-process "one writer per index cache" claim, used so two sessions that share an index (e.g. two
/// windows on the same repo, or two projects that link the same external root) can't both mutate one
/// cache dir and corrupt it. Ownership is an OS-held EXCLUSIVE FILE HANDLE, not a lock file's contents:
/// the claim exists only while this process holds the handle open, so a crash / kill / shutdown / power
/// loss releases it automatically (the kernel closes the handle when the process dies; on power loss the
/// handle table is RAM and simply gone). There is NO PID, heartbeat, or TTL to go stale - a leftover
/// lock FILE is inert because we test ownership by trying to open it exclusively, not by its existence.
/// The cache dir is always LOCAL (%LOCALAPPDATA%), so this is a single-machine lock on NTFS where handle
/// share-modes are reliable; different machines have separate caches and never contend.
/// </summary>
public sealed class WriteOwnership : IDisposable
{
    private const string Name = ".writer.lock";
    private FileStream? _handle;

    private WriteOwnership(FileStream handle) => _handle = handle;

    /// <summary>Try to become the sole writer of <paramref name="cacheDir"/>. Returns the claim on
    /// success, or null if another LIVE process already holds it (this session should read-only that
    /// index). Never throws.</summary>
    public static WriteOwnership? TryAcquire(string cacheDir)
    {
        try
        {
            Directory.CreateDirectory(cacheDir);
            // FileShare.None => a second opener gets a sharing violation while we hold the handle; the
            // handle (hence the claim) is released the instant this process ends, cleanly or not.
            var fs = new FileStream(Path.Combine(cacheDir, Name), FileMode.OpenOrCreate,
                                    FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            return new WriteOwnership(fs);
        }
        catch (IOException) { return null; }              // held by another live process
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool IsHeld => _handle is not null;

    public void Dispose()
    {
        _handle?.Dispose(); // releases the claim; another session can now acquire
        _handle = null;
    }
}
