param(
    [Parameter(Mandatory)][string]$Exe
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class IconProbe {
  delegate bool EnumProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);
  [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
  static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
  [DllImport("kernel32.dll")]
  static extern bool FreeLibrary(IntPtr module);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumProc callback, IntPtr param);
  public static int CountGroupIcons(string file) {
    // 0x22 = LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE; 14 = RT_GROUP_ICON
    IntPtr module = LoadLibraryEx(file, IntPtr.Zero, 0x22);
    if (module == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
    int count = 0;
    EnumResourceNames(module, (IntPtr)14, (m, t, n, p) => { count++; return true; }, IntPtr.Zero);
    FreeLibrary(module);
    return count;
  }
}
"@

$path = (Resolve-Path $Exe).Path
$groups = [IconProbe]::CountGroupIcons($path)
Write-Host "RT_GROUP_ICON resources in ${path}: $groups"
if ($groups -lt 1) { throw "$path has no embedded icon (is ApplicationIcon set in EveUtils.Client.csproj?)" }
