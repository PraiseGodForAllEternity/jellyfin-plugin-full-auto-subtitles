<p align="center"><img src="images/logo.png" alt="Full Auto Subtitles" width="640"></p>

# Full Auto Subtitles for Jellyfin

Fetches missing subtitles for what each person is about to watch, and fixes the timing of text subtitles you already have.

Requires Jellyfin 10.11 or 12 and a subtitle provider plugin, such as OpenSubtitles.

## Install

1. In Jellyfin, open **Dashboard > Plugins > Repositories** and add:

   ```
   https://raw.githubusercontent.com/PraiseGodForAllEternity/jellyfin-plugin-full-auto-subtitles/main/manifest.json
   ```

2. Install **Full Auto Subtitles** from the **Catalog** and restart Jellyfin.
3. Install and sign in to a subtitle provider plugin, such as OpenSubtitles.

One link for everyone: Jellyfin installs the build for its own version (10.11 or 12) and keeps it updated.

## What it does

- Prepares Continue Watching, Next Up, the next episodes and favorite movies, in each person's subtitle language.
- Prefers exact matches for the file; skips forced-only and machine-translated results.
- Shares a daily allowance fairly between users.
- **Fix timing:** compares a subtitle with one that's in sync, then saves a corrected copy. Originals aren't changed, and it can be undone.

Adding a subtitle renumbers a video's tracks in Jellyfin. So nothing is added while anyone is watching that movie or show, and saved audio and subtitle choices are moved along.

## Limits

- A title nobody was expected to watch may have no subtitles the first time.
- If someone has a movie's page open while a subtitle is added, their app may use an old track number until they reopen it.
- Fix timing handles a constant delay and a steady drift, not a different cut or picture-based subtitles.

## Testing status

**Tested** on Jellyfin 10.11.11 and 12.4 (Windows test servers, made-up media, fake subtitle provider): fetching, quota, matching choices, saved track choices, Fix timing (separate files and inside the video), undo, permissions, settings page.

**Not tested:** the real OpenSubtitles plugin, real movies' subtitles, Roku or other apps, Linux, 10.11.0 to 10.11.10, large libraries, several people watching at once, two versions of one movie.
