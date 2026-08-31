from pathlib import Path
import re

path = Path('scripts/New-ProtonVPNPatchSfx.ps1')
text = path.read_text(encoding='utf-8-sig')
pattern = re.compile(
    r"    Add-Type @'\n"
    r"using System;\n"
    r"using System\.Runtime\.InteropServices;\n"
    r"public static class FastPatchConsoleWindow \{.*?\n"
    r"\}\n"
    r"'@\n",
    re.S)
new = '''    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FastPatchConsoleWindow { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); }'\n'''
text, count = pattern.subn(lambda _: new, text, count=1)
if count != 1:
    raise RuntimeError(f'nested Add-Type block count was {count}, expected 1')
pattern = re.compile(r"(?m)^    \$sfxLaunchCommand = .*\+ \$encodedLoader$")
replacement = r'''    $sfxLaunchCommand = '"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader'''
text, count = pattern.subn(lambda _: replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'SFX launch command count was {count}, expected 1')
path.write_text(text, encoding='utf-8', newline='\n')
print('SFX loader template syntax corrected.')
