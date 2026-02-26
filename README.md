# Local Achievements (Steam Tools Tracker)

A generic Playnite plugin designed to synchronize local Steam achievements and inject them seamlessly into the SuccessStory plugin database. This tool is built specifically for environments utilizing local `.lua` and `.bin` files (Steam Tools).

## Features

* **Offline Synchronization:** Reads local Steam binary files and schemas to recover accurate achievement unlock dates.
* **Automatic Tagging:** Scans your library and automatically applies a "Steam Tools" tag to supported games.
* **SuccessStory Integration:** Temporarily hides supported games from SuccessStory during gameplay to prevent data overwriting, silently injecting the correct data once the game is closed.
* **Localization:** Native support for English (en-US), Portuguese (pt-BR), and Spanish (es-ES).

## Installation

1. Navigate to the Releases page of this repository.
2. Download the latest `.pext` extension file.
3. Drag and drop the `.pext` file into your open Playnite window, or install it manually via the Playnite menu: Add-ons > Install extension file...

## Usage

* Right-click any supported game in your library and select "Sync Local Achievements" from the context menu.
* The plugin will automatically scan for new games and sync data in the background upon application startup or library updates.