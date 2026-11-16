namespace OrthoStreamXP.Scenery;

/// <summary>Why a read got less detail than X-Plane asked for, by choice: blurry on purpose, not a missing picture.</summary>
internal enum HeldBack
{
    No,
    /// <summary>Night with the photos skipped: the low-zoom base.</summary>
    Night,
    /// <summary>Dynamic quality: no sharper than the step for the camera's height.</summary>
    Altitude,
}

/// <summary>
/// The levels one open handle has been given, so a level X-Plane reads in several pieces is all from the same
/// version even if better data arrives in between (a BC1 block split between two versions would be garbage).
/// </summary>
internal sealed class ReadCursor
{
    private readonly Dictionary<int, (ReadOnlyMemory<byte> Bytes, Served Kind)> _levels = [];

    /// <summary>Where X-Plane's first chunk of the file (the header and the start of level 0) ended on this handle.</summary>
    public long? HeaderChunkEnd { get; set; }

    /// <summary>Where the latest read on this handle ended: a read starting there is the next piece of the same read.</summary>
    public long LastEnd { get; set; } = -1;

    /// <summary>
    /// The finest level fetched for the texture when X-Plane last wanted a level on this handle, and why (at night,
    /// or by Dynamic quality): a later piece of a level finer than it is a held-back read, not a stand-in. X-Plane
    /// interleaves reads on one handle (level 1, then the tail of levels 3 and on, then the rest of level 1), so
    /// each piece is judged by its own level, not by the read before it.
    /// </summary>
    public (int Finest, HeldBack Why) Hold { get; set; }

    /// <summary>Why a piece reading from <paramref name="mip"/> on is held back, by <see cref="Hold"/>.</summary>
    public HeldBack HeldBackAt(int mip) => mip < Hold.Finest ? Hold.Why : HeldBack.No;

    public bool TryGet(int mip, out ReadOnlyMemory<byte> bytes, out Served kind)
    {
        lock (_levels)
        {
            bool found = _levels.TryGetValue(mip, out var level);
            (bytes, kind) = level;
            return found;
        }
    }

    public bool Has(int mip)
    {
        lock (_levels) return _levels.ContainsKey(mip);
    }

    public void Set(int mip, ReadOnlyMemory<byte> bytes, Served kind)
    {
        lock (_levels) _levels[mip] = (bytes, kind);
    }
}
