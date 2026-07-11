# Threat Model

## Purpose and claim

Nyx is a best-effort logical file shredder for ordinary Windows 11 users. Its narrow claim is:

> For a supported, stable local object, Nyx attempts one cryptographically random overwrite of every addressable data stream, requests that the writes reach the storage stack, truncates the streams, weakens the original name, and performs a normal filesystem deletion.

This is not a claim of physical irrecoverability, media sanitization, regulatory compliance, or conformance with a particular erasure standard. The design follows the practical distinction between logical overwrite and media-level sanitization described by [NIST SP 800-88 Rev. 2](https://csrc.nist.gov/pubs/sp/800/88/r2/final), without claiming NIST certification.

## Security objective

After a `Shredded` result, an ordinary process using the live logical filesystem should not be able to reopen the selected object or read its former addressable data streams at the original path. Nyx also aims to avoid modifying any object that was not in the reviewed, revalidated batch.

The user is trusted to select the correct target and understand the final confirmation. Windows access controls, the filesystem, storage drivers, firmware, and hardware are trusted to implement their documented interfaces honestly. Nyx does not attempt to defeat an administrator, kernel-level attacker, malicious driver, compromised firmware, or forensic access to the physical medium.

## Procedure covered by a successful result

For regular files and supported directory streams, the v0.1 contract is:

1. Preflight the complete batch without destructive writes.
2. Snapshot identity, attributes, size, timestamps, data streams, and containment information.
3. Re-enumerate and compare the complete batch after confirmation and before the first write.
4. Process serially. For every data stream, write fresh system-generated random bytes through a 1 MiB buffer, detect short writes, request write-through behavior, and call `FlushFileBuffers`.
5. After every stream is overwritten and flushed, truncate it, replace the final path component once with a random legal name of the same length, and request normal deletion through a revalidated handle.
6. Process directories bottom-up and delete a directory only when every reviewed child is gone and the directory is still empty.

Nyx does not perform a full read-back verification, raw-disk writes, slack-space wiping, or free-space wiping. A flush request proves only that the operating-system API accepted the request; it cannot prove where every physical copy resides.

## Supported boundary

Nyx v0.1 accepts local files and folders on fixed or removable NTFS, exFAT, and FAT32 volumes. It rejects UNC paths, mapped drives, device paths, raw volumes, ReFS, unknown filesystems, and volume roots.

Protected trees are blocked, including Windows, Program Files, ProgramData, Boot, Recovery, System Volume Information, `$Recycle.Bin`, the `Users` root, the current user's profile root, and a directory containing the running Nyx executable. Ordinary descendants such as the user's Downloads directory remain eligible if every item passes preflight.

NTFS named `$DATA` streams on files and directories are part of the object snapshot, size calculation, overwrite, and result. Failure to enumerate or open any required stream blocks the batch. See [Microsoft's file-stream documentation](https://learn.microsoft.com/en-us/windows/win32/fileio/file-streams).

FAT32 retains its filesystem limit of roughly 4 GiB per file. A 100 GB acceptance case applies only to NTFS and exFAT; see Microsoft's [filesystem comparison](https://learn.microsoft.com/en-us/windows/win32/fileio/filesystem-functionality-comparison).

## Blocked conditions

Preflight blocks the entire batch when safe handling cannot be established. Blocking conditions include:

- missing access, an exclusive open conflict, incomplete enumeration, or unreliable identity;
- hard-linked files;
- NTFS compressed, sparse, or EFS-encrypted objects;
- an unknown reparse-point tag;
- cloud placeholders, partially hydrated objects, offline/recall states, or any cloud state that would require a download;
- a changed object set, identity, attribute, size, timestamp, or stream between review and final revalidation.

Users cannot override a blocker and cannot exclude only one blocked descendant of a selected folder. This all-or-nothing preflight prevents an apparently complete folder operation from silently skipping content.

During shredding, a newly detected identity change or volume-level fatal error stops all unstarted objects. Ordinary per-object I/O errors are recorded and processing may continue. Completed mutations cannot be rolled back.

## Links and cloud files

Symbolic links, junctions, and mount points are treated as link entries: Nyx does not follow or enumerate their targets. After warning the user, it randomizes and removes only the link entry. Other reparse-point types are blocked.

A OneDrive or other Cloud Files object is eligible only when Windows reports it as completely local. A local cloud object still receives a warning because synchronization history or remote versions can preserve the content. Nyx never hydrates a placeholder to shred it.

## Warnings that do not block

The review stage warns about:

- SSD, flash, or unknown media, where remapping and wear levelling can retain old physical pages;
- completely local cloud-synchronized content;
- read-only attributes that must be cleared immediately before deletion;
- supported link entries, whose targets will remain intact.

Proceeding accepts these limitations; it does not increase Nyx's guarantee.

## Explicitly out of scope

Nyx does not locate, overwrite, or invalidate:

- snapshots, restore points, backups, cloud history, remote replicas, or previous versions;
- old MFT, USN journal, FAT, directory, or allocation metadata;
- search indexes, thumbnails, application caches, temporary files, crash dumps, or page files;
- filesystem slack, unallocated space, device-reserved areas, remapped sectors, SSD over-provisioned pages, or storage-controller caches outside Windows' guarantees;
- copies created by another process before or during the operation;
- filenames or contents previously captured in logs, reports, telemetry from other software, or screenshots.

Nyx also excludes Windows 10, ARM64, non-Windows systems, network storage, whole-disk sanitization, free-space wiping, administrator bypass, forced process termination, reboot-time deletion, enterprise audit, and compliance certification.

## Result semantics

Results are intentionally more specific than success/failure:

- `Shredded`: all required streams were overwritten and flushed, the name was randomized, and the object was deleted.
- `DeletedWithoutNameRandomization`: content processing completed and deletion succeeded, but name randomization did not.
- `LinkRemoved`: only the reviewed link entry was randomized/removed; its target was not traversed.
- `FailedBeforeMutation`: no destructive write for this object began.
- `PartiallyOverwritten`: at least one destructive write occurred, but all required bytes/streams were not completed.
- `OverwriteFlushFailed`: overwrite writes completed but a required flush failed.
- `OverwrittenNotDeleted`: overwrite/flush completed but the object remains.
- `FolderRetained`: a directory could not be removed after child processing.
- `NotStarted`: the object was not mutated, commonly because the user stopped the batch or a fatal change was detected.

These labels describe observed API-stage outcomes, not forensic proof. Retrying failed or unstarted objects requires a fresh preflight and confirmation.

## Privacy and reports

Nyx is offline, emits no telemetry, and retains no cross-session history. A manually exported UTF-8 JSON report uses batch-local item IDs by default and records no content hash. Full paths are included only after an explicit opt-in and second warning.

Reports can still reveal timestamps, device characteristics, error details, and—when enabled—sensitive names and locations. Store and share them accordingly.

## Residual risks

File identity checks and a complete pre-write rescan narrow time-of-check/time-of-use races but cannot make a mutable, multi-process operating system perfectly static. Power loss, surprise device removal, firmware behavior, filesystem corruption, hardware faults, and process termination can leave partially overwritten or renamed objects. Nyx must report the last confirmed stage and must never convert an uncertain state into `Shredded`.

Cloud reparse tags and the current user's standard OneDrive roots are detected conservatively. A fully local, non-placeholder file in a provider-specific or additional sync root can appear to Windows like an ordinary local file and may therefore lack the cloud-copy warning. This does not remove remote versions; users must treat any known synced location as limited assurance even when the UI cannot identify the provider.

Nyx blocks writers visible during inventory and rechecks identity, metadata, and stream sizes at destructive stage boundaries, but it does not hold a filesystem oplock across the whole multi-stream operation. A cooperating process can still race between those boundaries, especially with a same-length write or an existing memory mapping. Close applications using the target; copies or writes made by another process remain outside Nyx's guarantee.

For a read-only object, preflight verifies that the attribute and entry can be managed without clearing the attribute early. Windows does not provide the data-write handle until the attribute is cleared after confirmation, so an unusual ACL that permits attribute/delete access but denies file-data writes can still produce `FailedBeforeMutation` at execution time.
