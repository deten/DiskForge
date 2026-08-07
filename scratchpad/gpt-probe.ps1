# Does diskpart `convert gpt` succeed where Initialize-Disk loses the race?
# RUNNING_NOTES §204: `convert gpt` accepts ONLY an empty MBR disk. The earlier attempt failed because
# `clean` left the stick RAW. But Windows now auto-initializes it to empty MBR by itself — which is
# exactly the state `convert gpt` wants. Worth one direct test before writing any more code.
$ErrorActionPreference = 'Continue'
$n = 3

"=== before ==="
Get-Disk -Number $n | Format-List Number, PartitionStyle, OperationalStatus, IsReadOnly, Size
"partitions: " + ((Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Measure-Object).Count)

"=== attempt: diskpart convert gpt ==="
@"
select disk $n
convert gpt
exit
"@ | diskpart

"=== after convert ==="
Start-Sleep -Seconds 2
Update-HostStorageCache -ErrorAction SilentlyContinue
Get-Disk -Number $n | Format-List Number, PartitionStyle
"partitions: " + ((Get-Partition -DiskNumber $n -ErrorAction SilentlyContinue | Measure-Object).Count)

"=== does it survive a rescan / 10s settle? ==="
Start-Sleep -Seconds 10
Update-HostStorageCache -ErrorAction SilentlyContinue
"style now: " + (Get-Disk -Number $n).PartitionStyle
