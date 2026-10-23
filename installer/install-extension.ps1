# Extension setup. The recommended way is the Chrome Web Store (Chrome, Edge and Brave can all use it; Edge first asks to allow extensions from other stores).
# The manual way (Load unpacked) opens the extension folder and each installed browser's extensions page.
Add-Type -AssemblyName System.Windows.Forms
$store = 'https://chromewebstore.google.com/detail/ojbaibjfbdjodgmfbiajkigpanbdkiid'
$answer = [Windows.Forms.MessageBox]::Show("Install NeoDM Integration from the Chrome Web Store?`n`nYes: open the store page in your default browser (recommended, the store keeps it up to date).`nNo: install it manually from the files in the NeoDM folder.", 'NeoDM browser extension', [Windows.Forms.MessageBoxButtons]::YesNoCancel)
if ($answer -eq [Windows.Forms.DialogResult]::Yes) { Start-Process $store; return }
if ($answer -ne [Windows.Forms.DialogResult]::No) { return }
$dir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'extension'
Start-Process explorer.exe "/select,`"$dir\NeoDM.crx`""
Start-Process explorer.exe "`"$dir\unpacked`""
$browsers = @(
  @{ Exe = 'chrome.exe'; Url = 'chrome://extensions' },
  @{ Exe = 'msedge.exe'; Url = 'edge://extensions' },
  @{ Exe = 'brave.exe';  Url = 'brave://extensions' })
foreach ($b in $browsers) {
  $p = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$($b.Exe)" -ErrorAction SilentlyContinue).'(default)'
  if ($p -and (Test-Path -LiteralPath $p)) { Start-Process -FilePath $p -ArgumentList $b.Url }
}
[void][Windows.Forms.MessageBox]::Show("1. Turn on Developer mode on the extensions page.`n2. Click 'Load unpacked' and choose the 'unpacked' folder that was just opened.`n   (Dragging NeoDM.crx onto the page does not work on most PCs: browsers refuse files from outside their store.)", 'NeoDM browser extension')
