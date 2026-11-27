# Changelog

## 1.2.1.0 (Jellyfin 10.11) and 1.2.1.12 (Jellyfin 12)

- Fixed: while the Roku app (or another app without remote control) plays a movie, other films in that series are left alone until it stops. These apps can queue a whole collection, and adding a subtitle mid-queue could make them pick the wrong audio track.

## 1.2.0.0 (Jellyfin 10.11) and 1.2.0.12 (Jellyfin 12)

- New: subtitles it adds and timing fixes now also appear in Jellyfin's **Dashboard > Activity** list.

## 1.1.0.0 (Jellyfin 10.11) and 1.1.0.12 (Jellyfin 12)

- New: the next film in a series gets subtitles ready while you watch the one before it (about 70% of the way through), and for movies in Continue Watching. It uses your collections, or the movie's TMDb series. Turn it off under **Prepare the next film in a series**.

## 1.0.0.0 (Jellyfin 10.11) and 1.0.0.12 (Jellyfin 12)

- First version: fetches missing subtitles ahead of time, and fixes the timing of text subtitles.
