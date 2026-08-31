# RDH Console Mode

Console shell for the **RETRO DECK MACHINE** — boots straight into Playnite
Fullscreen with no visible desktop.

This is a fork of [GamingConsoleMode](https://github.com/toonymak1993/GamingConsoleMode)
v2.6.8 by Mike Aniki (toonymak1993), used under the MIT license. The original
copyright notice and license are preserved in [LICENSE](LICENSE). All credit
for the underlying shell, overlay, gamepad and Steam infrastructure goes to the
upstream author.

Current version: **26.08.30** (versions are `YY.MM.DD` so release tags always
sort upward; the in-app updater reads this repo's latest GitHub release).

## What this fork changes

Every change is gated on `launcher = "playnite"` in
`%APPDATA%\gcmsettings\settings.toml` — with any other value the app behaves
exactly like upstream v2.6.8. All patch sites are marked `RDH patch` in source.

- **Direct launch** — boot goes straight to Playnite Fullscreen; the GCM UI is
  never shown first. GCM keeps running hidden as the shell and its UI is
  revealed *behind* Playnite once its window exists, so quitting Playnite lands
  on a working GCM screen instead of a black window.
- **Black-screen fallback** — if Playnite fails to appear within ~20 s, the GCM
  UI is revealed so the machine is never stranded on black.
- **Playnite launcher card** — the primary card in the launcher bar shows
  Playnite (foreground-if-running, launch-if-not) instead of Steam.
- **Fixed app cards** — the running-app ("recent") card is replaced by fixed
  cards defined in `%APPDATA%\gcmsettings\rdh_cards.json`.
- **Steam machinery hard-off** — the Steam plugin host loop, store sync,
  startup-video injection and every `StartSteam` path are suppressed; the eight
  `ApplySteamOnlyMode` call sites can no longer overwrite the launcher setting.
- **Boot cosmetics** — no Steam logo on the boot overlay; the launch flow keeps
  a plain black screen until Playnite's own intro takes over.
- Upstream's published tag is Steam-only at the boot path (`StartPlaynite()`
  exists but is never called there); this fork wires it in.

## Configuration (all files in `%APPDATA%\gcmsettings\`)

| File | Purpose |
|---|---|
| `settings.toml` | `launcher = "playnite"` enables everything above |
| `rdh_cards.json` | fixed launcher-bar cards: `[{"name","subtitle","exe","args","image"}]` — `image` optional (exe icon is extracted when omitted), bare filenames resolve inside `gcmsettings` |
| `launchercard.png` / `.jpg` | background artwork for the main launcher card |
| `launchercard_icon.png` | icon override for the main launcher card |

Playnite is auto-detected via the registry (`SOFTWARE\Playnite`, its
WOW6432Node twin, or the installer uninstall key). A **portable** Playnite
writes none of these — create `SOFTWARE\Playnite` with the install path so
detection succeeds.

## Building

```
dotnet publish gcmloader/gcmloader.csproj -c Release -p:Platform=x64 -r win-x64 --self-contained
```

.NET 8 SDK or later; Windows App SDK is restored from NuGet (self-contained).

## License

MIT — see [LICENSE](LICENSE). Ships on commercial RETRO DECK MACHINE units
with this license file included, as the MIT terms require.
