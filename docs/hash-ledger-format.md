# CodeCompass hash ledger: format and read-only access

CodeCompass keeps a per-repo **change ledger**: for every file it indexes, the file's size, last-write time and a
content hash. It uses the ledger to skip unchanged files on an update. Other local tools (CodeDiffer first) may
**read** it so they don't re-hash files CodeCompass has already hashed.

This document is the contract for those readers. It describes format version **2** (written since v1.0.243), and
version **1**, which older versions wrote and readers may still meet. Version 2 adds each file's change time, file id
and hashed-at time, so a reader can rule out a same-size rewrite whose modified time was set back.

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

## Base file (CCSN)

Little-endian. A fixed header, then fixed-width columns indexed by record number `i` (0 to `count - 1`).

| Offset | Type | Field |
|---|---|---|
| 0 | uint32 | magic `0x4E535343` (the bytes `C` `S` `S` `N` as written: reads as "CSSN" in a hex dump) |
| 4 | int32 | version: **2** (or **1** from older CodeCompass versions) |
| 8 | int32 | `count`: number of records |
| 12 | int64 | `pathOffsetsOff` |
| 20 | int64 | `pathBlobOff` |
| 28 | int64 | `sizesOff` |
| 36 | int64 | `mtimesOff` |
| 44 | int64 | `hashesOff` |
| 52 | int64 | `changeTimesOff` (version 2 only) |
| 60 | int64 | `fileIdsOff` (version 2 only) |
| 68 | int64 | `hashedAtOff` (version 2 only) |
| 76 | int64 | `sha256Off` (version 2 only) |

Version 1 has the first five offsets (a 52-byte header); version 2 has all nine (an 84-byte header), the first five
in the same order.

Sections (offsets are absolute file offsets):

- **Path offsets** at `pathOffsetsOff`: `count + 1` int64 values. Path `i` is the bytes
  `[pathBlobOff + off[i], pathBlobOff + off[i+1])`.
