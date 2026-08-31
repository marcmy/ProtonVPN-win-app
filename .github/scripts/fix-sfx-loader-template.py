from pathlib import Path
import re

path = Path('scripts/New-ProtonVPNPatchSfx.ps1')
text = path.read_text(encoding='utf-8-sig')
old = '''    Add-Type @'\nusing System;\nusing System.Runtime.InteropServices;\npublic static class FastPatchConsoleWindow {\n    [DllImport(\\"kernel32.dll\\")] public static extern IntPtr GetConsoleWindow();\n    [DllImport(\\"user32.dll\\")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);\n}\n'@\n'''
new = '''    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public static class FastPatchConsoleWindow { [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow(); [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow); }'\n'''
if text.count(old) != 1:
    raise RuntimeError(f'nested Add-Type block count was {text.count(old)}, expected 1')
text = text.replace(old, new, 1)
pattern = re.compile(r"(?m)^    \$sfxLaunchCommand = .*\+ \$encodedLoader$")
replacement = r'''    $sfxLaunchCommand = '"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ' + $encodedLoader'''
text, count = pattern.subn(lambda _: replacement, text, count=1)
if count != 1:
    raise RuntimeError(f'SFX launch command count was {count}, expected 1')
path.write_text(text, encoding='utf-8', newline='\n')
print('SFX loader template syntax corrected.')
