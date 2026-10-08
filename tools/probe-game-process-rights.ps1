param([Parameter(Mandatory)][int]$ProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AxiomProcessRightsProbe {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetProcessTimes(IntPtr handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("advapi32.dll", SetLastError=true)] public static extern bool OpenProcessToken(IntPtr handle, uint access, out IntPtr token);
}
'@
foreach ($access in @(0x1000,0x100000,0x101000,0x400)) {
    $handle = [AxiomProcessRightsProbe]::OpenProcess([uint32]$access, $false, $ProcessId)
    if ($handle -eq [IntPtr]::Zero) {
        [pscustomobject]@{ Access=('0x{0:X}' -f $access); Open=$false; Error=[Runtime.InteropServices.Marshal]::GetLastWin32Error() }
        continue
    }
    try {
        $path = [Text.StringBuilder]::new(32768)
        [uint32]$length = $path.Capacity
        $image = [AxiomProcessRightsProbe]::QueryFullProcessImageName($handle, 0, $path, [ref]$length)
        $imageError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        [long]$created = 0; [long]$exited = 0; [long]$kernel = 0; [long]$user = 0
        $times = [AxiomProcessRightsProbe]::GetProcessTimes($handle, [ref]$created, [ref]$exited, [ref]$kernel, [ref]$user)
        $timesError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $token = [IntPtr]::Zero
        $tokenOpened = [AxiomProcessRightsProbe]::OpenProcessToken($handle, 8, [ref]$token)
        $tokenError = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($token -ne [IntPtr]::Zero) { [AxiomProcessRightsProbe]::CloseHandle($token) | Out-Null }
        [pscustomobject]@{ Access=('0x{0:X}' -f $access); Open=$true; Image=if($image){$path.ToString()}else{"error:$imageError"}; Times=if($times){[DateTime]::FromFileTimeUtc($created)}else{"error:$timesError"}; Token=if($tokenOpened){'opened'}else{"error:$tokenError"} }
    }
    finally { [AxiomProcessRightsProbe]::CloseHandle($handle) | Out-Null }
}
