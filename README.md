# Bytehawk Studio Launcher

A Windows 10/11 launcher and downloader for Bytehawk Studio software, with room for your own programs and web apps.

| Section | What it does |
|---|---|
| **Bytehawk Apps** | Software released by Bytehawk Studio. Downloads the latest release from GitHub Releases, installs it silently, keeps it up to date automatically, launches and uninstalls it. The first app is **Guardrail EHS** ([adamglassner/guardrailehs](https://github.com/adamglassner/guardrailehs)). |
| **Bytehawk Servers** | Servers you run yourself for Bytehawk apps: **Guardrail Server** (team sync for Guardrail EHS, from one PC up to a cluster; since 1.7.0) and **WillowWallet Server** (sync for WillowWallet on your PC and phone). Install it on this PC as a Windows service, open its web panel, update and uninstall it (keeping or deleting its data), or copy the Docker command and get the Linux builds. |
| **Browser Extensions** | Bytehawk Studio extensions for Google Chrome and Microsoft Edge, starting with **AlbumGrab** ([adamglassner/AlbumGrab](https://github.com/adamglassner/AlbumGrab)), which saves photos and albums from Facebook, Instagram, Reddit and other sites. **Download** unpacks it into a folder that keeps the same path, and **Add to browser** opens Chrome's or Edge's Extensions page with the folder copied, plus the steps for *Load unpacked*. Updates are installed into the same folder automatically, so the browser picks them up. |
| **My Software** | Any program on your PC (.exe, .msi or .lnk shortcut) or any **Microsoft Store app** (**Add Store app** lists what's installed), even if it has nothing to do with Bytehawk Studio. Each entry has an optional description, version (filled in automatically, editable) and category. Works fully offline. |
| **Web Apps** | Bookmarks that open in your default browser, with an optional description and category. Each card shows the site's own icon (downloaded once and cached in `cache\icons\`; use the refresh button on the card to fetch it again). Nothing is downloaded or installed. |
| **Downloads** | Progress, speed, cancel and "show in folder" for this session's downloads. |
| **Settings** | Theme (Dark / Light / Follow System), download folder, update checks, automatic app updates, remote catalog URL, About. |

Built with C# / WPF on .NET 8 and published as a self-contained single-file `.exe`, so the PC doesn't need .NET installed. Linux and macOS versions are coming soon (v1.0.0 is Windows only).

**Categories:** give My Software and Web Apps entries a category (Productivity, Education, Games and so on, or type your own). Cards are grouped under category headings, and filter chips at the top of each page show one category at a time. Search matches categories too.

**Shortcuts:** every card has a **Create desktop shortcut** button (monitor icon). Programs get a `.lnk`, keeping their arguments and "Run as administrator". Store apps get a `.lnk` that opens them by app ID. Web apps get a `.url` that opens in your browser, with the site's icon. When the launcher installs a Bytehawk app, it adds the app to the **Start menu** (`Programs\Bytehawk Studio\`) unless the app's own installer already did. Uninstalling from the launcher removes that entry again.

**Website links:** My Software entries can have a website (for example the program's home page). A globe button on the card opens it.

**Export / import web apps:** on the Web Apps page, **Export** saves your web apps as Bytehawk JSON (keeps names, descriptions and categories) or as a browser bookmarks `.html` file. **Import** reads either one, including bookmark files exported from Chrome, Edge or Firefox; their folders become categories. Addresses already in your list are skipped.

**Microsoft Store apps** have no .exe to point at, so the launcher stores their app ID (for example `Microsoft.WindowsCalculator_8wekyb3d8bbwe!App`) and starts them through `shell:AppsFolder`. Launch arguments and "Run as administrator" aren't available for them. If a Store app is uninstalled, its card says so.

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

The launcher installs and updates apps the same way for every app, with no questions asked:

1. **Download** the release file that matches the app's `assetPattern` from the latest GitHub release, with progress, speed and Cancel.
2. **Check** it: the size must match GitHub's, and when the release publishes a SHA-256 digest (all recent releases do) the file's hash must match too. A damaged download is deleted and nothing is installed.
3. **Install** it into `%APPDATA%\BytehawkStudio\Launcher\apps\<app id>\`:
   * an **Inno Setup** or **NSIS** setup program (like `GuardrailEHS-x.y.z-Setup.exe`) is run **silently** into `apps\<app id>\app`, with no wizard, no admin prompt for per-user installers and no extra shortcuts;
   * a **.zip** is unpacked into `apps\<app id>\<version>` (a single top-level folder in the zip is skipped) and older version folders are removed.
4. **Find the program** to launch: the catalog's `exe` (for Guardrail EHS, `Guardrail.exe`), otherwise the only `.exe` in the install.
5. **Record** the installed version in `installed.json`. The setup file of the previous version is deleted.

**Updating** is installing the newer release over the old one, so settings and data that the app keeps in your profile are untouched. If the app is open, the launcher waits: the card says *Close <app> to update it*.

**Automatic updates** (Settings → *Install app updates automatically*, on by default): the launcher looks for new releases when it starts and **every 30 minutes** while it's open, and installs updates for apps it installed, as soon as they aren't running.

**Uninstall** (trash icon on the card) runs the app's own uninstaller and removes its folder. App data in your profile, such as Guardrail's records, is kept.

A release file the launcher can't install by itself (an `.msi`, or an `.exe` that isn't an Inno Setup / NSIS setup program) is started for you instead; when setup finishes, use **Choose launch file** on the card. Those apps are updated and uninstalled by their own setup program.

If the launch file is later deleted or moved, the card shows **Not found — reinstall** instead of crashing.

### GitHub rate limits and offline use

Anonymous GitHub API calls are limited to 60 per hour. To stay under that, the launcher:
* first asks **github.com** (not the API) which tag is the latest release (`/releases/latest` redirects to `/releases/tag/<tag>`). If it's the tag it already knows, no API call is made at all,
* makes one request per **repo**, not per card,
* caches release info in `cache\releases.json`, reuses it for 30 minutes on startup and for 60 seconds on a manual **Check for updates**,
* sends the cached `ETag`, so releases that haven't changed come back as `304 Not Modified`, which GitHub doesn't count against the limit,
* stops calling GitHub until the reset time once the limit is hit, and says so in the UI.

If GitHub can't be reached, the page shows *"Couldn't reach GitHub — showing last known versions"* and uses the cached data. **My Software** and **Web Apps** never need the network.

---

## Browser extensions

Chrome and Edge only install extensions from their own stores by themselves, so a Bytehawk extension is added to the browser once by hand. The launcher makes that quick:

1. **Download** on the extension's card downloads its release `.zip` (for AlbumGrab, `AlbumGrab-<version>.zip`) and unpacks it into `%APPDATA%\BytehawkStudio\Launcher\apps\<id>\extension`.
2. **Add to browser** opens a dialog with that folder and **Open Chrome** / **Open Edge** buttons. They open `chrome://extensions` or `edge://extensions` and copy the folder path, so you only have to turn on **Developer mode**, click **Load unpacked** and paste the folder.
3. **Updates** (automatic or with **Update**) unpack the new version into the **same folder**. The browser loads it the next time it starts, or right away with the reload arrow on its Extensions page.
4. **Uninstall** deletes the folder. Also click **Remove** on the extension in the browser.

In `catalog.json` an extension is a normal entry with `"kind": "extension"` and a `.zip` asset pattern; it appears on the Browser Extensions page instead of Bytehawk Apps. The macOS and Linux launchers list it as *Coming soon* for now.

---

## catalog.json format (for editing by hand)

`%APPDATA%\BytehawkStudio\Launcher\catalog.json`

```json
{
  "version": 1,
  "apps": [
    {
      "id": "guardrail-ehs",
      "name": "Guardrail EHS",
      "description": "What the app does, shown on the card.",
      "repo": "adamglassner/guardrailehs",
      "assetPattern": "GuardrailEHS-*-Setup.exe;*-win-x64.zip",
      "exe": "Guardrail.exe",
      "icon": "https://raw.githubusercontent.com/adamglassner/guardrailehs/main/src/Guardrail.Desktop/Assets/guardrail.png"
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
| `exe` | no | The program to launch after installing, e.g. `Guardrail.exe`. Found automatically when there's only one. |
| `icon` | no | `https://` URL of a square PNG shown on the card until the app is installed (then the program's own icon is used). |
| `comingSoon` | no | `true` shows the card with a *Coming soon* badge and no Download button. `repo` can be empty, but give it an `id`. When the app is released, click **Edit** on the card and paste the repo; the card becomes a normal app. |

Comments (`//`) and trailing commas are allowed. After editing, go to **Settings → Reload catalog** (or restart the launcher). **Settings → Open catalog.json** opens the file in Notepad. If the file can't be parsed, the launcher keeps a copy as `catalog.json.bad` and tells you.

The catalog ships with **Guardrail EHS** (free, open-source EHS / safety software, the first released Bytehawk Studio app; its server, Guardrail Server, is on the **Bytehawk Servers** page since 1.7.0), **Draftly** (word processor, installed from its portable zip), **ShoreDrop** (FTP, FTPS and SFTP client, installed from its portable zip), **Discshelf** (music player, installed from its portable zip), **Sumthing** (calculator, installed from its portable zip; since 1.3.2), **Taskflare** (to-do list with a Muse AI connector, installed from its portable zip; since 1.3.3), **Boardwalk** (kanban boards, one per project, installed from its portable zip; since 1.3.10), **WillowWallet** (budgeting, formerly CoinPilot, installed from its portable zip; since 1.3.4; its server, WillowWallet Server, is on the **Bytehawk Servers** page since 1.6.0), **Shutterbox** (photo organizer, installed from its portable zip; since 1.3.5), **Sparecopy** (family backup to a NAS, server or USB drive, early preview, installed from its portable zip; since 1.3.7), **Castaway** (media player for your own movies and shows, early preview, installed from its portable zip; since 1.3.8), **Larksong** (alarm clock, installed from its portable Windows zip; since 1.3.11), **Larksong Classic** (the alarm clock for Windows 95 to 11, installed from its zip; since 1.3.11), **Waylo** (video calls, voice calls and chat, early preview, installed from its portable zip) and **Coming soon** entries for Fleetwatch (PC management) and the Bytehawk office and creative suite (WriteHawk, GridHawk, PixelHawk and the other Hawk apps). The Notepad++ sample entries from 1.0.x are removed automatically when you update to 1.1.0, and the old "Padcraft (coming soon)" placeholder is replaced by Draftly in 1.1.1.

#### Apps with a server

An entry can have a `server` that goes with the app. Servers get their own **Bytehawk Servers** page in the sidebar, under Bytehawk Apps, with a card for each: **Install** downloads, checks and runs the server's Windows installer (it sets up a Windows service); once installed the card shows the version and offers **Open panel** (the server's web page), **Update** when there's a newer release, and **Uninstall** (keeping the server's data, or deleting it too). The **⋯** button shows the Docker command and the Linux and portable downloads. Server releases live in the app's repo under their own tag prefix, so they never replace the app's "latest" release:

```json
"server": {
  "name": "WillowWallet Server",
  "description": "Run your own server to sync WillowWallet between your PC and phone…",
  "repo": "adamglassner/coinpilot",
  "tagPrefix": "server-v",
  "windowsAsset": "WillowWalletServer-*-Setup.exe",
  "dockerImage": "ghcr.io/adamglassner/willowwallet-server",
  "port": 8484,
  "guideUrl": "https://github.com/adamglassner/coinpilot/blob/main/server/README.md",
  "windowsUninstallKey": "{6C4E2B1A-7F3D-4E85-9A2C-1D8B5F0E7A44}_is1",
  "windowsDataFolder": "%ProgramData%\\WillowWallet Server"
}
```

#### Releasing an app so the launcher can install it

Publish a GitHub Release (not a draft or pre-release) whose assets include an **Inno Setup or NSIS setup program** or a **Windows .zip**. The setup program must support a silent per-user install: for Inno Setup, `PrivilegesRequired=lowest` and no reboot. Guardrail EHS's [workflow](https://github.com/adamglassner/guardrailehs/blob/main/.github/workflows/build-release.yml) is a working example: every release is built, installed silently the same way the launcher does it, launched and uninstalled before it's published.

### The online catalog (managed in this GitHub repo)

By default the launcher loads the Bytehawk Apps list from **`catalog.json` at the root of this repo**:
`https://raw.githubusercontent.com/adamglassner/bytehawklauncher/main/catalog.json`.

To add a real app, or to turn a "Coming soon" entry into one, edit `catalog.json` on GitHub: fill in `"repo"` and `"assetPattern"`, and set `"comingSoon": false`. Every launcher picks up the change on its next start, or immediately with **Settings → Reload catalog**. No new launcher release is needed. GitHub's raw file cache can take up to about 5 minutes to show the change.

If GitHub can't be reached, the launcher uses the last downloaded copy, plus its built-in list. Apps added in the app with **Add Bytehawk app** are stored locally and shown alongside the online catalog.

### Remote catalog URL

**Settings → Remote catalog URL** points at the repo's catalog by default. You can change it to another URL that returns JSON in the same format, for example a raw GitHub URL such as `https://raw.githubusercontent.com/<owner>/<repo>/main/catalog.json`. On startup the launcher downloads it and shows those apps, plus any entries from your local `catalog.json` with a different `id`/`repo`. If the URL can't be reached, it uses the last downloaded copy, or just the local `catalog.json`. Entries from the remote catalog can't be edited or removed in the app.

---

## macOS and Linux launcher

`src/BytehawkStudioLauncher.Desktop` is the launcher for **macOS** (Apple Silicon and Intel, macOS 11+) and **Linux** (x64), built with [Avalonia](https://avaloniaui.net). It has the same look, theme options and sections as the Windows launcher: Bytehawk Apps, Bytehawk Servers, My Software, Web Apps, Downloads and Settings. The Windows launcher is unchanged and stays a separate WPF app. Both share the catalog, models, GitHub, download and web-app import/export code.

**Every Bytehawk app is listed on every system.** An app with no build for the system you're on shows **"Coming soon to macOS"** or **"Coming soon to Linux"** and has no Download button. For example, Draftly shows as Coming soon on macOS until it has a Mac version. Bytehawk Servers work the same way.

How apps are installed:

| System | Download | Installed to |
|---|---|---|
| macOS | `.zip` or `.dmg` with an `.app` | `~/Applications` (no password needed) |
| Linux | `.AppImage`, `.tar.gz`, `.zip` | the launcher's `apps/` folder, plus an applications-menu entry |
| Linux | `.deb` / `.rpm` | the system package manager, which asks for your password |

On Linux, servers install as a system service using their package's `install.sh`; the system asks for your password. On macOS and Linux they can also run with Docker ("Run with Docker"), which keeps the data in a Docker volume.

**Giving an app a macOS or Linux build:** add a `macos` and/or `linux` block to its entry in `catalog.json`. `{arch}` becomes `arm64` or `x64`:

```json
"macos": { "assetPattern": "ShoreDrop-*-macOS-{arch}.zip", "app": "ShoreDrop.app" },
"linux": { "tagPrefix": "linux-v", "assetPattern": "larksong_*_amd64.deb", "exe": "/opt/larksong/bin/Larksong", "package": "larksong" }
```

| Field | Meaning |
|---|---|
| `assetPattern` | Which release file to download (same wildcard rules as Windows), with `{arch}` for the processor |
| `tagPrefix` | Optional. Set it when this platform has its own releases (e.g. `macos-v`, `linux-v`); empty = the app's latest release |
| `app` | macOS: the `.app` inside the zip/dmg (found automatically if omitted) |
| `exe` | Linux: the program inside the archive, or the absolute path a `.deb`/`.rpm` installs |
| `package` | Linux `.deb`/`.rpm`: the package name, used to uninstall |

Servers get `linuxAsset` (e.g. `GuardrailServer-*-linux-{arch}.tar.gz`), `linuxInstallDir`, `linuxService` and `dockerName`.

**Releases:** change `<Version>` in `src/BytehawkStudioLauncher.Desktop/BytehawkStudioLauncher.Desktop.csproj` and push to `main`. The `macOS & Linux launcher` workflow then builds on a Mac and on Linux, runs the self-test, takes screenshots, and publishes release `desktop-v<version>` with:

- `BytehawkStudioLauncher-<v>-macOS-arm64.dmg` / `-x64.dmg` (plus `.zip` copies used for self-update)
- `BytehawkStudioLauncher-<v>-linux-x64.AppImage` and `.tar.gz`

These releases are never marked "latest", so the Windows launcher's updates aren't affected. Installed macOS/Linux launchers update themselves from them.

The Mac app is signed "ad hoc" because there's no Apple Developer ID yet. The first time someone opens it, macOS asks them to allow it in **System Settings → Privacy & Security → Open Anyway**. Apps the launcher downloads itself aren't affected.

Data lives in `~/Library/Application Support/BytehawkStudio/Launcher` on macOS and `~/.config/BytehawkStudio/Launcher` on Linux. Run `BytehawkStudioLauncher --print-data-dir` to see the exact folder.

Build locally: `packaging/desktop/build-mac.sh 1.0.0 osx-arm64` (on a Mac) or `packaging/desktop/build-linux.sh 1.0.0 linux-x64`.

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
