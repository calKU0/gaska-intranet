# Cameras module – deployment

The **Kamery** page shows Hikvision cameras live and plays their recordings. The browser never connects to a camera:

```
browser ──HTTPS / WSS──▶ intranet (IIS) ──ISAPI (HTTP Digest)──▶ camera     snapshots, recording search
                               │
                               └──WS (localhost)──▶ go2rtc ──RTSP──▶ camera  live video, playback
```

- **Live video and playback** go through [go2rtc](https://github.com/AlexxIT/go2rtc) on the application server. The
  intranet registers the streams through go2rtc's local API (with the camera credentials) and proxies the go2rtc
  WebSocket (fragmented MP4 for Media Source Extensions, MJPEG as a fallback) after checking the user's permission.
  When WebSockets do not get through (IIS without the *WebSocket Protocol* feature, a proxy or firewall stripping the
  upgrade), the player switches to the same video as plain HTTP (`/kamery/{id}/wideo` → go2rtc `/api/stream.mp4`);
  only when that fails too does it show pictures refreshed every second or two.
- **Recordings** stay where the cameras write them (network storage configured in each camera). The search
  (`/ISAPI/ContentMgmt/search`) and the playback (`rtsp://…/Streaming/tracks/101/?starttime=…`) both go to the camera.
  Hikvision starts a playback at the PLAY request's `Range: clock=…` header (without it: from the oldest recording),
  which go2rtc does not send – so for each playback the intranet opens a short-lived relay on 127.0.0.1 that adds it,
  plus `Scale: N` for 2×/4×/8× playback (the camera sends faster, the browser plays faster). A speed change while the
  recording plays is one more PLAY with the new Scale in the same RTSP session, sent by the relay (its answer is hidden
  from go2rtc) – the playback goes on without reconnecting. A playback range with
  nothing recorded is refused by the camera (RTSP 500), so the player starts at the next recording.
- **Downloads** (`/kamery/{id}/pobierz?od=…&do=…`, at most 1 hour) fetch the recording files of the range with
  `/ISAPI/ContentMgmt/download` (whole files – the camera does not cut them; older firmware takes it only as GET) and
  join them into one file. Each download is written to the audit log.
- **Snapshots** (`/ISAPI/Streaming/channels/101/picture`) are the tile poster and the fallback when video fails.

## Permissions

- **Kamery** (`cameras`) – the page itself.
- **Kamery – lokalizacje** – one entry per tab: `cameras.office`, `cameras.warehouse-a`, `cameras.warehouse-b`,
  `cameras.shop`, `cameras.production`, `cameras.packing`, `cameras.outdoor`, and `cameras.unassigned` – the
  *Nieprzypisane* tab with the cameras that have no `Location` yet (for IT).

A user sees a location only with **both** the page and the location permission (modes, AD users and groups work like
any other module). Administrators see everything. Access from outside the company network is controlled like for every
other tab – the "company network only" switch in the permissions panel, on the *Kamery* page (all locations) or on single
locations; it applies to everyone, administrators included. Every playback is written to the audit log.

## 1. Cameras

1. Create one service account on every camera (same name and password), role **Operator** or a user with *Remote: Live
   View* and *Remote: Playback / Search* – nothing else.
2. Enable the **sub stream** (channel `102`) in **H.264**, e.g. 640×360, 10–15 fps – it feeds the grid of tiles. The main
   stream may stay H.265 only if every browser used can play it (Chrome / Edge on Windows with hardware HEVC, Safari);
   otherwise set it to H.264 as well.
3. Time: NTP and the correct time zone. Recording times are read as UTC (current firmware); only if a camera writes its
   local time with a "Z" suffix set `Cameras:DeviceTimeZone` (e.g. `Europe/Warsaw`).
4. Network storage (NAS) configured under *Storage → Net HDD* – required for playback.

## 2. go2rtc on the application server

1. Download `go2rtc_win64.zip` from the go2rtc releases page and unpack it to e.g. `C:\go2rtc`.
2. `C:\go2rtc\go2rtc.yaml` – local API only, every other listener off, **no streams and no passwords** (the intranet
   adds them at run time):

   ```yaml
   api:
     listen: "127.0.0.1:1984"
   rtsp:
     listen: ""
   webrtc:
     listen: ""
   srtp:
     listen: ""
   log:
     level: info
   ```

3. Run it as a service, e.g. with [WinSW](https://github.com/winsw/winsw) (`go2rtc-service.xml` next to the exe, start
   mode *Automatic*) or a Task Scheduler task *At startup* running `C:\go2rtc\go2rtc.exe -config C:\go2rtc\go2rtc.yaml`
   as `SYSTEM`.
4. Check on the server: <http://127.0.0.1:1984/> opens the go2rtc page. Port 1984 must **not** be reachable from other
   machines (it listens on 127.0.0.1; keep the Windows firewall rule closed anyway).
5. Optional: put `ffmpeg.exe` next to `go2rtc.exe` and set `Cameras:FfmpegPath` to it. Downloads are then ordinary MP4
   files (remuxed, no re-encoding); without it they are the cameras' own files (MPEG program stream with a Hikvision
   header – VLC plays them). go2rtc uses it for the MJPEG fallback too.

