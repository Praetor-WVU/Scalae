<#
.SYNOPSIS
  Create a program-scoped firewall rule that allows the Scalae agent to receive UDP discovery packets and optional HTTP metric requests.

.NOTES
  - Run this from an elevated PowerShell prompt.
  - A log file 'Install-ScalaeAgentFirewallRule.log' is created next to this script with full errors.
  - This script is tolerant if you pass a directory instead of the exact exe path: it will try to locate 'ScalaeAgent.exe' inside the directory.
#>

param(
    [Parameter(Mandatory = $true)] [string]$AgentExePath,
    [int]$DiscoveryPort = 37020,
    [int]$HttpPort = 37021
)

# Helper: log to console + file
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logPath = Join-Path $scriptDir 'Install-ScalaeAgentFirewallRule.log'
function Log {
    param([string]$msg)
    $time = (Get-Date).ToString('o')
    $line = "$time`t$msg"
    Add-Content -Path $logPath -Value $line -Encoding UTF8
    Write-Host $msg
}

# Ensure elevated
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Log "ERROR: Script must be run as Administrator. Aborting."
    exit 10
}

Log "Starting Scalae agent firewall rule installer."
Log "Requested AgentExePath = $AgentExePath; DiscoveryPort = $DiscoveryPort; HttpPort = $HttpPort"

# Resolve / normalize input
$resolvedExe = $null
try {
    $literalResolved = Resolve-Path -LiteralPath $AgentExePath -ErrorAction SilentlyContinue
    if ($literalResolved) {
        $resolvedExe = $literalResolved.ProviderPath
    }
} catch {
    # ignore resolve errors
}

# If Resolve-Path succeeded and points to a file, use it
if ($resolvedExe -and (Test-Path $resolvedExe -PathType Leaf)) {
    $AgentExePath = $resolvedExe
    Log "Resolved AgentExePath to file: $AgentExePath"
} else {
    # If the provided value is an existing directory, search for ScalaeAgent.exe inside it
    if (Test-Path $AgentExePath -PathType Container) {
        Log "AgentExePath is a directory — searching for 'ScalaeAgent.exe' inside: $AgentExePath"
        $candidate = Get-ChildItem -Path $AgentExePath -Filter 'ScalaeAgent.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) {
            $AgentExePath = $candidate.FullName
            Log "Found agent executable: $AgentExePath"
        } else {
            Log "No ScalaeAgent.exe found under the directory. Listing top-level contents for debugging:"
            Get-ChildItem -Path $AgentExePath -Force -ErrorAction SilentlyContinue | ForEach-Object { Log ("  " + $_.Name) }
            Log "ERROR: Agent executable not found in provided directory."
            exit 1
        }
    } else {
        # If the literal path did not exist as a file, try searching the provided path as a parent folder (common when user points to publish folder path without trailing slash)
        $maybeDir = $AgentExePath
        if (-not [IO.Path]::HasExtension($maybeDir)) {
            # treat as directory-like — try search
            Log "Provided path did not resolve to a file. Attempting to search path for 'ScalaeAgent.exe': $maybeDir"
            $candidate = Get-ChildItem -Path $maybeDir -Filter 'ScalaeAgent.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($candidate) {
                $AgentExePath = $candidate.FullName
                Log "Found agent executable: $AgentExePath"
            } else {
                Log "Search failed. Will attempt a one-level-up search in parent directory."
                $parent = Split-Path -Parent $AgentExePath
                if ($parent -and (Test-Path $parent -PathType Container)) {
                    $candidate = Get-ChildItem -Path $parent -Filter 'ScalaeAgent.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
                    if ($candidate) {
                        $AgentExePath = $candidate.FullName
                        Log "Found agent executable in parent: $AgentExePath"
                    } else {
                        Log "No executable found in parent. Aborting."
                        exit 1
                    }
                } else {
                    Log "Provided path does not exist and parent directory unavailable. Aborting."
                    exit 1
                }
            }
        } else {
            # Path had an extension but did not exist
            Log "ERROR: Provided path points to a file that does not exist: $AgentExePath"
            exit 1
        }
    }

# Final validation: ensure we now have an exe file
if (-not (Test-Path $AgentExePath -PathType Leaf)) {
    Log "ERROR: Final AgentExePath does not point to a file: $AgentExePath"
    exit 1
}

# Optional: ensure it's an .exe
if ([IO.Path]::GetExtension($AgentExePath).ToLowerInvariant() -ne '.exe') {
    Log "WARNING: AgentExePath extension is not .exe. Proceeding but verify this is the correct executable: $AgentExePath"
}

$displayNameUdp = "Scalae Agent Discovery (Program rule)"
$displayNameHttp = "Scalae Agent HTTP (Program rule)"

try {
    # Create UDP program-scoped rule
    if (-not (Get-NetFirewallRule -DisplayName $displayNameUdp -ErrorAction SilentlyContinue)) {
        Log "Creating UDP program rule: $displayNameUdp (Program: $AgentExePath, Port: $DiscoveryPort)"
        New-NetFirewallRule -DisplayName $displayNameUdp `
            -Direction Inbound `
            -Program $AgentExePath `
            -Protocol UDP `
            -LocalPort $DiscoveryPort `
            -Action Allow `
            -Profile Domain,Private `
            -Enabled True -ErrorAction Stop
        Log "Created firewall rule: $displayNameUdp"
    } else {
        Log "Firewall rule already exists: $displayNameUdp"
    }

    # Create HTTP program-scoped rule if requested
    if ($HttpPort -ne 0) {
        if (-not (Get-NetFirewallRule -DisplayName $displayNameHttp -ErrorAction SilentlyContinue)) {
            Log "Creating TCP program rule: $displayNameHttp (Program: $AgentExePath, Port: $HttpPort)"
            New-NetFirewallRule -DisplayName $displayNameHttp `
                -Direction Inbound `
                -Program $AgentExePath `
                -Protocol TCP `
                -LocalPort $HttpPort `
                -Action Allow `
                -Profile Domain,Private `
                -Enabled True -ErrorAction Stop
            Log "Created firewall rule: $displayNameHttp"
        } else {
            Log "Firewall rule already exists: $displayNameHttp"
        }
    }

    Log "Completed successfully."
    exit 0
}
catch {
    $err = $_.Exception | Out-String
    Log "FAILED to create firewall rule. Exception details:"
    Add-Content -Path $logPath -Value $err -Encoding UTF8
    Write-Error "Failed to create firewall rule. See log: $logPath"
    exit 2
}