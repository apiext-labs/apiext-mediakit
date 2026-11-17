# 探测本机对外 IPv4（安装向导预填用，纯 ASCII，兼容 PowerShell 5.1）
$ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object {
        $_.IPAddress -notlike '127.*' -and
        $_.IPAddress -notlike '169.254.*' -and
        $_.PrefixOrigin -ne 'WellKnown'
    } |
    Sort-Object InterfaceMetric | Select-Object -First 1).IPAddress

if (-not $ip) {
    $ip = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' } |
        Select-Object -First 1).IPAddress
}

if ($ip) { Write-Output $ip }
