<#
.SYNOPSIS
  Diagnose WMI/RPC firewall state and optionally enable common rules required for remote WMI and UDP discovery.

.NOTES
  - Script will auto-elevate if not run as Administrator.
  - Logs are written to Diagnose-EnableWmiFirewall.log next to this script.
#>

param(
    [string]$Target = 'localhost',
    [switch]$EnableWmiGroup,
    [switch]$EnableRpcEndpoint,
    [switch]$AllowDiscoveryUdp,
    [int]$DiscoveryPort = 37020
)

# Ensure running elevated; if not, relaunch elevated and exit current process.
function Ensure-Elevated {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")
    if (-not $isAdmin) {
        $scriptPath = $MyInvocation.MyCommand.Path
        Write-Host "Not running as Administrator. Relaunching elevated..." -ForegroundColor Yellow
        Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" $($MyInvocation.BoundParameters.GetEnumerator() | ForEach-Object { "-$($_.Key) `"$($_.Value)`"" } -join ' ')" -Verb RunAs
        exit 0
    }
}

Ensure-Elevated

# Setup logging (transcript) to file next to script
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logPath = Join-Path $scriptDir "Diagnose-EnableWmiFirewall.log"

try {
    Start-Transcript -Path $logPath -Force -ErrorAction SilentlyContinue
} catch {
    # fallback: write a small note if transcript not available
    "Failed to start transcript: $($_.Exception.Message)" | Out-File -FilePath $logPath -Append -Encoding utf8
}

# Main
try {
    Write-Host "Target: $Target" -ForegroundColor Cyan

    Write-Host "`nTesting TCP 135 (RPC endpoint mapper) reachability..." -ForegroundColor Yellow
    try {
        $tcptest = Test-NetConnection -ComputerName $Target -Port 135 -WarningAction SilentlyContinue
        if ($tcptest.TcpTestSucceeded) {
            Write-Host "TCP 135 reachable from this machine to $Target" -ForegroundColor Green
        } else {
            Write-Host "TCP 135 NOT reachable to $Target. WMI/DCOM will fail if RPC is blocked." -ForegroundColor Red
            Write-Host "Details: $($tcptest | Out-String)"
        }
    } catch {
        Write-Host "Test-NetConnection failed: $($_.Exception.Message)" -ForegroundColor Red
    }

    Write-Host "`nInspecting Windows Management Instrumentation firewall rule group..." -ForegroundColor Yellow
    try {
        $wmiRules = Get-NetFirewallRule -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.DisplayGroup -and ($_.DisplayGroup -match 'WMI' -or $_.DisplayGroup -match 'Windows Management Instrumentation')) -or
                ($_.DisplayName -and ($_.DisplayName -match 'WMI' -or $_.DisplayName -match 'Windows Management Instrumentation'))
            }

        if ($wmiRules -and $wmiRules.Count -gt 0) {
            $enabledAny = $wmiRules | Where-Object { $_.Enabled -eq 'True' }
            if ($enabledAny) {
                Write-Host "WMI-related firewall rules present and some are enabled." -ForegroundColor Green
            } else {
                Write-Host "WMI-related firewall rules present but none are enabled." -ForegroundColor Red
            }
        } else {
            Write-Host "No obvious WMI-related firewall rules found by DisplayGroup/DisplayName. This may be a localized OS or custom policy." -ForegroundColor Yellow
        }
    } catch {
        Write-Host "Failed to inspect local firewall rules: $($_.Exception.Message)" -ForegroundColor Red
    }

    Write-Host "`nSearching for inbound UDP firewall rule allowing local port $DiscoveryPort..." -ForegroundColor Yellow
    $foundUdpRule = $null
    try {
        $inboundRules = Get-NetFirewallRule -Direction Inbound -Enabled True -ErrorAction SilentlyContinue
        foreach ($r in $inboundRules) {
            try {
                $pf = Get-NetFirewallPortFilter -AssociatedNetFirewallRule $r.Name -ErrorAction SilentlyContinue
                if ($pf) {
                    foreach ($p in $pf) {
                        if ($p.Protocol -match 'UDP') {
                            $lp = $p.LocalPort
                            if ($lp -eq 'Any' -or $lp -eq $DiscoveryPort.ToString()) {
                                $foundUdpRule = $r; break
                            }
                            if ($lp -match '^\d+-\d+$') {
                                $parts = $lp -split '-'
                                if ($DiscoveryPort -ge [int]$parts[0] -and $DiscoveryPort -le [int]$parts[1]) {
                                    $foundUdpRule = $r; break
                                }
                            }
                        }
                    }
                }
            } catch { continue }
            if ($foundUdpRule) { break }
        }

        if ($foundUdpRule) {
            Write-Host "Found an inbound UDP rule for port $DiscoveryPort: $($foundUdpRule.DisplayName)" -ForegroundColor Green
        } else {
            Write-Host "No inbound UDP rule found for port $DiscoveryPort." -ForegroundColor Red
        }
    } catch {
        Write-Host "Error while checking UDP rules: $($_.Exception.Message)" -ForegroundColor Red
    }

    # Optional enable/create rules
    if ($EnableWmiGroup) {
        Write-Host "`nEnabling WMI-related firewall rules..." -ForegroundColor Cyan
        try {
            Enable-NetFirewallRule -DisplayGroup "Windows Management Instrumentation (WMI)" -ErrorAction Stop
            Write-Host "WMI firewall group enabled (DisplayGroup)." -ForegroundColor Green
        } catch {
            $candidates = Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -match 'WMI' -or $_.DisplayGroup -match 'WMI' }
            if ($candidates) { $candidates | Enable-NetFirewallRule -ErrorAction SilentlyContinue; Write-Host "Enabled $($candidates.Count) WMI-related rules." -ForegroundColor Green }
            else { Write-Host "No WMI-related rules found to enable." -ForegroundColor Red }
        }
    }

    if ($EnableRpcEndpoint) {
        Write-Host "`nCreating inbound firewall rule for RPC endpoint mapper (TCP 135)..." -ForegroundColor Cyan
        try {
            if (-not (Get-NetFirewallRule -DisplayName "Allow RPC Endpoint Mapper - Scalae" -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName "Allow RPC Endpoint Mapper - Scalae" -Direction Inbound -Protocol TCP -LocalPort 135 -Action Allow -Profile Domain,Private -Enabled True
                Write-Host "Created rule: Allow RPC Endpoint Mapper - Scalae" -ForegroundColor Green
            } else {
                Write-Host "Rule already exists." -ForegroundColor Yellow
            }
        } catch {
            Write-Host "Failed to create RPC rule: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    if ($AllowDiscoveryUdp) {
        Write-Host "`nCreating inbound UDP rule for discovery port $DiscoveryPort..." -ForegroundColor Cyan
        try {
            if (-not (Get-NetFirewallRule -DisplayName "Scalae Discovery UDP" -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -DisplayName "Scalae Discovery UDP" -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort -Action Allow -Profile Domain,Private -Enabled True
                Write-Host "Created rule: Scalae Discovery UDP" -ForegroundColor Green
            } else {
                Write-Host "Rule 'Scalae Discovery UDP' already exists." -ForegroundColor Yellow
            }
        } catch {
            Write-Host "Failed to create UDP rule: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    Write-Host "`nDiagnostics complete. Log file: $logPath" -ForegroundColor Cyan
}
catch {
    Write-Host "Unhandled error: $($_.Exception.Message)" -ForegroundColor Red
    $_ | Out-String | Out-File -FilePath $logPath -Append -Encoding utf8
}
finally {
    try { Stop-Transcript } catch {}
    Read-Host -Prompt "Press Enter to close"
}