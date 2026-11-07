<#
.SYNOPSIS
    Backs up the Keystone database.

.DESCRIPTION
    A full backup, verified, with the date in the name.

    The verify step is not optional and not a formality. A backup that cannot be read is
    worse than no backup, because it is the one you find out about on the day you need it.
    RESTORE VERIFYONLY reads the whole file back and checks its checksums, which catches a
    truncated or corrupted write at the time it happened rather than months later.

.EXAMPLE
    ./ops/backup.ps1
    ./ops/backup.ps1 -Server "prod-sql" -Database KeystonePortal -Path D:\backups
#>
[CmdletBinding()]
param(
    [string] $Server = '.\SQLEXPRESS',
    [string] $Database = 'KeystonePortal',
    [string] $Path = './backups',
    [int] $KeepDays = 30
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Path)) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

$full = (Resolve-Path $Path).Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$file = Join-Path $full "$Database-$stamp.bak"

Write-Host "Backing up $Database to $file"

# Express refuses BACKUP ... WITH COMPRESSION outright, so the edition decides. Asking is
# better than a switch somebody has to remember, and better than a script that only works
# on the machine it was written on.
$edition = (sqlcmd -S $Server -b -I -h -1 -W -Q `
    "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('EngineEdition') AS int);").Trim()

if ($LASTEXITCODE -ne 0) { throw "Could not reach $Server." }

# 4 is Express. Everything else compresses.
$compression = if ($edition -eq '4') { '' } else { ', COMPRESSION' }

if ($compression -eq '') {
    Write-Host "Express edition: compression not available."
}

# CHECKSUM makes the verify below meaningful: without it there is nothing to verify against.
$backup = @"
BACKUP DATABASE [$Database]
TO DISK = N'$file'
WITH FORMAT, INIT, CHECKSUM$compression, STATS = 25,
     NAME = N'$Database full backup';
"@

sqlcmd -S $Server -b -I -Q $backup
if ($LASTEXITCODE -ne 0) { throw "Backup failed with exit code $LASTEXITCODE." }

Write-Host "Verifying"

sqlcmd -S $Server -b -I -Q "RESTORE VERIFYONLY FROM DISK = N'$file' WITH CHECKSUM;"
if ($LASTEXITCODE -ne 0) { throw "The backup could not be verified. Treat it as lost." }

$size = [math]::Round((Get-Item $file).Length / 1MB, 2)
Write-Host "Verified. $size MB."

# Old backups go, but only after the new one has been verified.
if ($KeepDays -gt 0) {
    $cutoff = (Get-Date).AddDays(-$KeepDays)

    Get-ChildItem -Path $full -Filter "$Database-*.bak" |
        Where-Object { $_.LastWriteTime -lt $cutoff } |
        ForEach-Object {
            Write-Host "Removing $($_.Name), older than $KeepDays days"
            Remove-Item $_.FullName -Force
        }
}

Write-Host "Done."
