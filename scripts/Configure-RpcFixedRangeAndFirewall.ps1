<#
.SYNOPSIS
  Configure a fixed RPC port range and create minimal firewall rules for WMI/DCOM.

.NOTES
  - MUST run as Administrator.
  - System restart required for RPC port-range registry changes to take effect.
  - Choose a port range that is not in use and allowed by your network policies.
#>

param(
    [string]$RpcPortRange = "50000-50010",
    [int]$DiscoveryPort = 37020,              # UDP discovery port (optional)
    [switch]$CreateDiscoveryRule               # Create UDP discovery rule if set
)

function Require-Admin {
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] "Administrator")) {
        Write-Error "This script must be run as Administrator."
        exit 1
    }
}

Require-Admin

Write-Host "Configuring RPC fixed port range: $RpcPortRange" -ForegroundColor Cyan

# 1) Create RPC Internet parameters key and set ports
$rpcParamsPath = "HKLM:\SYSTEM\CurrentControlSet\Services\Rpc\Parameters\Internet"
if (-not (Test-Path $rpcParamsPath)) {
    New-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Services\Rpc\Parameters" -Name "Internet" -Force | Out-Null
}

# Split range into lines (MultiString)
$ports = $RpcPortRange -split ';' -replace '\s+',''
# If a single range like 50000-50010, leave as one entry
$ports = @($RpcPortRange)

Set-ItemProperty -Path $rpcParamsPath -Name "Ports" -Value $ports -Type MultiString -Force
Set-ItemProperty -Path $rpcParamsPath -Name "PortsInternetAvailable" -Value "Y" -Type String -Force
Set-ItemProperty -Path $rpcParamsPath -Name "UseInternetPorts" -Value "Y" -Type String -Force

Write-Host "Registry values written under $rpcParamsPath. A reboot is required for changes to apply." -ForegroundColor Yellow

# 2) Create firewall rule for RPC endpoint mapper (TCP 135)
if (-not (Get-NetFirewallRule -DisplayName "Allow RPC Endpoint Mapper - Scalae" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "Allow RPC Endpoint Mapper - Scalae" -Direction Inbound -Protocol TCP -LocalPort 135 -Action Allow -Profile Domain,Private -Enabled True
    Write-Host "Created firewall rule for TCP 135." -ForegroundColor Green
} else {
    Write-Host "Firewall rule for TCP 135 already exists." -ForegroundColor Yellow
}

# 3) Create firewall rule for the fixed RPC port range
if (-not (Get-NetFirewallRule -DisplayName "Allow RPC Fixed Range - Scalae" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "Allow RPC Fixed Range - Scalae" -Direction Inbound -Protocol TCP -LocalPort $RpcPortRange -Action Allow -Profile Domain,Private -Enabled True
    Write-Host "Created firewall rule for RPC port range $RpcPortRange." -ForegroundColor Green
} else {
    Write-Host "Firewall rule for RPC fixed range already exists." -ForegroundColor Yellow
}

# 4) Optionally create discovery UDP rule for your agent (port 37020 by default)
if ($CreateDiscoveryRule) {
    if (-not (Get-NetFirewallRule -DisplayName "Scalae Discovery UDP" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "Scalae Discovery UDP" -Direction Inbound -Protocol UDP -LocalPort $DiscoveryPort -Action Allow -Profile Domain,Private -Enabled True
        Write-Host "Created rule: Scalae Discovery UDP (port $DiscoveryPort)." -ForegroundColor Green
    } else {
        Write-Host "Discovery UDP rule already exists." -ForegroundColor Yellow
    }
}

Write-Host "`nDONE. Reboot the machine to apply RPC port-range changes (required)." -ForegroundColor Cyan