<#
.SYNOPSIS
    Support checks for a RingOrder EPOS till, without opening the app.

.DESCRIPTION
    Everything here is also in Settings -> Support. This exists for the times
    that screen cannot be reached: the app will not start, the shop is mid
    service and nobody can be asked to click through menus, or a remote session
    is being driven by someone who has never seen the till.

    Read-only by default. The two commands that write anything -- Backup and
    Restore -- say so and ask first.

    Ships beside the executable and is signed with it.

.PARAMETER Command
    Status   - version, data folder, schema, printers, queue, backups (default)
    Backup   - take a copy of the database now
    Restore  - queue a backup to be put back at the next start
    Logs     - tail the current log file
    Collect  - write one file with everything, to send to us
    Printers - list the Windows print queues and whether each can be opened

.EXAMPLE
    .\ringorder-support.ps1
    .\ringorder-support.ps1 Collect
    .\ringorder-support.ps1 Logs -Lines 200
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Backup', 'Restore', 'Logs', 'Collect', 'Printers')]
    [string]$Command = 'Status',

    [int]$Lines = 60,

    # Restore only. Omit to be shown the list and asked.
    [string]$BackupFile
)

$ErrorActionPreference = 'Stop'

# Machine-wide, and the same path every support script and the app itself use.
# A till that fell back to a per-user folder is a fault in its own right, so
# that case is reported rather than silently followed.
$Root       = Join-Path $env:ProgramData 'RingOrder\EPOS'
$Database   = Join-Path $Root 'data.sqlite'
$BackupDir  = Join-Path $Root 'backups'
$LogDir     = Join-Path $Root 'logs'
$ProfileDir = Join-Path $Root 'profile'
$Marker     = Join-Path $Root 'restore-pending.txt'

function Write-Heading([string]$Text) {
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
    Write-Host ('-' * 60)
}

function Get-PerUserFallback {
    # If this exists and the machine-wide one does not, the installer never set
    # the ProgramData permissions and the shop's data is invisible to support.
    $perUser = Join-Path $env:LOCALAPPDATA 'RingOrder\EPOS'
    if ((Test-Path $perUser) -and -not (Test-Path $Database)) { return $perUser }
    return $null
}

function Show-Status {
    Write-Heading 'RingOrder EPOS'

    if (-not (Test-Path $Root)) {
        Write-Host "No data folder at $Root" -ForegroundColor Red
        $fallback = Get-PerUserFallback
        if ($fallback) {
            Write-Host "Found a per-user copy at $fallback" -ForegroundColor Yellow
            Write-Host 'That means the machine-wide folder was not writable. Fix its'
            Write-Host 'permissions -- a second Windows account opens an empty till.'
        }
        return
    }

    Write-Host ("Data folder   {0}" -f $Root)

    if (Test-Path $Database) {
        $db = Get-Item $Database
        Write-Host ("Database      {0:N1} MB, last written {1:yyyy-MM-dd HH:mm}" -f ($db.Length / 1MB), $db.LastWriteTime)
    } else {
        Write-Host 'Database      MISSING' -ForegroundColor Red
    }

    # A -wal that is large and old usually means the app was killed rather than
    # closed. Harmless, but worth seeing when a shop reports "it lost an order".
    $wal = "$Database-wal"
    if (Test-Path $wal) {
        $w = Get-Item $wal
        if ($w.Length -gt 8MB) {
            Write-Host ("Write-ahead   {0:N1} MB -- larger than usual" -f ($w.Length / 1MB)) -ForegroundColor Yellow
        }
    }

    $running = Get-Process -Name 'RingOrder.Epos' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host ("Till          running, pid {0}, started {1:HH:mm}" -f $running.Id, $running.StartTime)
    } else {
        Write-Host 'Till          not running' -ForegroundColor Yellow
    }

    if (Test-Path $Marker) {
        $pending = (Get-Content $Marker -Raw).Trim()
        Write-Host ("RESTORE PENDING  {0}" -f (Split-Path $pending -Leaf)) -ForegroundColor Yellow
        Write-Host 'It will be applied the next time the till starts.'
    }

    if (Test-Path $ProfileDir) {
        $bundles = @(Get-ChildItem $ProfileDir -Filter '*.ringpos.json' -ErrorAction SilentlyContinue)
        Write-Host ("Shop bundle   {0}" -f $(if ($bundles.Count) { $bundles[0].Name } else { 'NONE -- till needs setting up' }))
    }

    Write-Heading 'Backups'
    if (Test-Path $BackupDir) {
        Get-ChildItem $BackupDir -Filter '*.sqlite' |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 8 |
            ForEach-Object {
                '{0,-46} {1,8:N1} MB  {2:yyyy-MM-dd HH:mm}' -f $_.Name, ($_.Length / 1MB), $_.LastWriteTime
            }
        $newest = Get-ChildItem $BackupDir -Filter 'daily-*.sqlite' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest -and $newest.LastWriteTime -lt (Get-Date).AddDays(-2)) {
            Write-Host ''
            Write-Host ("Newest nightly backup is {0:yyyy-MM-dd}. That is stale." -f $newest.LastWriteTime) -ForegroundColor Red
        }
    } else {
        Write-Host 'No backup folder.' -ForegroundColor Red
    }

    Write-Heading 'Recent errors'
    $log = Get-ChildItem $LogDir -Filter 'epos-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        $errors = @(Select-String -Path $log.FullName -Pattern 'ERROR|WARN' -ErrorAction SilentlyContinue | Select-Object -Last 10)
        if ($errors.Count) { $errors | ForEach-Object { $_.Line } }
        else { Write-Host 'None in the current log.' -ForegroundColor Green }
    } else {
        Write-Host 'No log file.' -ForegroundColor Yellow
    }
}

