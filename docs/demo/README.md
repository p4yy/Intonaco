# README demo media

These files preserve a short real-app recording linked from the root [`README.md`](../../README.md). The featured official trailer is hosted on [YouTube](https://www.youtube.com/watch?v=ne2Bk_dG3JY).
They were derived from a 13.7 s, 1920×1200 screen recording supplied by the
project owner, then re-encoded for the web.

| File | Purpose | Size |
| --- | --- | --- |
| `intonaco-demo.mp4` | Full demo, 1280×800 H.264/AAC with `+faststart` so it streams in the browser. This is the canonical video. | ~2.4 MB |
| `intonaco-demo-preview.gif` | Silent 4.7 s, 800×450, 10 fps loop. GitHub renders GIFs inline, so the demo plays with **zero clicks** on the repository page. | ~3.4 MB |
| `intonaco-demo-poster.png` | 1280×720 still with a play affordance. Used as the `prefers-reduced-motion` fallback and reusable as a release/social thumbnail. | ~0.9 MB |

The two images are cropped to 16:9 to remove the taskbar and the recorder's
on-screen indicator; no app UI is hidden.

## How the demo is shown

GitHub's README sanitizer strips `<video>`, `<iframe>` and `<source>` elements.
It only renders a native inline player for a bare
`https://github.com/user-attachments/assets/<uuid>` URL. Such attachment URLs
can only be created by a signed-in browser session dragging a file into an
issue/PR comment — the upload endpoint returns 404 for Actions and App tokens —
so CI cannot generate one.

There is a second constraint: GitHub serves committed `.mp4` files with
`Content-Type: application/octet-stream` plus `nosniff`, so the file viewer and
`raw.githubusercontent.com` both **download** the video rather than play it,
regardless of size. GitHub Pages could serve it as `video/mp4`, but the Pages
settings API is not available to the integration token used here.

The README therefore uses three layers:

1. **Zero clicks** — the looping GIF plays inline on the repository page.
2. **One click** — the preview links to
   `https://cdn.jsdelivr.net/gh/p4yy/Intonaco@main/docs/demo/intonaco-demo.mp4`.
   [jsDelivr](https://www.jsdelivr.com/?docs=gh) mirrors this public repository
   and serves the MP4 with the correct `video/mp4` MIME type and byte-range
   support, so the browser opens its own video player with sound, seeking and
   full-screen controls. No upload, account or external storage is involved —
   the source of truth is the MP4 committed to `main`.
3. **Download** — a direct link to the canonical MP4 in the repository.

Visitors who enable reduced motion see the static poster instead of the loop.
If the CDN is ever unavailable, the MP4 can still be downloaded and played
locally. Note that jsDelivr caches `@main` for a short period, so a re-encoded
video may take a little while to appear; pinning a tag or commit SHA in the URL
avoids that.

## Optional upgrade to a native inline player

If you want an actual playable video element embedded in the README itself:

1. Drag `intonaco-demo.mp4` into the comment box of any issue or PR in this
   repository (or the README editor) while signed in on github.com.
2. GitHub uploads it and inserts a URL like
   `https://github.com/user-attachments/assets/<uuid>`.
3. Put that URL **on its own line** where the preview block is in
   `README.md`. GitHub renders it as a native player with controls and audio.
   Keep the preview GIF and the MP4 links as a fallback.

## Regenerating

With `ffmpeg` and Pillow available, from the repository root:

```bash
# Full demo (from the original recording)
ffmpeg -i source.mp4 -map 0:v:0 -map '0:a:0?' \
  -vf "scale='min(1280,iw)':-2" -c:v libx264 -crf 26 -pix_fmt yuv420p \
  -c:a aac -b:a 96k -movflags +faststart docs/demo/intonaco-demo.mp4

# Silent looping preview (16:9 crop removes the taskbar / recorder overlay)
ffmpeg -ss 8.8 -t 4.7 -i docs/demo/intonaco-demo.mp4 \
  -vf "crop=1280:720:0:50,fps=10,scale=800:450:flags=lanczos,split[a][b];[a]palettegen=max_colors=96[p];[b][p]paletteuse=dither=bayer:bayer_scale=3" \
  -loop 0 docs/demo/intonaco-demo-preview.gif

# Static poster frame
ffmpeg -ss 9.5 -i docs/demo/intonaco-demo.mp4 -frames:v 1 \
  -vf "crop=1280:720:0:50" -update 1 /tmp/frame.png
```

The play-button overlay on `intonaco-demo-poster.png` is drawn with Pillow over
`/tmp/frame.png` (a blurred cyan glow, white disc and dark triangle).
