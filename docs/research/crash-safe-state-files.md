# Crash-safe local state files in .NET

Research for [issue #5](https://github.com/cquillen/dagline/issues/5). Feeds the decision in issue #10 ("Resume: what the state file records and what re-runs"). This note reports facts and costs. It does not pick an option.

- Researched: 2026-11-10.
- .NET source read at the `v10.0.0` tag of `dotnet/runtime`. SQLite, Win32, Linux and POSIX facts are from the documentation pages linked inline.
- Workload assumed: one process, a few thousand task state transitions over several hours, occasional bursts, one state file per run on a local disk.

## How to read this

Every claim is tagged with where it comes from:

- **[source]** read directly in the `dotnet/runtime` source.
- **[doc]** stated in first-party documentation (Microsoft Learn, man pages, POSIX, kernel.org, sqlite.org).
- **[measured]** measured locally for this note (one machine, one run, see [Cost](#cost)).
- **[inference]** follows from the cited facts but is not stated by a primary source.
- **Unverified** means no primary source was found. These are collected again in [Unverified](#unverified).

Two failure classes are kept apart throughout, because the guarantees differ:

- **Process kill**: the process dies (crash, `kill -9`, Task Manager, Ctrl+C handled badly) but the operating system keeps running.
- **OS crash or power loss**: the kernel stops without flushing its caches.

## Comparison

| | Append-only journal (JSON lines) | Atomic replace (temp file + rename) | SQLite (`Microsoft.Data.Sqlite`) |
| --- | --- | --- | --- |
| Survives process kill | Yes for every record handed to the OS (`Flush()`); bytes still in the `FileStream` buffer are lost | Yes: the old or the new file is in place; a stale temp file may be left behind | Yes, at any `synchronous` setting |
| Survives OS crash / power loss | Records before the last successful `Flush(true)`; the tail can be missing or torn and must be tolerated by the reader | Linux: needs `fsync` of the temp file before the rename, and of the directory after it, neither of which `File.Move` does. Windows: crash behaviour of the rename is not documented | Atomic commits by design; durable in WAL with `synchronous=FULL`; in WAL with `NORMAL`, or with a rollback journal and `FULL`, consistent but the latest commits can roll back |
| On-disk shape | One file, human-readable, complete history | One file, human-readable, current snapshot only (plus a transient temp file) | One binary file when cleanly closed; up to three files (`-wal`, `-shm`, or `-journal`) while open or after a crash |
| Dependencies | None beyond the BCL (P/Invoke needed for the one directory `fsync` on Unix) | None beyond the BCL (P/Invoke needed for directory `fsync` and for `MOVEFILE_WRITE_THROUGH`) | Five or six NuGet packages, including a 1.0 to 2.0 MB native library per platform |
| Write latency [measured], SSD | about 1 ms per durable record | about 4 ms per durable 0.5 MB snapshot; grows with file size | about 1.2 ms per commit (WAL, `FULL`); 0.05 ms (WAL, `NORMAL`); 5 ms (rollback journal, `FULL`) |
| Main pitfalls | Torn tail; must repair the tail before appending again; `Flush()` alone is not durable | `File.Move` fails if another process has the destination open on Windows; cross-volume moves silently become copy + delete; cannot hold a lock on the file being replaced | Sidecar files must travel with the database; pooled connections keep the file open; native binary in every consumer's output |
| Second process excluded by | The open handle on the journal itself: mandatory on Windows; on Unix advisory, and only with `FileShare.None` | A separate lock file | SQLite's own locking, optionally `locking_mode=EXCLUSIVE` |

## 1. The layers a write passes through

1. **`FileStream` buffer** (user space, 4096 bytes by default). [source: [`FileStream.cs#L15`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L15)] Lost on process kill. A `bufferSize` of 0 or 1 disables it. [doc: [`FileStreamOptions.BufferSize`](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestreamoptions.buffersize?view=net-10.0)] A `StreamWriter` on top adds a second buffer of its own.
2. **Operating system cache.** Survives process kill, lost on OS crash or power loss.
   - Windows: "write operations write file data to the system file cache rather than to the disk"; a lazy writer runs every second and queues one-eighth of the unflushed pages; on "a sudden system failure (such as a loss of power to the computer) ... the cached data will be lost". [doc: [File Caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)]
   - Linux: "A successful return from write() does not make any guarantee that data has been committed to disk ... The only way to be sure is to call fsync(2)". [doc: [write(2)](https://man7.org/linux/man-pages/man2/write.2.html)] With ext4 defaults, metadata can lag by up to 5 seconds and, because of delayed allocation, data by longer. [doc: [ext4 `commit=`](https://www.kernel.org/doc/html/latest/admin-guide/ext4.html)]
   - SQLite's documentation states the same split independently: with syncing off, "If the application running SQLite crashes, the data will be safe, but the database might become corrupted if the operating system crashes or the computer loses power". [doc: [PRAGMA synchronous](https://sqlite.org/pragma.html#pragma_synchronous)]
3. **Device write cache.** Outside the application's control. `fsync` on Linux "includes writing through or flushing a disk cache if present", but "older kernels and lesser used filesystems do not know how to flush disk caches". [doc: [fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html)] SQLite's authors warn that "most consumer-grade mass storage devices lie about syncing" and that `FlushFileBuffers` "can be completely disabled using registry settings on some Windows versions". [doc: [How To Corrupt](https://sqlite.org/howtocorrupt.html), [Atomic Commit](https://sqlite.org/atomiccommit.html)] This limit applies equally to all three options.

## 2. What `FileStream.Flush(true)` guarantees

- `Flush()` is `Flush(flushToDisk: false)`. It empties the `FileStream` buffer into the OS with a normal write and stops there. `Dispose` does the same and no more. [source: [`FileStream.cs#L379-L397`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileStream.cs#L379-L397), [`BufferedFileStreamStrategy.cs#L754-L778`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/BufferedFileStreamStrategy.cs#L754-L778); maintainer confirmation in [dotnet/runtime#100229](https://github.com/dotnet/runtime/issues/100229)]
- `Flush(true)` additionally calls `FlushFileBuffers` on Windows and `fsync` on Unix (`fcntl(F_FULLFSYNC)` on macOS). [source: [`FileStreamHelpers.Windows.cs#L39-L53`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/FileStreamHelpers.Windows.cs#L39-L53), [`FileStreamHelpers.Unix.cs#L40-L57`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/FileStreamHelpers.Unix.cs#L40-L57), [`pal_io.c#L729-L742`](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.c#L729-L742)] `RandomAccess.FlushToDisk(SafeFileHandle)` is the same call without a `FileStream`. [source: [`RandomAccess.cs#L262-L282`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/RandomAccess.cs#L262-L282)]
- Linux: `fsync` "transfers ... all modified in-core data of ... the file ... to the disk device ... so that all changed information can be retrieved even if the system crashes or is rebooted ... The call blocks until the device reports that the transfer has completed." It does **not** cover the directory entry: "Calling fsync() does not necessarily ensure that the entry in the directory containing the file has also reached disk. For that an explicit fsync() on a file descriptor for the directory is also needed." [doc: [fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html)]
- Windows: `FlushFileBuffers` "writes all the buffered information for a specified file to the device". The page warns it "can be inefficient when used after every write to a disk drive device when many writes are being performed separately". [doc: [FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers)] File metadata is always cached, so "to store any metadata changes to disk, the file must either be flushed or be opened with FILE_FLAG_WRITE_THROUGH". [doc: [File Caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching)] The `FlushFileBuffers` page does not say whether the device's own write cache is flushed. **Unverified.**
- **.NET has no managed way to `fsync` a directory.** Opening a directory through `FileStream` or `File.OpenHandle` on Unix throws (the runtime turns a directory into `EACCES`). [source: [`SafeFileHandle.Unix.cs#L325-L328`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L325-L328)] A P/Invoke to `open` + `fsync` is required. [inference]
- `FileOptions.WriteThrough` maps to `FILE_FLAG_WRITE_THROUGH` on Windows (the enum value is passed straight to `CreateFile`) and to `O_SYNC` on Unix. [source: [`SafeFileHandle.Windows.cs#L90-L98`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Windows.cs#L90-L98), [`SafeFileHandle.Unix.cs#L302-L305`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L302-L305)]
  - Linux `O_SYNC`: each write behaves "as though each write(2) was followed by a call to fsync(2)". [doc: [open(2)](https://man7.org/linux/man-pages/man2/open.2.html)]
  - Windows: with `FILE_FLAG_WRITE_THROUGH` alone, "the data is written to the system cache but is flushed to disk without delay", and NTFS also flushes metadata changes resulting from the request. The sentence about requesting "a write-through of the hard disk's local hardware cache" is attached to the combination with `FILE_FLAG_NO_BUFFERING`, which `FileOptions` does not expose, and "Not all hard disk hardware supports this write-through capability". [doc: [CreateFile, Caching Behavior](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew#caching-behavior)] So on Windows `WriteThrough` is not documented as equivalent to `Flush(true)`.
- Version pitfall: before .NET 8, `Flush(true)` on a buffered `FileStream` skipped the flush to disk when data was sitting in the buffer. Fixed in .NET 8. [doc: [dotnet/runtime#77373](https://github.com/dotnet/runtime/issues/77373), [#100229](https://github.com/dotnet/runtime/issues/100229)] Not a concern on .NET 10.
- The runtime maintainers have declined to make `Flush()` or `Dispose` flush to disk; the stated guidance is to call `Flush(true)` explicitly. [doc: [dotnet/runtime#32600](https://github.com/dotnet/runtime/issues/32600), still open]

## 3. Append-only journal with flush-to-disk

One record per line (JSON lines), appended, optionally followed by `Flush(true)`.

**Guaranteed**

- Process kill: every record already passed to the OS (`Flush()` returned, or buffering is off) is kept. [doc, section 1] Whatever was still in the `FileStream` or `StreamWriter` buffer is lost. [source, section 1]
- OS crash or power loss: every record written before the last successful `Flush(true)` is kept, subject to the device honouring the flush. [doc, section 2]
- On Linux the journal file's directory entry needs one directory `fsync` after the file is created, or the whole file can be missing after power loss. [doc: fsync(2)] The file is created once per run, so this is a one-off. [inference]

**Not guaranteed: the tail**

- No primary source promises that an append is all-or-nothing across a crash. `O_APPEND` makes the seek-and-write atomic with respect to other writers, not with respect to power loss. [doc: [write(2)](https://man7.org/linux/man-pages/man2/write.2.html)] A record that was being written at the moment of failure can be absent or cut short.
- What the torn region contains depends on the file system. ext4 in its default `data=ordered` mode writes data blocks before committing the metadata that exposes them; in `data=writeback` "A crash+recovery can cause incorrect data to appear in files which were written shortly before the crash". [doc: [ext4](https://www.kernel.org/doc/html/latest/admin-guide/ext4.html)] The equivalent behaviour for NTFS (and for XFS and Btrfs) is **unverified**.
- A record can also straddle two OS writes if it does not fit in the remaining `FileStream` buffer, so a process kill alone can leave a partial line when buffering is on. [inference from source: `BufferedFileStreamStrategy`]

**Handling a torn final line** [inference unless marked]

- A reader has to accept that the last line may be incomplete: no trailing newline, or JSON that does not parse. `System.Text.Json` throws `JsonException` on incomplete input by default. Parsing line by line keeps every good record and isolates the bad one.
- Before appending in a resumed run, the torn tail has to be removed (truncate to the end of the last good line) or neutralised (start with a newline). Otherwise the first new record is glued onto the fragment and the damage moves into the middle of the file.
- A torn tail is distinguishable from damage elsewhere only by position. Detecting corruption in the middle would need a per-record checksum or length prefix; none of the cited sources require it for a file that is only ever appended to and flushed.
- Loss model for the consumer of the journal: a transition whose record did not reach disk is simply not in the file. On resume it reads as "not yet succeeded", so the affected task runs again.

**Cost**

- No dependencies. Small code surface: append, flush, tolerant reader, tail repair, one directory `fsync` on Unix through P/Invoke.
- About 1 ms per durable record on an SSD. [measured] On rotating disks expect tens of milliseconds: SQLite's FAQ puts a synced commit at "a few dozen transactions per second" because "Transaction speed is limited by the rotational speed of your disk drive". [doc: [SQLite FAQ 19](https://sqlite.org/faq.html#q19)]
- The file is the complete history of the run in order, readable with any text tool. It grows with the number of transitions; current state has to be rebuilt by replaying it. [inference]

## 4. Atomic replace (temp file, then rename)

Write the whole state to a temp file, then `File.Move(temp, target, overwrite: true)` or `File.Replace(temp, target, backup)`.

**What the .NET calls do**

| Call | Windows | Unix |
| --- | --- | --- |
| `File.Move(src, dst, overwrite: true)` | `MoveFileExW` with `MOVEFILE_REPLACE_EXISTING \| MOVEFILE_COPY_ALLOWED`. No `MOVEFILE_WRITE_THROUGH`. [source: [`Interop.MoveFileEx.cs#L30-L42`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/Common/src/Interop/Windows/Kernel32/Interop.MoveFileEx.cs#L30-L42)] | `rename()`. On `EXDEV` (different mount) it falls back to copy + delete. [source: [`FileSystem.Unix.cs#L190-L211`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs#L190-L211)] |
| `File.Replace(src, dst, backup)` | `ReplaceFile`. [source: [`FileSystem.Windows.cs#L88-L96`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Windows.cs#L88-L96)] | Some `lstat` checks, then (if a backup is requested) `unlink(backup)` + `link(dst, backup)`, then `rename(src, dst)`. The checks "are not atomic". [source: [`FileSystem.Unix.cs#L122-L182`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Unix.cs#L122-L182)] |

Neither call flushes anything to disk. [source]

**POSIX / Linux**

- Atomic for observers: "If newpath already exists, it will be atomically replaced, so that there is no point at which another process attempting to access newpath will find it missing", and if the call fails, `rename()` "guarantees to leave an instance of newpath in place". [doc: [rename(2)](https://man7.org/linux/man-pages/man2/rename.2.html)] POSIX: the entry named *new* "shall remain visible to other threads throughout the renaming operation and refer either to the file referred to by new or old before the operation began". [doc: [POSIX rename](https://pubs.opengroup.org/onlinepubs/9799919799/functions/rename.html)]
- So a process kill at any point leaves either the complete old file or the complete new file, plus possibly an orphaned temp file. [doc + inference]
- Neither POSIX nor the man page says anything about what is on disk after an OS crash. That comes from the file system:
  - The temp file's data must be `fsync`ed before the rename. Without it, ext4 can commit the rename before the data, leaving a zero-length file; this happened in practice to a .NET application using write-then-rename without `Flush(true)`. [doc: [dotnet/runtime#32600](https://github.com/dotnet/runtime/issues/32600)] ext4's default `auto_da_alloc` mitigates the pattern ("the data blocks of the new file are forced to disk before the rename() operation is committed") and describes applications that rely on it as "broken". [doc: [ext4](https://www.kernel.org/doc/html/latest/admin-guide/ext4.html)] Other file systems are **unverified**.
  - The rename itself becomes durable only when the directory is synced. [doc: fsync(2)] Without that, a crash shortly after the rename can bring back the old file: an old but intact state. [inference]
- Temp file and target must be in the same directory (same mount). Otherwise `rename` fails with `EXDEV` and .NET silently substitutes a non-atomic copy + delete. [source]

**Windows / NTFS**

- The `MoveFileEx` documentation does not use the word "atomic". `MOVEFILE_REPLACE_EXISTING` is described as "the function replaces its contents with the contents of the lpExistingFileName file". [doc: [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)]
- Durability of the rename is opt-in through `MOVEFILE_WRITE_THROUGH` ("The function does not return until the file is actually moved on the disk"), which .NET does not pass. [doc + source] Getting it requires a P/Invoke to `MoveFileEx`. [inference]
- `MOVEFILE_COPY_ALLOWED` is always passed, so a cross-volume move silently becomes `CopyFile` + `DeleteFile`. [source + doc] The `File.Move` documentation itself calls copy-based replacement "not atomic, because a system or program crash during the Copy may leave a partially-written destination file". [doc: [File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)]
- Microsoft's guidance for atomically saving "document-like" data is: "write the document to a new file, then replace the original file with the new one. One method to do this is with the ReplaceFile API". [doc: [Alternatives to using Transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf)] But the `ReplaceFile` reference documents partial-failure states: with `ERROR_UNABLE_TO_MOVE_REPLACEMENT` and no backup name, "the replaced file no longer exists and the replacement file exists under its original name"; with `ERROR_UNABLE_TO_MOVE_REPLACEMENT_2` the replaced file "still exists with a different name". `REPLACEFILE_WRITE_THROUGH` is "not supported". [doc: [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)] Supplying a backup name makes the first failure state recoverable, at the price of a third file.
- `File.Replace` requires the destination to exist (`FileNotFoundException` otherwise) and throws across volumes, so the first write of a run needs a different call. [doc: [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0)]
- NTFS keeps "a transaction-based log file" and "uses this log to automatically restore file system consistency" after a failure. [doc: [NTFS overview](https://learn.microsoft.com/en-us/windows-server/storage/file-server/ntfs-overview)] That is a statement about file system metadata consistency. Whether a same-volume rename-over-existing is all-or-nothing across power loss is not stated in any Microsoft document found. **Unverified.**
- **Sharing violations.** `File.Move` with overwrite throws `UnauthorizedAccessException` when "The Operating System has failed to acquire an exclusive access to the destination file". [doc: File.Move] Any other process that has the target open (antivirus, indexer, a viewer) can cause this. An open, user-reported runtime issue says it happens even when the other process opened the file with `FileShare.Delete`, and reports failures every 50 to 100 replacements on a machine with aggressive scanning; maintainers have not confirmed the analysis. [doc: [dotnet/runtime#114230](https://github.com/dotnet/runtime/issues/114230)] A retry loop is needed; deleting the target first removes the atomicity. Windows has a rename mode that allows replacing a file with open handles (`FILE_RENAME_POSIX_SEMANTICS`), but it is a driver-level flag and `File.Move` does not use it. [doc: [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information); source]

**Cost**

- No dependencies. Code: serialise whole state, write temp, `Flush(true)`, rename, retry on sharing violation (Windows), directory `fsync` (Unix, P/Invoke), clean up stale temp files at start.
- Every transition rewrites the whole file, so cost scales with state size: about 4.4 ms for a 0.5 MB snapshot with `Flush(true)`, 1.4 ms without. [measured] Bursts can be coalesced into one write, which widens the window of transitions lost on a crash. [inference]
- The file holds the current state only. A history of the run exists only if the snapshot itself carries it, which makes the file, and every write, larger. [inference]
- The file being replaced cannot double as the lock: see section 6.

## 5. SQLite through `Microsoft.Data.Sqlite`

Included for comparison; the project's stated preference is no embedded database.

**Guaranteed**

- "transactions appear to be atomic even if the transaction is interrupted by an operating system crash or power failure", given the hardware and OS behave as SQLite assumes. [doc: [Atomic Commit](https://sqlite.org/atomiccommit.html)]
- "Transactions are durable across application crashes regardless of the synchronous setting or journal mode." [doc: [PRAGMA synchronous](https://sqlite.org/pragma.html#pragma_synchronous)]
- Across OS crash or power loss it depends on `synchronous`:
  - `FULL`: "ensures that an operating system crash or power failure will not corrupt the database". It is fully durable in WAL mode. With a rollback journal (the default journal mode) it "is not necessarily durable across a power loss": the last commit can go missing, without corruption, unless `EXTRA` is used, which also syncs the directory after the journal is deleted.
  - `NORMAL` in WAL mode: "safe from corruption" and "always consistent", but "A transaction committed in WAL mode with synchronous=NORMAL might roll back following a power loss or system crash".
  - `OFF`: "the database might become corrupted if the operating system crashes or the computer loses power". [doc: PRAGMA synchronous]
- SQLite does its own directory syncing on Unix: it syncs the directory when it creates a rollback journal, and with `EXTRA` again after deleting it. [doc: Atomic Commit, PRAGMA synchronous]
- Recovery is automatic: "If the last connection to a database crashed, then the first new connection to open the database will start a recovery process." [doc: [WAL](https://sqlite.org/wal.html)]

**Single-file behaviour**

- A cleanly closed database is one file. While it is open, and after a crash, it is not:
  - WAL mode adds `-wal` and `-shm` files. On a clean close the last connection "does one last checkpoint and then deletes the WAL and its associated shared-memory file". After a crash the WAL file can remain, and it "is part of the persistent state of the database and should be kept with the database if the database is copied or moved"; separating them can lose committed transactions or corrupt the database. [doc: WAL]
  - Rollback-journal mode creates a `-journal` file per transaction. After a crash that hot journal must stay next to the database under its original name. [doc: [How To Corrupt](https://sqlite.org/howtocorrupt.html)]
- Copying the database file while a transaction is in progress can produce a corrupt copy. [doc: How To Corrupt]
- WAL mode "does not work over a network filesystem" and is persistent once set. With `locking_mode=EXCLUSIVE` set before first access, no `-shm` file is created and other processes are locked out. [doc: WAL]
- `Microsoft.Data.Sqlite` does not turn WAL on; it is one `PRAGMA journal_mode = 'wal'` statement. [doc: [Async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)]
- Connections are pooled by default since 6.0: "database files being kept open by the process even after the ADO.NET connection object is closed". `Pooling=False` or `SqliteConnection.ClearPool` avoids that; it matters for the final checkpoint and for deleting or moving the file. [doc: [EF Core 6 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-6.0/breaking-changes), [connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)] [measured: with `Pooling=False`, only the `.db` files remained after a clean close in all three modes tested]
- The file is binary. Reading the run record needs SQLite tooling rather than a text viewer. [inference]

**Dependency footprint** (read from the package manifests on nuget.org)

- `Microsoft.Data.Sqlite` 10.0.15 (latest 10.x): `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 + `SQLitePCLRaw.core`, which pull in `SQLitePCLRaw.provider.e_sqlite3` and `SQLitePCLRaw.lib.e_sqlite3`. The native package is an 18.8 MB download covering all platforms.
- `Microsoft.Data.Sqlite` 11.0.0 (targets `net10.0`): `SQLitePCLRaw` 3.0.5, whose bundle depends on `SQLitePCLRaw.config.e_sqlite3` (and through it a provider package) and takes the native code from a package named `SQLite` (3.53.4, 27.5 MB download).
- Native library per platform: 1.0 to 2.0 MB (`e_sqlite3.dll` for win-x64 is 1.97 MB, `libe_sqlite3.so` for linux-x64 is 1.47 MB). Runtime identifiers shipped include win-x64/x86/arm64, linux-x64/arm/arm64 and their musl variants, osx-x64/arm64 and several more.
- For a library package this dependency is transitive: every application that references the library gets the managed assemblies and the native binary in its output. [inference]
- Alternatives exist that avoid shipping native code (`Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_sqlite3` for the system library, or `bundle_winsqlite3` on Windows 10+), at the cost of an SQLite version that varies by machine. [doc: [Custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)]
- `Microsoft.Data.Sqlite.Core` is marked `IsAotCompatible`. [source: [csproj, `release/10.0`](https://github.com/dotnet/efcore/blob/release/10.0/src/Microsoft.Data.Sqlite.Core/Microsoft.Data.Sqlite.Core.csproj)] Trim and AOT behaviour of the `SQLitePCLRaw` packages was not checked. **Unverified.**

**Pitfalls SQLite documents for itself**

- File systems with broken locks, and POSIX advisory locks being cancelled when any descriptor for the file is closed elsewhere in the process. [doc: How To Corrupt, sections 2.1 and 2.2]
- Two copies of SQLite linked into one process. [doc: How To Corrupt, 2.3] Relevant when the host application also uses SQLite through a different native build. [inference]
- Renaming or unlinking the database while it is open is "undefined". [doc: How To Corrupt, 2.5]

**Cost**

- One commit per transition: about 1.2 ms in WAL with `FULL`, 0.05 ms in WAL with `NORMAL`, 5 ms with the default rollback journal and `FULL`. [measured]
- Code: schema, connection lifetime, pragmas, pooling settings. Crash recovery, torn-write handling and locking are SQLite's. [inference]

## 6. Exclusive locking across platforms

**`FileShare` when opening the file**

- Windows: the share mode is passed to `CreateFile` and enforced by the OS for every process. With share mode zero, "the file or device cannot be shared and cannot be opened again until the handle to the file or device is closed". [doc: [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew); source: `SafeFileHandle.Windows.cs`] This is mandatory: no other process can read or write the file, including text viewers and `type`. `FileShare.Read` keeps writers out and lets readers in. [doc: [FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0)] The reader has to cooperate too: "You cannot request a sharing mode that conflicts with the access mode that is specified in an existing request that has an open handle", so a reader must open with `FileShare.ReadWrite` while the writer holds the file; a reader that asks for `FileShare.Read` (the `FileStream` default) is refused. [doc: CreateFileW; inference for the .NET mapping]
- Unix: there is no share mode. .NET emulates it after opening the file with `flock`: `FileShare.None` takes `LOCK_EX`, every other value takes `LOCK_SH`, both non-blocking. The source comment is explicit: "This is only advisory locking ... not as granular as Windows, not mandatory, and not atomic with file opening". [source: [`SafeFileHandle.Unix.cs#L345-L360`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L345-L360)] Consequences:
  - It stops a second .NET process (or anything else that calls `flock`) with an `IOException` on open. It does not stop `cat`, an editor, `cp` or any program that does not ask for the lock: "a process is free to ignore the use of flock() and perform I/O on the file". [doc: [flock(2)](https://man7.org/linux/man-pages/man2/flock.2.html)]
  - Only `EWOULDBLOCK` is treated as failure. If the file system does not support locking, the open succeeds unlocked and silently. [source: same lines]
  - `LOCK_SH` is skipped when writing to NFS, CIFS or SMB mounts; `LOCK_EX` is always attempted. [source: [`SafeFileHandle.Unix.cs#L454-L486`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L454-L486)]
  - The whole mechanism is switched off by `DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1` or the `System.IO.DisableFileLocking` AppContext switch, which a host application or its environment may set. [source: [`SafeFileHandle.Unix.cs#L37-L38`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs#L37-L38)]
  - Only `FileShare.None` excludes anyone. Two processes that both open the file for writing with `FileShare.Read` each take a shared lock and both succeed, where on Windows the second would be refused. [source: the `LOCK_EX` / `LOCK_SH` choice in the same lines]
  - With `FileShare.None`, a second open of the same file inside the same process is refused as well, on both platforms. [doc: FileShare, "by this process or another process"; inference from flock(2) for Unix: descriptors from separate `open` calls "are treated independently"]

**Release on crash**

- Windows: on process termination "All kernel objects are closed". [doc: [Terminating a Process](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process)]
- Unix: an `flock` lock is released "when all such file descriptors have been closed", which the kernel does when the process dies. [doc: flock(2)]
- So an OS-held lock never goes stale after a kill, unlike a "lock file exists" convention. [inference]

**`FileStream.Lock` (byte-range locks)**

- Windows: `LockFile`, mandatory, released by the OS on termination, although "the time it takes for the operating system to unlock these locks depends upon available system resources". [source: `FileStreamHelpers.Windows.cs#L107-L118`; doc: [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex)]
- Linux: `fcntl(F_SETLK)`, advisory. [source: [`pal_io.c`, `SystemNative_LockFileRegion`](https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Native/pal_io.c#L1736-L1770)] POSIX record locks are dropped when the process closes any descriptor for that file. [doc: How To Corrupt, 2.2]
- macOS, iOS, tvOS and FreeBSD: throws `PlatformNotSupportedException`. [source: [`FileStreamHelpers.Unix.cs#L59-L67`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/Strategies/FileStreamHelpers.Unix.cs#L59-L67); doc: [FileStream.Lock](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.lock?view=net-10.0)]

**Interaction with each option** [inference]

- Journal: the writer keeps the file open for the whole run, so the open handle is the lock. The two platforms pull in opposite directions. On Windows, `FileShare.None` also stops an operator viewing the file during the run, while `FileShare.Read` lets viewers in and still refuses a second writer. On Unix, `FileShare.Read` refuses nobody; `FileShare.None` is the only setting that stops a second .NET process, and it never stops viewers because the lock is advisory. The same share mode therefore does not give "one writer, any number of viewers" on both.
- Atomic replace: the state file cannot be the lock. On Unix a `flock` belongs to the open file, not the name, and each rename installs a different file under the name. On Windows a handle held on the target makes the replacing move fail. A separate lock file held open for the run is needed, which is a second file per run.
- SQLite: handles its own cross-process locking; a second writer gets `SQLITE_BUSY`. Its locks are also advisory POSIX locks on Unix, and it warns that locking "might not work correctly if the database file is kept on an NFS filesystem". [doc: [SQLite FAQ 5](https://sqlite.org/faq.html#q5)]

## 7. `System.Text.Json`: source generation and trimming

- The reflection-based overloads, such as `JsonSerializer.Serialize<T>(T, JsonSerializerOptions)`, are annotated `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`. The overloads taking a `JsonTypeInfo<T>` or a `JsonSerializerContext` are not. [source: [`JsonSerializer.Write.String.cs#L28-L30`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/JsonSerializer.Write.String.cs#L28-L30)]
- When an application sets `PublishTrimmed`, "reflection-based serialization is automatically disabled" unless `JsonSerializerIsReflectionEnabledByDefault` is set; Native AOT requires source generation. [doc: [Source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation), [Reflection vs source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation)]
- A library that wants to be usable from trimmed or AOT-published applications therefore has to serialise its own state-file types through a source-generated `JsonSerializerContext`. Setting `IsAotCompatible` (or `IsTrimmable`) in the library project turns any use of the reflection overloads into build warnings. [doc: [Prepare libraries for trimming](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming)]
- Types the library does not own (for example a piece of state that a task defines and asks to have saved) cannot be covered by the library's context. The trim-safe shapes are: the caller supplies a `JsonTypeInfo<T>`, or the library stores an opaque value (`JsonElement`, raw UTF-8, a string) and the caller serialises it. [inference from the annotations above]
- JSON lines:
  - JSON strings cannot contain a raw newline (control characters are escaped), and output is not indented unless `WriteIndented` is set, so one serialised value is one line. [inference from the JSON grammar and the default options]
  - .NET 9 and later can read a sequence of top-level values: `JsonReaderOptions.AllowMultipleValues` on `Utf8JsonReader`, and `JsonSerializer.DeserializeAsyncEnumerable<T>(stream, topLevelValues: true)`. [doc: [Use Utf8JsonReader, "Read multiple JSON documents"](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader)]
  - Whether `DeserializeAsyncEnumerable` yields all complete values before throwing on a torn final value is **unverified**. Splitting on newlines and deserialising each line separately does not depend on it.
- Not researched: schema evolution of the state file across library versions (unknown properties, renamed members, a format version field).

## Cost

Measured for this note with a throwaway program: .NET SDK 10.0.404, Windows 10, NTFS, SATA SSD, one run, a JSON line of about 170 bytes as the record. Indicative only; no Linux or rotating-disk run was made.

| Operation | n | mean | p50 | p99 | max |
| --- | --- | --- | --- | --- | --- |
| Append line + `Flush(true)` | 2000 | 0.95 ms | 0.85 ms | 3.8 ms | 14.3 ms |
| Append line + `Flush()` (OS cache only) | 2000 | 0.004 ms | 0.003 ms | 0.03 ms | 0.3 ms |
| Append line, `FileOptions.WriteThrough`, no `Flush(true)` | 2000 | 0.17 ms | 0.12 ms | 0.95 ms | 8.7 ms |
| 503 KiB snapshot: temp + `Flush(true)` + `File.Move` | 300 | 4.40 ms | 4.00 ms | 11.9 ms | 19.0 ms |
| 503 KiB snapshot: temp + `File.Move`, no flush to disk | 300 | 1.39 ms | 1.35 ms | 1.95 ms | 2.1 ms |
| SQLite insert per transaction, WAL, `synchronous=FULL` | 2000 | 1.22 ms | 1.07 ms | 5.2 ms | 11.2 ms |
| SQLite insert per transaction, WAL, `synchronous=NORMAL` | 2000 | 0.05 ms | 0.03 ms | 0.10 ms | 16.0 ms |
| SQLite insert per transaction, rollback journal, `synchronous=FULL` | 2000 | 5.03 ms | 4.57 ms | 9.9 ms | 51.2 ms |

Scaled to the workload [inference]: 3000 transitions cost about 3 s of cumulative flush time for the journal, about 13 s for the snapshot and about 4 s for SQLite in WAL with `FULL`, spread over a run of hours. On a rotating disk, at the "few dozen" synced commits per second SQLite's FAQ quotes, the same 3000 durable writes would add up to a minute or two. A burst of a few hundred transitions costs a few hundred milliseconds of flush time on SSD if each one is flushed individually.

## Unverified

Claims for which no primary source was found, or which were not tested:

1. Whether a same-volume `MoveFileEx(MOVEFILE_REPLACE_EXISTING)` on NTFS is all-or-nothing across an OS crash or power loss. Microsoft's documentation does not say so; it recommends `ReplaceFile` for atomic document saves and documents partial-failure states for it.
2. Whether `FlushFileBuffers` flushes the storage device's own write cache. The function's reference page does not say.
3. What a torn append looks like after power loss on NTFS, XFS and Btrfs (missing bytes, zeros, or stale data). Only ext4's data modes were checked.
4. Whether replace-by-rename without an `fsync` of the temp file is safe on Linux file systems other than ext4 with `auto_da_alloc`.
5. Whether `JsonSerializer.DeserializeAsyncEnumerable(topLevelValues: true)` returns the complete leading values before failing on a truncated final value.
6. Trim and Native AOT compatibility of the `SQLitePCLRaw` packages.
7. What state a file is left in after `Flush(true)` throws, and whether retrying the flush is meaningful.
8. No crash-injection test (killing a process or cutting power mid-write) was run. The guarantees above are documented ones, not observed ones.
9. Latency on Linux and on rotating disks was not measured; the rotating-disk figure is derived from SQLite's FAQ.

## Sources

.NET runtime source (`v10.0.0`): `FileStream.cs`, `BufferedFileStreamStrategy.cs`, `OSFileStreamStrategy.cs`, `FileStreamHelpers.Windows.cs`, `FileStreamHelpers.Unix.cs`, `RandomAccess.cs`, `FileSystem.Windows.cs`, `FileSystem.Unix.cs`, `Interop.MoveFileEx.cs`, `SafeFileHandle.Windows.cs`, `SafeFileHandle.Unix.cs`, `pal_io.c`, `JsonSerializer.Write.String.cs`. Linked inline.

.NET runtime issues: [#32600](https://github.com/dotnet/runtime/issues/32600), [#77373](https://github.com/dotnet/runtime/issues/77373), [#100229](https://github.com/dotnet/runtime/issues/100229), [#114230](https://github.com/dotnet/runtime/issues/114230), [#18034](https://github.com/dotnet/runtime/issues/18034).

Microsoft Learn: [FileStream.Flush](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0), [File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0), [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0), [FileShare](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare?view=net-10.0), [FileOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileoptions?view=net-10.0), [FileStream.Lock](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.lock?view=net-10.0), [FlushFileBuffers](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-flushfilebuffers), [MoveFileExW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw), [ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew), [CreateFileW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew), [LockFileEx](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-lockfileex), [File Caching](https://learn.microsoft.com/en-us/windows/win32/fileio/file-caching), [Alternatives to using Transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/deprecation-of-txf), [Terminating a Process](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process), [NTFS overview](https://learn.microsoft.com/en-us/windows-server/storage/file-server/ntfs-overview), [FILE_RENAME_INFORMATION](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information), [Microsoft.Data.Sqlite custom versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions), [connection strings](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings), [async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async), [EF Core 6 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-6.0/breaking-changes), [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation), [reflection vs source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation), [Utf8JsonReader](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader), [prepare libraries for trimming](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/prepare-libraries-for-trimming).

Linux and POSIX: [fsync(2)](https://man7.org/linux/man-pages/man2/fsync.2.html), [rename(2)](https://man7.org/linux/man-pages/man2/rename.2.html), [write(2)](https://man7.org/linux/man-pages/man2/write.2.html), [open(2)](https://man7.org/linux/man-pages/man2/open.2.html), [flock(2)](https://man7.org/linux/man-pages/man2/flock.2.html), [POSIX rename](https://pubs.opengroup.org/onlinepubs/9799919799/functions/rename.html), [POSIX fsync](https://pubs.opengroup.org/onlinepubs/9799919799/functions/fsync.html), [ext4 administration guide](https://www.kernel.org/doc/html/latest/admin-guide/ext4.html).

SQLite: [Atomic Commit](https://sqlite.org/atomiccommit.html), [Write-Ahead Logging](https://sqlite.org/wal.html), [PRAGMA statements](https://sqlite.org/pragma.html), [How To Corrupt An SQLite Database File](https://sqlite.org/howtocorrupt.html), [FAQ](https://sqlite.org/faq.html).

NuGet package manifests: `Microsoft.Data.Sqlite` and `Microsoft.Data.Sqlite.Core` 10.0.15 and 11.0.0, `SQLitePCLRaw.bundle_e_sqlite3` 2.1.13 and 3.0.5, `SQLitePCLRaw.lib.e_sqlite3` 2.1.13, `SQLite` 3.53.4.