- **Path blob** at `pathBlobOff`: the paths as UTF-8, concatenated, with no terminators.
- **Sizes** at `sizesOff`: `count` int64 values, the file size in bytes.
- **Modified times** at `mtimesOff`: `count` int64 values (see [Times](#times)).
- **Hashes** at `hashesOff`: `count` 16-byte values (see [Hashes](#hashes)).
- Version 2 only:
  - **Change times** at `changeTimesOff`: `count` int64 values (see [Times](#times)).
  - **File ids** at `fileIdsOff`: `count` int64 values, the filesystem's file id (NTFS file index); `0` = unknown.
  - **Hashed-at** at `hashedAtOff`: `count` int64 values, CodeCompass's own clock (UTC .NET ticks): just before the
    read for a trusted entry, when it was recorded for a pending one; `0` = unknown.
  - **SHA-256** at `sha256Off`: `count` 32-byte values. CodeCompass doesn't compute SHA-256: always all zero.

Records are sorted by path using **ordinal (byte-wise, case-sensitive)** comparison, so a reader can binary-search.

Validate before trusting a file. Each section must start after the previous one and be large enough for `count`, and
the last section must not run past the end of the file. Treat any failure as "ignore the ledger".

## Journal

The journal is rewritten whole each time; it isn't appended to. Little-endian, using .NET `BinaryWriter` encoding:
a *string* is a 7-bit-encoded length prefix (the number of UTF-8 bytes) followed by those UTF-8 bytes.

```
uint32  magic 0x324E5343 ("CSN2", written since v1.0.243) or 0x4A4E5343 ("CSNJ", older versions)
int32   upsertCount
repeat upsertCount times:
    string  path
    int64   size
    int64   mtime
    string  hash            32 uppercase hex characters (see Hashes)
    -- CSN2 only:
    int64   changeTime
    int64   fileId
    int64   hashedAt
    string  sha256          64 hex characters, or empty: CodeCompass writes it empty
int32   removalCount
repeat removalCount times:
    string  path            a path to delete from the base
```

The journal's magic, not the base's version, says which entry layout it uses. If the magic is unknown, ignore the whole
ledger (never guess). If the journal is truncated or otherwise unreadable, drop the journal and keep the base: a base
entry the journal would have replaced describes an older state of the file, so it simply won't match the live file
(rules 2-4 below) and costs a re-hash, never a wrong answer. This is what both CodeCompass and CodeDiffer do.

## Paths

- Relative to the indexed root, with `/` separators and no leading `/`.
- The original case from the filesystem.
- UTF-8 exactly as the directory listing returned it, with no Unicode normalization.

## Times

`mtime` is the file's last-write time in **UTC**, as .NET ticks: 100-nanosecond intervals since 0001-01-01 00:00 UTC
(`FileInfo.LastWriteTimeUtc.Ticks`). `0` means unknown. A **negative** value means *pending*: CodeCompass saw the file
with last-write time `-mtime` but hasn't trusted the entry yet (see below). A live file's ticks are always positive, so
neither ever matches.

`changeTime` (version 2) is the file's change time in the same units: the time of its last change of any kind, data or
metadata, so it moves when a tool sets the last-write time back. Windows reports it (`FILE_BASIC_INFO.ChangeTime`), and
CodeCompass reads it from the handle the file was read through. `0` means unknown: always so on other platforms, and on
entries carried over from a version 1 ledger until the file is next re-read or the index is rebuilt.

## Hashes

- **Versions 1 and 2 = XxHash128** (seed 0) over the file's raw bytes: the whole file, no decoding, no line-ending changes.
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

1. The entry's `mtime` is greater than `0`.
2. The live file's size equals the entry's size.
3. The live file's UTC last-write ticks equal the entry's `mtime`.
4. Version 2, when you need to rule out a rewrite whose last-write time was set back: the entry's `changeTime` is not
   `0` and equals the live file's change time, and the file ids match when both are known. A version 1 entry, or one
   with `changeTime` `0`, can't rule that out.

CodeCompass stats the file's read handle just before and just after reading it. If those disagree with each other or
with the directory listing (the file changed while being read), it records `mtime` `0` and no change time or file id, so
the entry matches nothing. CodeCompass decides what to record when it hashes a file, so readers need no clock rules of
their own. Every
filesystem's timestamps tick coarsely somewhere - FAT/exFAT every 2 s, some NAS boxes every second, and even NTFS only
advances a file's modified time on a ~1-16 ms timer - so a same-size edit in the same tick as the read keeps size and mtime
unchanged ("racily clean"). CodeCompass records the real `mtime` only when it knows that tick was over before the read:

- **First look:** the file was last modified - and, when known, last changed - safely before the read began: at least 3
  seconds before on a local disk, and an hour before on a network share (whose clock CodeCompass can't read; the hour
  absorbs realistic clock difference). The newer of the two times counts, so a last-write time set back to an old value
  (which moves the change time to now) is not settled.
- **Second look:** otherwise the entry is recorded *pending* (negative). On a later update CodeCompass re-reads the file;
  if it still has the same size, last-write time, change time and file id, and that read began more than 3 seconds after
  the pending entry was recorded (its `hashedAt`; both times on CodeCompass's own clock), the timestamps' tick had already
  started at the first look and is long over, so the entry is trusted. This needs no agreement between clocks, so it also
  settles files whose timestamp is in the future (a share whose clock runs ahead, a future-dated file), which the first
  look never trusts.

Either way, any write after the read gets a newer timestamp, which the next update (and any reader) sees as a mismatch.

Known limit: CodeCompass itself compares only size and last-write time when deciding whether to re-read a file, so a
same-size rewrite whose last-write time was deliberately set back is not noticed by CodeCompass's own index. A reader that
also checks the change time (rule 4) does notice it.

Anything that fails a rule: hash the file yourself.

## Compatibility

- Readers ignore what they don't recognise: extra manifest lines, files they don't know, unknown versions.
- CodeCompass changes the version number for any change to the base layout or the hash algorithm.
