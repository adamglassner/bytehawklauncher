# Bytehawk Studio Launcher 1.0.0

A Windows 10/11 launcher and downloader for Bytehawk Studio software, with room for your own programs and web apps.

| Section | What it does |
|---|---|
| **Bytehawk Apps** | Software released by Bytehawk Studio. Downloads the latest release from GitHub Releases, checks for updates, installs and launches it. |
| **My Software** | Any program on your PC (.exe, .msi or .lnk shortcut), even if it has nothing to do with Bytehawk Studio. Works fully offline. |
| **Web Apps** | Bookmarks that open in your default browser. Nothing is downloaded or installed. |
| **Downloads** | Progress, speed, cancel and "show in folder" for this session's downloads. |
| **Settings** | Theme (Dark / Light / Follow System), download folder, check for updates on startup, remote catalog URL, About. |

Built with C# / WPF on .NET 8 and published as a self-contained single-file `.exe`, so the PC doesn't need .NET installed. Linux and macOS versions are coming soon (v1.0.0 is Windows only).

---

## Add a Bytehawk app by pasting a GitHub repo URL

1. Open **Bytehawk Apps** and click **Add Bytehawk app**.
2. Paste the repo as a URL (`https://github.com/owner/repo`) or as `owner/repo`. URLs that end in `.git` or `/releases` work too.
3. Click **Look up**. The launcher reads the repo's name and description from GitHub (public API, no login) and previews the latest release and which file it will download.
4. Set **Release asset to download**, for example:
   - `*.exe` for an installer or portable exe
   - `*.msi` for an MSI installer
   - `*.zip` for a portable zip (it's extracted to `apps\<AppName>\`)
   - Be more specific when a release has several files: `*-win-x64.zip`, `*Setup*.exe`
   - Use `;` to list several patterns (the first one that matches wins) and `!` to exclude: `*.exe;!*arm64*`
5. Click **Add app**.

The repo must be **public** and have at least one published (non-draft, non-prerelease) release. GitHub's `releases/latest` endpoint skips drafts and pre-releases.

### What happens on Download / Update

* The card shows *Installed x · Latest y* (or *Not installed*). When the latest tag is newer than the installed one, you get an **Update available** badge and an **Update** button.
* The download shows bytes downloaded, speed and %, and you can cancel it. Files go to the download folder (set it in Settings).
* **.exe**: runs it. The launcher then asks whether it was an installer. If it was, click **Choose launch file** on the card once setup finishes and pick the installed program. If not, Launch runs the downloaded exe.
* **.msi**: runs it. When setup finishes, click **Choose launch file** and pick the installed program.
* **.zip**: extracts it to `%APPDATA%\BytehawkStudio\Launcher\apps\<AppName>\`, opens that folder, and asks which `.exe` is the main program (if there's only one, it's picked automatically). Your choice is remembered across updates.
* The installed version (release tag) and install date are recorded in `installed.json`.
* If the launch file is later deleted or moved, the card shows **Not found — reinstall** instead of crashing.

### GitHub rate limits and offline use

Anonymous GitHub API calls are limited to 60 per hour. To stay under that, the launcher:
* makes one request per **repo**, not per card,
* caches release info in `cache\releases.json`, reuses it for 30 minutes on startup and for 60 seconds on a manual **Check for updates**,
* sends the cached `ETag`, so releases that haven't changed come back as `304 Not Modified`, which GitHub doesn't count against the limit,
* stops calling GitHub until the reset time once the limit is hit, and says so in the UI.

If GitHub can't be reached, the page shows *"Couldn't reach GitHub — showing last known versions"* and uses the cached data. **My Software** and **Web Apps** never need the network.

---

## catalog.json format (for editing by hand)

`%APPDATA%\BytehawkStudio\Launcher\catalog.json`

```json
{
  "version": 1,
  "apps": [
    {
      "id": "my-cool-app",
      "name": "My Cool App",
      "description": "What the app does, shown on the card.",
      "repo": "BytehawkStudio/my-cool-app",
      "assetPattern": "*.exe",
      "sample": false
    }
  ]
}
```

| Field | Required | Meaning |
|---|---|---|
| `repo` | yes | GitHub repository as `owner/repo`. |
| `name` | yes | Display name on the card. |
| `description` | no | Shown under the name. |
| `assetPattern` | no (default `*.exe`) | Which release file to download. Wildcards `*` and `?`, case-insensitive. Separate several patterns with `;`, and prefix with `!` to exclude. |
| `id` | no | Stable key for install state. Defaults to the lowercase `repo`. Set it if you list the same repo twice (for example an installer and a portable zip). |
| `sample` | no | `true` shows the yellow *Sample — replace me* badge. |
| `comingSoon` | no | `true` shows the card with a *Coming soon* badge and no Download button. `repo` can be empty, but give it an `id`. When the app is released, click **Edit** on the card and paste the repo; the card becomes a normal app. |

Comments (`//`) and trailing commas are allowed. After editing, go to **Settings → Reload catalog** (or restart the launcher). **Settings → Open catalog.json** opens the file in Notepad. If the file can't be parsed, the launcher keeps a copy as `catalog.json.bad` and tells you.

The catalog ships with nine **Coming soon** entries: Padcraft (word processor), ShoreDrop (FTP client), Sparecopy (backup), Fleetwatch (PC management), Shutterbox (photo management), Discshelf (music player), CoinPilot (budgeting), Sumthing (calculator) and Castaway (media player, which needs a Castaway server to host the media). It also ships with **two sample entries** labeled *"Sample — replace me"*. They point at a public open-source repo (Notepad++, not a Bytehawk Studio product) only so you can try the download flows: one uses the installer `.exe` and one uses the portable `.zip`. Delete them or replace their `repo` with a real Bytehawk Studio repo.

### Remote catalog URL (optional)

In **Settings → Remote catalog URL**, enter a URL that returns JSON in the same format, for example a raw GitHub URL such as `https://raw.githubusercontent.com/<owner>/<repo>/main/catalog.json`. On startup the launcher downloads it and shows those apps, plus any entries from your local `catalog.json` with a different `id`/`repo`. If the URL can't be reached, it uses the last downloaded copy, or just the local `catalog.json`. Entries from the remote catalog can't be edited or removed in the app.

---

## Where data is stored

Everything is under **`%APPDATA%\BytehawkStudio\Launcher\`** (Settings → About → **Open data folder**):

| File / folder | Contents |
|---|---|
| `settings.json` | Theme, download folder, check-on-startup, remote catalog URL |
| `catalog.json` | Bytehawk Apps catalog (hand-editable) |
| `installed.json` | Installed version, date, asset, and chosen launch file per app |
| `library.json` | My Software entries |
| `webapps.json` | Web Apps entries |
| `downloads\` | Default download folder (changeable in Settings) |
| `apps\` | Extracted `.zip` apps |
| `cache\` | Cached GitHub release info and the last remote catalog |
| `launcher.log` | Diagnostic log |

Uninstalling the launcher leaves this folder alone, and app updates never touch it. Removing a My Software entry only removes it from the launcher. The program file is never deleted.

---

## Launcher updates and releases

The launcher updates itself from **GitHub Releases on [`adamglassner/bytehawklauncher`](https://github.com/adamglassner/bytehawklauncher/releases)**.

* On startup (when *Check for updates on startup* is on), and when you click **Settings → About → Check for launcher updates**, it reads that repo's latest release. If the release tag is newer than the running version, a banner appears: *"Bytehawk Studio Launcher X is available."*
* **Update now** downloads the release's `BytehawkStudioLauncher-X-Setup.exe` with progress, runs it silently (`/SILENT /RELAUNCH=1`) and closes the launcher. The installer replaces the app and reopens it. Your data folder isn't touched. Windows asks for administrator permission because the launcher is installed under Program Files.
* The repo must stay **public**, because the launcher uses the anonymous GitHub API.

### Publishing a new version

GitHub Actions (`.github/workflows/build-release.yml`) does the build on a Windows machine. To release, change `<Version>` in `src/BytehawkStudioLauncher/BytehawkStudioLauncher.csproj` (for example to `1.0.1`) and push to `main`. Pushing a tag like `v1.0.1` works too.

When `main` has a version that hasn't been released yet, the workflow:
1. builds the single-file exe and the installer, stamped with that version (it shows in the window title, About and the installer),
2. runs a self-test that uses the real GitHub API, downloads and extracts real release assets, and checks URL validation and storage,
3. starts the app with the Dark, Light and System themes and checks that it doesn't crash and that the theme setting persists,
4. publishes a GitHub Release with `BytehawkStudioLauncher-1.0.1-Setup.exe` and `BytehawkStudioLauncher.exe`.

Every installed launcher then offers 1.0.1. Pushes that don't change the version, and pull requests, run steps 1–3 without releasing. The built files are attached to each run as artifacts.

You can run the self-test yourself: `BytehawkStudioLauncher.exe --self-test result.txt` writes PASS/FAIL lines to `result.txt`.

---

## Building

Requirements: Windows 10/11, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), and [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer (`winget install JRSoftware.InnoSetup`).

### One step

```powershell
.\build.ps1
```

This produces:
* `dist\BytehawkStudioLauncher.exe`: the self-contained single-file app
* `dist\BytehawkStudioLauncher-1.0.0-Setup.exe`: the installer

Use `.\build.ps1 -SkipInstaller` to build only the exe, or `.\build.ps1 -Version 1.0.1` to stamp a version.

> **"Could not find file …\obj" when building inside Documents?** Windows Security's *Controlled folder access* is blocking `dotnet.exe` from writing there. Build from a folder outside Documents (for example `C:\Dev\bytehawklauncher`), or allow `dotnet.exe` and `ISCC.exe` under Windows Security → Ransomware protection.

### By hand

```powershell
dotnet publish src\BytehawkStudioLauncher\BytehawkStudioLauncher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\publish
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DPublishDir="$PWD\dist\publish" installer\BytehawkStudioLauncher.iss
```

(If Inno Setup was installed per-user with winget, `ISCC.exe` is in `%LOCALAPPDATA%\Programs\Inno Setup 6\`.)

You can also open `BytehawkStudioLauncher.sln` in Visual Studio 2022 (17.8 or newer) and press F5.

### Installer details

* App name **Bytehawk Studio Launcher**, version **1.0.0**, publisher **Bytehawk Studio**, © Bytehawk Studio
* Installs to `Program Files\Bytehawk Studio\Launcher\`, or per-user if you choose that in the first dialog
* Start Menu shortcut, optional desktop shortcut (checked by default), and an uninstaller registered in **Settings → Apps → Installed apps** / **Add or Remove Programs**
* Asks you to close the launcher if it's running
* Uninstall removes the program and shortcuts and **keeps** `%APPDATA%\BytehawkStudio\Launcher\`

---

## Project layout

```
BytehawkStudioLauncher.sln
build.ps1                         publish + installer in one step
.github/workflows/                CI: build, self-test, UI smoke test, GitHub Release on v* tags
assets/                           logo: bytehawk.ico (16–256 px), PNG/SVG marks for dark & light backgrounds, app icon
installer/BytehawkStudioLauncher.iss
src/BytehawkStudioLauncher/
  App.xaml(.cs)                   startup, single instance, theme
  MainWindow.xaml(.cs)            sidebar, top bar, all five pages
  Models/Models.cs                JSON file shapes
  Services/GitHubService.cs       GitHub REST API, rate-limit handling, asset matching, version compare
  Services/Downloader.cs          HttpClient download with progress/speed/cancel
  Services/ShellHelper.cs         launching, Explorer, icon extraction
  Services/ThemeService.cs        Dark / Light / Follow System + dark title bar
  Services/Storage.cs             %APPDATA% paths, JSON store, log
  Services/LauncherUpdater.cs     self-update from adamglassner/bytehawklauncher releases
  Services/SelfTest.cs            --self-test checks used by CI
  ViewModels/ViewModels.cs        card view models
  Views/                          Add/Edit dialogs (Bytehawk app, software, web app)
  Themes/                         Dark.xaml, Light.xaml, Common.xaml (styles)
```

### Logo files (`assets/`)

* `bytehawk.ico`: multi-size Windows icon (16, 20, 24, 32, 40, 48, 64, 128, 256)
* `bytehawk-appicon-*.png` / `.svg`: the app icon (mark on a dark rounded tile), 64–1024 px
* `logo-mark-ondark-*.png` / `.svg`: the mark for dark backgrounds
* `logo-mark-onlight-*.png` / `.svg`: the mark for light backgrounds (the sidebar switches between the two with the theme)

© Bytehawk Studio