## 3. Intranet

- IIS: the *WebSocket Protocol* feature (already required by Blazor).
- `appsettings.Local.json` (never in the repository):

  ```json
  "Cameras": {
    "Username": "<service account>",
    "Password": "<password>",
    "Cameras": [
      { "Host": "<camera address>", "Location": "Office" },
      { "Id": "magazyn-a-rampa", "Name": "Rampa", "Host": "<camera address>", "Location": "WarehouseA" },
      { "Host": "<camera address>" }
    ]
  }
  ```

  | Field | Meaning |
  |---|---|
  | `Id` | Optional permanent id used in URLs and the audit log – letters, digits, `-`, `_`. Empty: made from the address (`10-0-0-43`). |
  | `Name` | Optional. Empty: the camera's own name from its web panel (OSD "Camera Name", else "Device Name"), read at startup and every 6 hours. Factory names ("Camera 01", "IP CAMERA") are skipped – the address is shown instead. |
  | `Location` | `Office`, `WarehouseA`, `WarehouseB`, `Shop`, `Production`, `Packing`, `Outdoor`; omitted – the *Nieprzypisane* tab. |
  | `AlsoIn` | Further tabs showing the same camera, e.g. a packing station also in its warehouse: `"AlsoIn": [ "WarehouseA" ]`. A user may watch it from any of its tabs. |
  | `Host`, `HttpPort` (80), `RtspPort` (554) | Camera address. |
  | `Channel` (1) | Channel – 1 for a camera, the channel number on a recorder. |
  | `HasSubStream` (true) | `false` when the camera has no sub stream – the grid then uses the main stream. |

  Other settings: `Go2RtcUrl` (`http://127.0.0.1:1984`), `DeviceTimeZone`
  (`UTC`), `TimeoutSeconds` (8).

  Polish letters: cameras keep ć, ś, ź as an apostrophe plus the letter ("Wej'scie") and turn ą, ę, ł, ż into plain
  letters. Names read from the cameras are turned back into Polish – the apostrophe form directly, the lost letters by
  whole words from `PolishWords` (built-in list: Piętro, Tył, Dół, Przyjęcia, Wejście, Zewnętrzne, Księgowość …;
  entries in the configuration are added, e.g. `"PolishWords": { "Kotlownia": "Kotłownia" }`). OSD text on the picture
  is plain ASCII. Invalid camera entries stop the application at startup with a message.
- Grant the permissions in **Administracja → Uprawnienia** (module *Kamery* and the locations) and switch on
  "company network only" on the *Kamery* module if the cameras must not be watched from outside the company.

## Demo mode

With `Demo:Enabled` (launch profile `https-demo`) sixteen sample cameras show generated pictures with a clock, and the
recordings tab shows sample recording blocks – no cameras or go2rtc needed.

## Troubleshooting

| Symptom | Check |
|---|---|
| Tile shows *Obraz co kilka sekund* | Both the video socket and HTTP video failed: go2rtc not running (log: "Live stream of camera … unavailable", "go2rtc MP4 stream unavailable"), wrong RTSP port, or a codec the browser cannot play (H.265). |
| Video works but slowly starts / log "Camera video … arrived without a WebSocket upgrade" | WebSockets do not reach the application – the player uses HTTP video. Install the IIS *WebSocket Protocol* feature (Server Manager → Web Server → Application Development) or let the proxy pass `Upgrade`. The same cause makes the pages slow ("Blazor connection without WebSockets"). |
| *Brak obrazu* | Camera does not answer ISAPI: address, account, or the server cannot reach the camera network. |
| Recording search error | Account without playback rights, or no network storage on the camera. |
| Playback starts at the wrong hour | Camera time zone / NTP vs. `Cameras:DeviceTimeZone`. |
