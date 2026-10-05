# CodeCompass hash ledger: format and read-only access

CodeCompass keeps a per-repo **change ledger**: for every file it indexes, the file's size, last-write time and a
content hash. It uses the ledger to skip unchanged files on an update. Other local tools (CodeDiffer first) may
**read** it so they don't re-hash files CodeCompass has already hashed.

This document is the contract for those readers. It describes format version **1**.

**Ground rules**

- **Read-only.** Only CodeCompass writes this ledger. It's part of CodeCompass's index: it is compacted, rewritten,
  wiped on a rebuild, and deleted by `codecompass cache clear` / `cache gc`. A tool that wants to store its own hashes
  keeps its own ledger, in its own folder. It may use this same format.
- **Never guess.** If anything doesn't match this document (an unknown magic number or version, a missing file, a
  corrupt section), ignore the ledger and hash the files yourself.
- **Always re-check the live file.** An entry only says what the file looked like when CodeCompass last hashed it.
  See [When an entry can be trusted](#when-an-entry-can-be-trusted).

## Where it lives

```
%LOCALAPPDATA%\CodeCompass\<repoKey>\
    snapshot.manifest          names the current base file
    snapshot-NNNNNNNN.base     the ledger body (CCSN), NNNNNNNN = 8-digit number
    snapshot.journal           recent changes on top of the base (CSNJ); may be absent
```

The ledger is always on the **local** machine, never on the share being indexed. The same folder holds the rest of
the index. Ignore everything that isn't one of the three names above.

### repoKey

```
repoKey = first 16 characters of UPPERCASE hex( SHA-256( UTF-8( lower( NormalizeDir(root) ) ) ) )
```

`NormalizeDir(root)` is the absolute, fully qualified path with any trailing directory separator removed (a drive
root such as `C:\` keeps its separator). `lower` is invariant-culture lower-casing.

**The same folder can have more than one key.** `\\server\share\repo` and a drive letter mapped to it (`Z:\repo`) are
different strings, so they get different keys and different ledgers. A reader looking for a ledger should try every
form of the path it knows: as given, UNC-resolved, and drive-mapped.

**Subfolders:** there is one ledger per indexed root. To use it for a subfolder of an indexed root, walk up the
subfolder's ancestors looking for a ledger, then strip the relative prefix from paths. On Windows, compare the prefix
case-insensitively; stored paths keep their original case.

## Reading it safely

1. Read `snapshot.manifest`. It's UTF-8 text, one value per line:
   - line 1: the current base file name, e.g. `snapshot-00000042.base`. This must be a bare file name; reject
     anything containing a path separator.
   - line 2: CodeCompass's next base number (not needed by readers).
   - Ignore any further lines; future versions may add some.
2. Open that base file, and `snapshot.journal` if it exists. Open each with sharing set to **ReadWrite | Delete**, read
   what you need, and close it promptly. Don't hold a long-lived memory map: CodeCompass deletes old base files after
   compaction, and an open handle without Delete sharing blocks that.
3. If the named base file doesn't exist, CodeCompass replaced it while you were reading. Re-read the manifest and
   retry once; if it fails again, ignore the ledger.
4. Merge: start from the base entries, then apply the journal (its entries replace or add to the base; its removals
   delete base entries).

If the manifest is missing, there is no current ledger. A `snapshot.bin` from very old versions isn't covered by this
document.

## Base file (CCSN), version 1

Little-endian. A fixed header, then fixed-width columns indexed by record number `i` (0 to `count - 1`).

| Offset | Type | Field |
|---|---|---|
| 0 | uint32 | magic `0x4E535343` (the bytes `C` `S` `S` `N` as written: reads as "CSSN" in a hex dump) |
| 4 | int32 | version = **1** |
| 8 | int32 | `count`: number of records |
| 12 | int64 | `pathOffsetsOff` |
| 20 | int64 | `pathBlobOff` |
| 28 | int64 | `sizesOff` |
| 36 | int64 | `mtimesOff` |
| 44 | int64 | `hashesOff` |

Sections (offsets are absolute file offsets):

- **Path offsets** at `pathOffsetsOff`: `count + 1` int64 values. Path `i` is the bytes
  `[pathBlobOff + off[i], pathBlobOff + off[i+1])`.
- **Path blob** at `pathBlobOff`: the paths as UTF-8, concatenated, with no terminators.
- **Sizes** at `sizesOff`: `count` int64 values, the file size in bytes.
- **Modified times** at `mtimesOff`: `count` int64 values (see [Times](#times)).
- **Hashes** at `hashesOff`: `count` 16-byte values (see [Hashes](#hashes)).

Records are sorted by path using **ordinal (byte-wise, case-sensitive)** comparison, so a reader can binary-search.

Validate before trusting a file. Each section must start after the previous one; `hashesOff + count * 16` must not run
past the end of the file; and each section must be large enough for `count`. Treat any failure as "ignore the ledger".

## Journal (CSNJ)

The journal is rewritten whole each time; it isn't appended to. Little-endian, using .NET `BinaryWriter` encoding:
a *string* is a 7-bit-encoded length prefix (the number of UTF-8 bytes) followed by those UTF-8 bytes.

```
uint32  magic 0x4A4E5343   ("CSNJ")
int32   upsertCount
repeat upsertCount times:
    string  path
    int64   size
    int64   mtime
    string  hash            32 uppercase hex characters (see Hashes)
int32   removalCount
repeat removalCount times:
    string  path            a path to delete from the base
```

The journal has no version field of its own. It belongs to the base version named in the manifest. If the magic
doesn't match or the file is truncated, ignore the journal **and** the base: entries the journal would have replaced
are no longer trustworthy.

## Paths

- Relative to the indexed root, with `/` separators and no leading `/`.
- The original case from the filesystem.
- UTF-8 exactly as the directory listing returned it, with no Unicode normalization.

## Times

`mtime` is the file's last-write time in **UTC**, as .NET ticks: 100-nanosecond intervals since 0001-01-01 00:00 UTC
(`FileInfo.LastWriteTimeUtc.Ticks`). `0` means unknown.

## Hashes

- **Version 1 = XxHash128** (seed 0) over the file's raw bytes: the whole file, no decoding, no line-ending changes.
  The 16 bytes are stored in the order `System.IO.Hashing.XxHash128.Hash` returns them; the journal stores the same
  bytes as uppercase hex.
- XxHash128 is for **equality only**. It isn't cryptographic, so don't use it to defend against deliberately crafted
  collisions.
- Any change of algorithm will come with a new version number. A reader that doesn't know the version must not
  assume an algorithm.

Special values:

| Value | Meaning |
|---|---|
| all `FF` (`FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF`) | CodeCompass looked at the file and found it **binary**. There's no content hash; hash it yourself. |
| all `00` | Unknown (defensive). Hash it yourself. |

**Not in the ledger at all:** files CodeCompass doesn't index (ignored paths, files over the index size cap,
symlinks and junctions). Hash those yourself.

## When an entry can be trusted

An entry stands for the live file's content only if **all** of these hold:

1. The entry's `mtime` is not `0`.
2. The live file's size equals the entry's size.
3. The live file's UTC last-write ticks equal the entry's `mtime`.

CodeCompass decides what to record when it hashes a file, so readers need no clock rules of their own. It records an
`mtime` of `0` (so the entry matches no file) when the timestamp couldn't prove the bytes:

- **Possibly racy:** the timestamp is a whole second (a coarse filesystem such as FAT/exFAT or some NAS boxes, where a
  same-size edit in the same second leaves size and mtime unchanged) and within the last hour of CodeCompass's clock.
  The hour also absorbs any realistic clock difference between a file server and the indexing machine.
- **Changed while being read:** for a file modified within the last hour, CodeCompass checks its size and timestamp
  again after reading it; if either moved since the file was listed, the hash may not match.

A sub-second timestamp (NTFS, or ext4/xfs behind Samba) can't be racy: any later edit gets a new timestamp.

Known limit: a tool that rewrites a file at the same size and then deliberately restores its exact previous
timestamp is not detected (that needs the file's change time, which this format doesn't record).

Anything that fails a rule: hash the file yourself.

## Compatibility

- Readers ignore what they don't recognise: extra manifest lines, files they don't know, unknown versions.
- CodeCompass changes the version number for any change to the base layout or the hash algorithm.
