# DiskForge

A native Windows disk and partition suite: inspect drives, create and delete partitions, format to
Windows *and Linux* filesystems, and clone disks with verified copies.

DiskForge is built around one idea: a tool that can destroy a drive should be able to tell you exactly
what it is about to do, and prove afterwards that it did it. Every operation runs the same
Validate, Simulate, Execute, Verify pipeline, and destructive work is staged into a batch you review
before anything is written.

## What it does today

| Area | Capability |
|---|---|
| Inspect | Full topology: disks, partitions, volumes, capability profile, BitLocker state, USB link speed |
| Partition | Create on unallocated space, delete, set volume label, set drive letter |
| Format (Windows) | exFAT, NTFS, FAT32, in place or clean whole disk |
| Format (Linux) | ext2, ext3, ext4 written natively, plus btrfs, XFS, F2FS and swap |
| Partition table | Choose GPT or MBR when erasing a whole disk |
| Clone | Whole disk block copy with SHA-256 read back verification |
| Media test | Sequential and scattered write/read verification to prove a drive stores what it is given |

## Native ext2/ext3/ext4

DiskForge writes ext filesystems itself. No WSL, no bundled `e2fsprogs`, no external process. That
matters most on the drives people actually reach for, because Windows cannot hand a removable disk to
WSL at all.

Correctness is proved by real `e2fsck` rather than by our own reader agreeing with our own writer.
The test suite formats images from 16 MiB to 3 GiB and runs `e2fsck -fn` over each one, including
images pre-filled with garbage and images that already contain a complete ext4 filesystem, so a region
the writer fails to zero cannot hide behind a blank canvas.

btrfs, XFS and F2FS are written by the real `mkfs` tools through WSL2, with the resulting filesystem
verified by reading its superblock back off the drive.

## Safety model

- **Staged batches.** Destructive actions queue up. Nothing runs until you apply them, and the disk
  map previews the result before you commit.
- **Anti-wrong-target guards.** Every write operation refuses the system and boot disks, read-only and
  offline disks, EFI, MSR and recovery partitions, and BitLocker-protected volumes. Internal disks are
  refused by default and need an explicit acknowledgment.
- **Fresh re-validation.** Each operation re-checks against a new snapshot of the system immediately
  before writing, so a staged plan is never trusted.
- **Verification after the fact.** Formats read the filesystem signature back off the platter.
  Clones re-read the target and compare hashes.
- **Honest reporting.** Capabilities that have not been probed are reported as pending rather than
  assumed. If a disk ends up with a different partition table than requested, it says so.

## Getting started

Requires the .NET 8 SDK. Build x64, since disk interop is bitness sensitive.

```powershell
dotnet build DiskForge.sln
dotnet run --project src/DiskForge.Cli    # headless disk report
dotnet run --project src/DiskForge.App    # WPF dashboard
dotnet test                               # unit tests
```

To produce standalone executables that need no SDK installed:

```powershell
powershell -ExecutionPolicy Bypass -File build-exe.ps1
```

## Command line

```
diskforge                                   Read-only report of every disk
diskforge provision --disk <n> --yes        Erase a disk, set it to GPT, and lay down one partition
                                            per Linux filesystem with distinct labels
diskforge verify-media --disk <n>           Read-only surface scan
diskforge verify-media --disk <n> --write --yes
                                            Full write and read-back test. Each 4 KiB block carries
                                            its own offset, so a block returning another block's data
                                            is identified as aliasing
```

Commands that write require Administrator and refuse the system disk.

## Roadmap

Delivered:

- Phase 1, safety spine: the operation pipeline and staged batch model
- Phase 2, read-only enumeration, capability profiling, connection and link detection
- Phase 3, VHDX loopback test harness for real write-operation round trips
- Phase 4, write operations: format, create, delete, labels, drive letters
- Native ext2/ext3/ext4 writer
- Phase 8 first increment, whole disk clone with verified copy

Next up:

- Resize, extend and shrink partitions, with alignment and shrink-over-used-data guards
- Check and repair filesystems
- Convert between MBR and GPT in place, preserving data
- Native btrfs and XFS writers, so every Linux filesystem works on removable media
- Clone follow-ups: boot file rebuild, live snapshot clone, auto-grow, used-block clone

Later phases:

- Imaging: full disk and partition images to VHDX with a checksummed sidecar, and restore
- Capability-routed secure erase across ATA Secure Erase, NVMe Sanitize and TCG crypto-erase

## Project layout

| Project | Role |
|---|---|
| `DiskForge.Core` | Domain models, the `IDiskOperation` contract, staged queue and layout planner. No Windows dependencies, so it is cross-platform testable |
| `DiskForge.Engine` | The privileged worker: enumeration, native probes, Linux filesystem writers. All disk access lives here |
| `DiskForge.Cli` | Headless reporting, provisioning and media verification |
| `DiskForge.App` | WPF dashboard. Talks to the engine and never touches a disk directly |
| `DiskForge.Engine.Tests` | Unit tests plus the VHDX loopback harness for real round trips |

## License

Not yet licensed. All rights reserved for now.
