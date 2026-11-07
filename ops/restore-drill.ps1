<#
.SYNOPSIS
    Restores a backup into a throwaway database and checks the data is really there.

.DESCRIPTION
    An untested backup is a rumour. This restores the most recent one under a different
    name, counts what came back, and drops it again — so the restore path is exercised
    without touching anything live.

    Run it after any change to the backup arrangements, and on a schedule. The failure this
    catches is the one that matters: a backup job that has been "succeeding" for months
    against a database it stopped including.

.EXAMPLE
    ./ops/restore-drill.ps1
#>
[CmdletBinding()]
param(
    [string] $Server = '.\SQLEXPRESS',
    [string] $Database = 'KeystonePortal',
    [string] $Path = './backups',
    [switch] $KeepRestored
)

$ErrorActionPreference = 'Stop'

$backup = Get-ChildItem -Path $Path -Filter "$Database-*.bak" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if (-not $backup) { throw "No backup found in $Path." }

$target = "${Database}_DrillRestore"

Write-Host "Restoring $($backup.Name) as $target"

# MOVE is required: the backup carries the original file paths, and restoring without it
# would try to overwrite the live database's own files.
#
# Parsed with an explicit column separator rather than on whitespace. The physical path is
# one of the columns and it contains spaces on any default install — "Program Files" — so
# splitting on whitespace finds the type column in the wrong place and silently matches
# nothing.
$files = sqlcmd -S $Server -b -I -h -1 -W -s'|' -Q `
    "SET NOCOUNT ON; RESTORE FILELISTONLY FROM DISK = N'$($backup.FullName)';"

if ($LASTEXITCODE -ne 0) { throw "Could not read the backup's file list." }

$parsed = $files |
    Where-Object { $_ -match '\|' } |
    ForEach-Object { $columns = $_.Split('|'); [pscustomobject]@{ Name = $columns[0]; Type = $columns[2] } }

$data = ($parsed | Where-Object { $_.Type -eq 'D' } | Select-Object -First 1).Name
$log = ($parsed | Where-Object { $_.Type -eq 'L' } | Select-Object -First 1).Name

if (-not $data -or -not $log) { throw "Could not identify the data and log files in the backup." }

$folder = Split-Path $backup.FullName -Parent

$restore = @"
IF DB_ID('$target') IS NOT NULL
BEGIN
    ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [$target];
END

RESTORE DATABASE [$target]
FROM DISK = N'$($backup.FullName)'
WITH MOVE N'$data' TO N'$folder\$target.mdf',
     MOVE N'$log'  TO N'$folder\$target.ldf',
     RECOVERY, STATS = 25;
"@

sqlcmd -S $Server -b -I -Q $restore
if ($LASTEXITCODE -ne 0) { throw "The restore failed." }

Write-Host ""
Write-Host "Restored. What came back:"

# Counting rows is the point. A database that restores but is empty has still lost
# everything, and only a count says so.
$counts = @"
SET NOCOUNT ON;
SELECT 'Projects', COUNT(*) FROM [$target].dbo.Projects
UNION ALL SELECT 'Units', COUNT(*) FROM [$target].dbo.Units
UNION ALL SELECT 'Reservations', COUNT(*) FROM [$target].dbo.Reservations
UNION ALL SELECT 'Holds', COUNT(*) FROM [$target].dbo.Holds
UNION ALL SELECT 'Prospects', COUNT(*) FROM [$target].dbo.Prospects
UNION ALL SELECT 'AuditLogs', COUNT(*) FROM [$target].dbo.AuditLogs
UNION ALL SELECT 'Migrations applied', COUNT(*) FROM [$target].dbo.__EFMigrationsHistory;
"@

sqlcmd -S $Server -b -I -W -Q $counts
if ($LASTEXITCODE -ne 0) { throw "The restored database could not be read." }

if ($KeepRestored) {
    Write-Host ""
    Write-Host "Left in place as $target. Drop it when you are finished."
}
else {
    sqlcmd -S $Server -b -I -Q @"
ALTER DATABASE [$target] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
DROP DATABASE [$target];
"@
    Write-Host ""
    Write-Host "Drill database dropped."
}

Write-Host "Restore drill passed."
