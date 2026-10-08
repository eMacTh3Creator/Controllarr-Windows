# Torrent Storage and Subfolders

Applies to Windows v2.2.4 and later. A trailing `\` does **not** control torrent
subfolders. `E:\temp\tv-sonarr` and `E:\temp\tv-sonarr\` identify the same base
folder. Use absolute local or UNC paths accessible to the account running the app.

## Keep Category Folders Clean

In **Manage categories**, select a category, enable **Create a subfolder for
each new torrent**, and click **Save Category**. The same checkbox is available
in the browser Categories editor. New categories start with it enabled;
existing categories keep it disabled until you opt in.

For example, with Save Path `E:\temp\tv-sonarr` and Complete Path
`E:\complete\tv-sonarr`, an opted-in new torrent uses:

```text
Downloading: E:\temp\tv-sonarr\Episode.Name [0123456789ab]\Episode.Name.mkv
Completed:   E:\complete\tv-sonarr\Episode.Name [0123456789ab]\Episode.Name.mkv
```

Both single-file and multi-file torrents get one outer folder. Existing
multi-file internal directories remain intact, without a second torrent-name
wrapper. Windows-invalid name characters are replaced and the name is bounded;
a 12-character hash suffix distinguishes same-name downloads. Magnets without
a display name start in `Torrent [short-hash]` and keep that stable name after
metadata arrives. No payload relocation is needed just to learn the title.

**Settings > General > Create per-torrent subfolders for uncategorized downloads**
controls intake without a known category. Categories override that default,
including an explicitly unchecked category. An explicit add-dialog/API save
folder overrides the category's base path, not its subfolder policy. RSS and
watched-folder intake use the same routing. Migration/import of existing client
data keeps its original payload locations and remains paused pending recheck.

## Existing Downloads and Moves

Changing the checkbox applies to **new intake only**. Installation, category
edits and settings saves do not reorganize existing data. Loose files already
downloaded remain loose; do not move them in Explorer while the app is managing
them. A torrent's chosen outer-folder policy is saved with its engine state.

Completed-file moves and **Move storage** preserve that captured folder, or an
existing multi-file containing directory. Moving repeatedly to the same parent
does not append another copy of the folder. Existing flat single-file torrents
retain their flat layout during moves. To reorganize old data, stop the torrent
and plan a separate migration/recheck; there is no automatic retroactive wrapper.

Native category reassignment follows **Category change move**: Always moves,
Ask prompts, Never only changes the label. Headless/API callers move only for
Always. Enabling a category's checkbox does not retroactively opt an assigned
existing torrent into the new folder layout.

Before moving, Controllarr checks for target-file collisions and does not
overwrite another torrent's files. Insufficient permissions, missing disks,
collisions and other I/O failures are reported in Log and post-processing
status; use Retry after resolving the cause. A multi-file disk move is not a
transactional filesystem operation, so an I/O failure partway through may leave
some files at each location. Do not delete either location without checking.
Successful moves save their new engine paths for restart and retain manual pause.

## Archive Extraction and Sonarr/Radarr

Post-processing waits for metadata and 100% of selected files, not 99.9%.
Archive extraction considers only that torrent's selected payload files, even
when older torrents share a flat category root. It does not scan unrelated
torrents' archives in the category folder. Archives are extracted beside their
own archive files and existing extracted files are not overwritten.

The qBittorrent API reports both `save_path` and the actual `content_path`.
Sonarr/Radarr on another machine still need matching shared-folder permissions
and Remote Path Mappings when Windows and server paths differ. Subfolders are
not a substitute for remote path mapping. Sonarr/Radarr organize imported library
files independently; point the category at your download staging area rather
than directly at a final Plex library unless that workflow is intentional.
