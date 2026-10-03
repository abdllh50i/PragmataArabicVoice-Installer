# PRAGMATA Arabic Voice-Over — Installer

Small optional helper (C# / WinForms, ~50 KB) for the **Arabic Voice-Over** mod for PRAGMATA on Nexus Mods.
It only copies the mod file into the game folder — the same thing a manual install does.

## What it does
1. Finds the game folder (reads Steam's install path from `HKCU\Software\Valve\Steam` → `SteamPath` and
   `steamapps\libraryfolders.vdf`, read-only; or the user picks the folder).
2. Finds `PragmataArabicVoice.pak` that the user downloaded from Nexus (next to the installer, in neighbouring
   folders, Downloads or Desktop; or the user picks the file).
3. Checks that REFramework (`dinput8.dll`) is present; if not, a button opens the REFramework Nexus page in the browser.
4. Copies the pak to `<game>\pak_mods\PragmataArabicVoice.pak` and writes a small marker
   `<game>\pak_mods\PragmataArabicVoice.install.json` (version + date).
5. **Uninstall** deletes those two files.

## What it does NOT do
- **No network access at all** — no downloads, no update checks, no telemetry (there is no networking code in `src/Installer.cs`).
- No registry writes, no services, no scheduled tasks, no files outside the game folder.
- Runs as a normal user; it only asks for administrator rights if the game folder itself is not writable
  (for example when the game is installed under Program Files).

## Build
Windows 10/11 already include the compiler (.NET Framework 4.x). Run:

```
build.bat
```

This produces `PragmataArabicVoice-Installer.exe` from `src/Installer.cs`, `app.manifest` and `icon.ico`.

## Command line (testing)
`PragmataArabicVoice-Installer.exe --silent --path "<game folder>" [--uninstall]` — no UI, log in `%TEMP%\PragmataArabicInstaller.log`.

## License
MIT — see `LICENSE`.