function Show-Printers {
    Write-Heading 'Windows print queues'
    # The everyday failure is a renamed or unplugged printer, and it is silent
    # until a ticket does not arrive.
    # Format-Table streams lazily, so its output can land after whatever is
    # printed next. Out-String forces it now, which matters because Collect
    # depends on these sections staying in order.
    Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue |
        Select-Object Name, PortName, WorkOffline, PrinterStatus |
        Format-Table -AutoSize | Out-String | Write-Host

    $offline = @(Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue |
        Where-Object { $_.WorkOffline })
    foreach ($p in $offline) {
        Write-Host ("{0} is offline. Tickets sent to it queue in Windows and never print." -f $p.Name) -ForegroundColor Red
    }

    $stuck = @(Get-CimInstance -ClassName Win32_PrintJob -ErrorAction SilentlyContinue)
    if ($stuck.Count) {
        Write-Host ("{0} job(s) sitting in the Windows spooler:" -f $stuck.Count) -ForegroundColor Yellow
        $stuck | Select-Object Name, JobStatus, TotalPages |
            Format-Table -AutoSize | Out-String | Write-Host
        Write-Host 'Clear them with: Restart-Service Spooler' -ForegroundColor Yellow
    } else {
        Write-Host 'Nothing stuck in the spooler.' -ForegroundColor Green
    }
}

function Invoke-Backup {
    if (-not (Test-Path $Database)) { throw "No database at $Database" }
    New-Item -ItemType Directory -Force $BackupDir | Out-Null

    $dest = Join-Path $BackupDir ('support-{0:yyyyMMdd-HHmmss}.sqlite' -f (Get-Date))

    # A plain file copy, unlike the app's VACUUM INTO, can catch a half-written
    # page while the till is trading. Say so rather than hand over a copy that
    # looks fine and is not.
    if (Get-Process -Name 'RingOrder.Epos' -ErrorAction SilentlyContinue) {
        Write-Host 'The till is running. Use "Back up now" in Settings -> Support instead:' -ForegroundColor Yellow
        Write-Host 'it reads through the write-ahead log, and a file copy taken now can'
        Write-Host 'capture a half-written page.'
        $answer = Read-Host 'Copy anyway? (y/N)'
        if ($answer -ne 'y') { return }
    }

    Copy-Item $Database $dest
    Write-Host "Copied to $dest" -ForegroundColor Green
}

function Invoke-Restore {
    if (-not (Test-Path $BackupDir)) { throw "No backup folder at $BackupDir" }

    if (-not $BackupFile) {
        Write-Heading 'Choose a backup'
        $files = @(Get-ChildItem $BackupDir -Filter '*.sqlite' | Sort-Object LastWriteTime -Descending)
        if (-not $files.Count) { throw 'There are no backups.' }

        for ($i = 0; $i -lt $files.Count; $i++) {
            '{0,3}. {1,-46} {2:yyyy-MM-dd HH:mm}' -f ($i + 1), $files[$i].Name, $files[$i].LastWriteTime
        }
        $pick = Read-Host 'Number (blank to cancel)'
        if (-not $pick) { return }
        $BackupFile = $files[[int]$pick - 1].FullName
    }

    if (-not (Test-Path $BackupFile)) { throw "No such backup: $BackupFile" }

    Write-Host ''
    Write-Host 'Everything taken since that backup was made will be gone.' -ForegroundColor Red
    Write-Host 'The live database is kept first, so this can be undone.'
    Write-Host 'Nothing happens until the till is restarted.'
    $answer = Read-Host 'Type RESTORE to confirm'
    if ($answer -ne 'RESTORE') { Write-Host 'Cancelled.'; return }

    # The same marker the app writes. The swap is done by the till at startup,
    # before anything opens the database -- never by this script, which cannot
    # know whether the till is holding the file.
    Set-Content -Path $Marker -Value $BackupFile -Encoding utf8
    Write-Host ''
    Write-Host 'Queued. Restart the till to apply it.' -ForegroundColor Green
}

function Show-Logs {
    $log = Get-ChildItem $LogDir -Filter 'epos-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { Write-Host 'No log file.'; return }

    Write-Heading $log.Name
    Get-Content $log.FullName -Tail $Lines
}

function Invoke-Collect {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $out = Join-Path ([Environment]::GetFolderPath('Desktop')) "ringorder-diagnostics-$stamp.txt"

    # Transcript rather than piping the functions: they report through
    # Write-Host, which goes to the console and not to the pipeline, so
    # `Show-Status | Out-String` collects an empty string and the file that
    # reaches us says nothing. Found by running it.
    Start-Transcript -Path $out -Force | Out-Null
    try {
        Write-Host "RingOrder EPOS diagnostics, $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        Write-Host "Machine: $env:COMPUTERNAME   User: $env:USERNAME"
        Show-Status
        Show-Printers
        Write-Heading 'Recent log'
        Show-Logs
    }
    finally {
        Stop-Transcript | Out-Null
    }

    Write-Host "Written to $out" -ForegroundColor Green
    Write-Host 'Send that file to us.'
}

switch ($Command) {
    'Status'   { Show-Status }
    'Backup'   { Invoke-Backup }
    'Restore'  { Invoke-Restore }
    'Logs'     { Show-Logs }
    'Collect'  { Invoke-Collect }
    'Printers' { Show-Printers }
}
