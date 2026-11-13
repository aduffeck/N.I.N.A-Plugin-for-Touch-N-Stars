# Touch 'N' Stars

## Unreleased
- End of night: `POST /api/secure-observatory` runs the shutdown on the server, so it keeps going when the phone drops off Wi-Fi: stop the sequence, abort the exposure, stop guiding, park, close the dome (only over a parked mount, and only counted when the dome reports closed), close the flat panel cover and switch its light off, warm the camera, disconnect every device (on pins also the guide camera; skipped when an earlier step failed). Body `{ park, closeDome, closeCover, warmCamera, disconnect, warmMinutes }`, all optional. `GET /api/secure-observatory` reports the steps, `POST /api/secure-observatory/cancel` stops the remaining ones; a second start while one runs returns the running one
- `GET /api/preflight/image-disk` reports the image folder, whether it exists and its free space, for the app's check before a sequence starts
- Internal guider API: `GET /api/internal-guider/logs` lists the guide logs of the internal guider (one per night, newest first, with size, last write, whether it is being written and the host's time zone) and `GET /api/internal-guider/logs/{name}` sends one as text, gzip-compressed when the client accepts it; both also while the guider is not connected; see `INTERNAL_GUIDER_API.md`
- Internal guider API: `POST /api/internal-guider/coach/start?slew=stay|east|west` slews to a calibration position before the Guiding Coach starts, as before a calibration; Stop or `coach/cancel` cancel the slew; a running session, calibration or dark library build is refused before the mount moves; see `INTERNAL_GUIDER_API.md`
- Internal guider API: `POST /api/internal-guider/coach/start` takes `exposureTrial` (default true) to switch the coach's shorter-exposure trials (E, E2) on or off; dismissing the one-time `hint.longExposure` hides it for good; see `INTERNAL_GUIDER_API.md`
- Atlas: new survey `nsns-cont` (NSNS DR0.2 RGB continuum, rgb8), orders 3–5
- Raw image data: `GET /api/raw-image/prepared`, `/api/raw-image/history` and `/api/raw-image/livestack` serve 16-bit preview planes plus an exact full-resolution histogram (65536 bins per channel) and statistics for the app's histogram and client-side stretch; OSC frames are superpixel-debayered, responses are gzip-compressed
- Multi-mode flats: `GET /api/flats/status` also reports `BitDepth`, the bit depth of the image `CurrentADU` was measured on (null while there is no ADU), so the app can scale the ADU correctly for RAW frames, e.g. a 14-bit NEF while the camera profile says 16 bit
- Internal guider API: `POST /api/internal-guider/start-guiding?calibrate=true&slew=stay|east|west` slews to a calibration position (5° from the meridian on the celestial equator, like PHD2's Calibration Assistant) before calibrating; `stay` keeps the mount's side of pier; `/status` reports `calibrationSlew` while it runs; see `INTERNAL_GUIDER_API.md`
- Internal guider API: `POST /api/internal-guider/select-star` makes the star nearest a position in the guide frame the guide star while the guider is looping, for choosing the guide star by tapping it; see `INTERNAL_GUIDER_API.md`
- Persistent Celestia Atlas data (landscapes, DSS survey) now lives in `NINA\TnsCache` next to the other plugin data; an existing `NINA\Touch-N-Stars` tree is moved there once
- Atlas DSS survey download: new `/api/atlas/survey/*` endpoints (status, download, cancel, delete) fetch the DSS colour HiPS tile by tile onto the host as 512 px JPEG and serve it at `/celestia-atlas-data/surveys/dss` from the persistent data directory; resumable, one job at a time
- Atlas DSS survey delete now accepts an optional `keepOrder` to downgrade to a lower order instead of always wiping the whole survey
- Keep advertising the `_touchnstars._tcp` service and its active port on
  Linux while sharing hostname ownership with the system Avahi daemon.
- Package and serve the `celestia-atlas-data` application directory.
- Keep generated Celestia Atlas landscapes in persistent N.I.N.A. user data and
  migrate existing app-local landscapes without overwriting user content.
- Atlas NSNS narrowband survey (CC BY-NC-SA 4.0): the survey endpoints take an optional `survey` (`dss` default, `nsns` colour composite ohs8, `nsns-ha`, `nsns-oiii`, `nsns-sii` single lines); NSNS tiles inside its Moc.fits coverage are fetched from simg.de, stored as JPEG q85 and served at `/celestia-atlas-data/surveys/nsns`; only one survey download runs at a time
- `/api/sequence/move` moves items, triggers and conditions across containers: the target sibling may sit in another container, and a container `targetId` without `insertAfter` moves into it; the object keeps its id, children and settings, a container cannot be moved into itself and the start/target/end areas stay in place
- Manual mount control works with mounts that are neither INDI nor pins' native OnStepX driver, e.g. ASCOM Alpaca: `/ws/mount-control` moves them through NINA's telescope mediator and reports parked or fixed axes as errors, and `/api/indi/mount/slew-rates` describes their AxisRates (one range continuous, fixed or gapped rates as discrete steps) for `POST /api/indi/mount/slew-rate`; previously the pad answered "No mount is currently connected"

## 1.4.0.1
- fix(filesystem-preview): read the Bayer pattern from the file header, so OSC FITS/XISF frames offer the debayer option and render with their actual pattern

## 1.4.0.0
- feat(filesystem): add server-side preview/imageinfo endpoints

## 1.3.2.0
- Update Touch N Stars WebApp

## 1.3.1.0
- Update Touch N Stars WebApp

## 1.3.0.0
- Generated Celestia Atlas landscapes are now stored in the persistent N.I.N.A. user data; existing app-local landscapes are migrated without overwriting user content
- The `celestia-atlas-data` directory is now packaged and served

## 1.0.0.1

- Initial release
