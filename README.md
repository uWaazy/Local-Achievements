# Local Achievements (Steam Tools Tracker)

A generic Playnite plugin designed to synchronize local Steam achievements and inject them seamlessly into the [SuccessStory](https://github.com/Lacro59/playnite-successstory-plugin) plugin database. This tool is built specifically for environments utilizing local `.lua` and `.bin` files (Steam Tools).

## Features

* **Offline Synchronization:** Reads local Steam binary files and schemas to recover accurate achievement unlock dates.
* **Automatic Tagging:** Scans your library and automatically applies a "Steam Tools" tag to supported games.
* **SuccessStory Integration:** Temporarily hides supported games from SuccessStory during gameplay to prevent data overwriting, silently injecting the correct data once the game is closed.
* **Playtime Sync:** Imports the hours played from Steam (`localconfig.vdf`) into Playnite for Steam Tools / Lua Tools games. If Steam has more playtime than Playnite, Playnite is updated to the Steam value; if Playnite has equal or more, nothing changes.
* **Instant Detection:** When a new Steam game is added to your library, it is tagged and its playtime is imported right away.
* **Settings:** Automatic sync on startup and automatic playtime import can be turned on or off in Add-ons > Extensions settings > Local Achievements.
* **Localization:** Native support for English (en-US), Portuguese (pt-BR), and Spanish (es-ES).

## Installation

1. Navigate to the Releases page of this repository.
2. Download the latest `.pext` extension file.
3. Drag and drop the `.pext` file into your open Playnite window, or install it manually via the Playnite menu: Add-ons > Install extension file...

## Usage

* Right-click any supported game and open the **Local Achievements** submenu: **Sync Achievements** or **Sync Playtime**.
* The plugin will automatically scan for new games and sync data in the background upon application startup, library updates, or when a new game is added.
