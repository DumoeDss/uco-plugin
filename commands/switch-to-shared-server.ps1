<#
.SYNOPSIS
    Switch one or more Unity-MCP plugin projects between the default
    "self-spawn server" (1:1:1) and a "shared server" (N:1:M) deployment.

.DESCRIPTION
    The Unity-MCP plugin defaults to spawning its own unity-mcp-server.exe
    sub-process when each Unity Editor starts (port = SHA256(projectPath)).
    To run multiple Unity Editors against a single shared server (and let
    multiple MCP clients all talk to that server), each project's
    UserSettings/AI-Game-Developer-Config.json needs:

        host                = <shared server URL>
        ConnectionMode      = Custom
        KeepServerRunning   = false   # do NOT spawn a local server
        KeepConnected       = true    # auto-connect on Editor open

    This script applies that switch (idempotently) and, when reverting,
    restores the defaults so the plugin again manages its own server.

    DLL sync is NOT performed by this script — use
    `sync-local-mcp-plugin.ps1` to push the Phase A/B McpPlugin.dll into
    each project's Assets/Plugins/NuGet/.

.PARAMETER SharedHost
    URL of the shared Unity-MCP-Server (e.g. http://localhost:7777).
    Required unless -Revert is set.

.PARAMETER Token
    Optional auth token to write into the config. Leave unset to keep
    the per-project token already in the file.

.PARAMETER Projects
    One or more Unity project root folders (the folder containing
    Assets/ and UserSettings/). Defaults to the Unity-MCP-Plugin folder
    that sits next to this script.

.PARAMETER Revert
    Restore plugin-managed server defaults:
      ConnectionMode    = Custom
      KeepServerRunning = true
      host              = http://localhost:<port hash> (cleared so plugin recomputes)

.PARAMETER DryRun
    Show what would change without writing.

.EXAMPLE
    .\commands\switch-to-shared-server.ps1 -SharedHost http://localhost:7777

.EXAMPLE
    .\commands\switch-to-shared-server.ps1 `
        -SharedHost http://localhost:7777 `
        -Projects D:\Projects\GameA, D:\Projects\GameB

.EXAMPLE
    .\commands\switch-to-shared-server.ps1 -Revert
#>

#Requires -Version 5.1
[CmdletBinding(DefaultParameterSetName = 'Switch')]
param(
    [Parameter(ParameterSetName = 'Switch', Mandatory = $true, Position = 0)]
    [string]$SharedHost,

    [Parameter(ParameterSetName = 'Switch')]
    [string]$Token,

    [Parameter(ParameterSetName = 'Revert', Mandatory = $true)]
    [switch]$Revert,

    [string[]]$Projects,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ── Default project list ─────────────────────────────────────────────────────
$scriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$unityMcpDir = Split-Path -Parent $scriptDir

if (-not $Projects -or $Projects.Count -eq 0) {
    $defaultProject = Join-Path $unityMcpDir 'Unity-MCP-Plugin'
    if (Test-Path $defaultProject) {
        $Projects = @($defaultProject)
        Write-Host "==> No -Projects passed, defaulting to: $defaultProject" -ForegroundColor DarkGray
    } else {
        throw "No -Projects passed and default '$defaultProject' does not exist."
    }
}

# ── URL validation (Switch only) ─────────────────────────────────────────────
if ($PSCmdlet.ParameterSetName -eq 'Switch') {
    $uri = $null
    if (-not [Uri]::TryCreate($SharedHost, [UriKind]::Absolute, [ref]$uri) -or
        ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
        throw "Invalid -SharedHost '$SharedHost'. Expected http(s):// URL."
    }
}

# ── Single-project mutation ──────────────────────────────────────────────────
function Update-OneProject {
    param(
        [string]$ProjectRoot,
        [string]$Mode,           # 'switch' | 'revert'
        [string]$NewHost,
        [string]$NewToken
    )

    $configPath = Join-Path $ProjectRoot 'UserSettings\AI-Game-Developer-Config.json'

    Write-Host ""
    Write-Host "-- $ProjectRoot" -ForegroundColor Cyan

    if (-not (Test-Path $configPath)) {
        Write-Host "   SKIP: config not found at $configPath" -ForegroundColor Yellow
        Write-Host "         Open the project in Unity once to create it, then rerun."
        return $false
    }

    # Backup
    $stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = "$configPath.$stamp.bak"
    if (-not $DryRun) {
        Copy-Item -Path $configPath -Destination $backup -Force
    }

    # Load
    $raw  = Get-Content -Path $configPath -Raw
    try {
        $cfg = $raw | ConvertFrom-Json
    } catch {
        Write-Host "   ERROR: config JSON is malformed — $($_.Exception.Message)" -ForegroundColor Red
        return $false
    }

    # Capture before
    $before = @{
        host              = $cfg.host
        ConnectionMode    = $cfg.ConnectionMode
        KeepServerRunning = $cfg.KeepServerRunning
        KeepConnected     = $cfg.KeepConnected
    }

    if ($Mode -eq 'switch') {
        $cfg.host              = $NewHost
        $cfg.ConnectionMode    = 'Custom'
        $cfg.KeepServerRunning = $false
        $cfg.KeepConnected     = $true
        if ($PSBoundParameters.ContainsKey('NewToken') -and -not [string]::IsNullOrEmpty($NewToken)) {
            $cfg.token = $NewToken
        }
    }
    else {
        # Revert. Clear host so the plugin's DefaultHost (port-from-hash) kicks back in
        # next time the Editor reads the config.
        $cfg.host              = ''
        $cfg.ConnectionMode    = 'Custom'
        $cfg.KeepServerRunning = $true
        $cfg.KeepConnected     = $true
    }

    # Diff print
    foreach ($k in 'host','ConnectionMode','KeepServerRunning','KeepConnected') {
        $oldVal = $before[$k]
        $newVal = $cfg.$k
        if ("$oldVal" -ne "$newVal") {
            "{0,-20} {1,-30} -> {2}" -f $k, "$oldVal", "$newVal" | Write-Host -ForegroundColor Green
        } else {
            "{0,-20} {1,-30}    (unchanged)" -f $k, "$oldVal" | Write-Host -ForegroundColor DarkGray
        }
    }

    if ($DryRun) {
        Write-Host "   (dry-run, not writing)" -ForegroundColor Yellow
        if (-not $DryRun) { Remove-Item $backup -ErrorAction SilentlyContinue }
        return $true
    }

    # Write. ConvertTo-Json default depth is 2 — bump it.
    $json = $cfg | ConvertTo-Json -Depth 32
    Set-Content -Path $configPath -Value $json -Encoding UTF8 -NoNewline:$false
    Write-Host "   wrote $configPath" -ForegroundColor Green
    Write-Host "   backup $backup"   -ForegroundColor DarkGray
    return $true
}

# ── Run ──────────────────────────────────────────────────────────────────────
$mode = if ($Revert) { 'revert' } else { 'switch' }
$ok = 0; $skip = 0; $fail = 0
foreach ($p in $Projects) {
    $resolved = Resolve-Path -Path $p -ErrorAction SilentlyContinue
    if (-not $resolved) {
        Write-Host "-- $p" -ForegroundColor Cyan
        Write-Host "   SKIP: path does not exist" -ForegroundColor Yellow
        $skip++
        continue
    }
    try {
        $changed = Update-OneProject `
            -ProjectRoot $resolved.ProviderPath `
            -Mode $mode `
            -NewHost $SharedHost `
            -NewToken $Token
        if ($changed) { $ok++ } else { $skip++ }
    } catch {
        Write-Host "   FAILED: $($_.Exception.Message)" -ForegroundColor Red
        $fail++
    }
}

# ── Summary + next-steps ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "Summary: $ok updated, $skip skipped, $fail failed." -ForegroundColor Cyan

if ($mode -eq 'switch' -and $ok -gt 0 -and -not $DryRun) {
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Start the shared server, e.g.:"
    Write-Host "       cd $unityMcpDir\Unity-MCP-Server" -ForegroundColor DarkGray
    Write-Host "       dotnet run -c Release -- port=$($uri.Port)" -ForegroundColor DarkGray
    Write-Host "       (NoAuthMcpStrategy.AllowMultipleConnections needs to be true — see Phase B follow-up.)"
    Write-Host "  2. (Once per project) Sync the local Phase A/B DLLs:"
    Write-Host "       .\commands\sync-local-mcp-plugin.ps1" -ForegroundColor DarkGray
    Write-Host "  3. Open each Unity project — plugin will auto-connect to $SharedHost."
    Write-Host "  4. From an MCP client, call 'mcp-session-instance-list' then 'mcp-session-instance-set <id>'."
}
elseif ($mode -eq 'revert' -and $ok -gt 0 -and -not $DryRun) {
    Write-Host ""
    Write-Host "Reverted. Next Editor open will re-spawn its own local server on the port-hashed URL." -ForegroundColor Cyan
}
