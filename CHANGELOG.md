# Changelog

## [0.2.2] - 2026-09-02

### Added

- `PicoFrame.trackers` (`MotionFrame`): parses the `Motion` field sent by the
  PICO bridge app (motion trackers bound to left/right). Exposes per-side
  `TrackerState` (SN, pose, PICO validity flag). Recording/replay carries the
  field verbatim. Old apps without `Motion` yield an inactive frame.

## [0.2.1] - 2026-05-22

- Added head-gaze UI following for the headset panel.
- Added receiver tracking recording with `--record`.
- Fixed tracking pose semantics and packet resync handling.
- Fixed the XR origin floor offset.
- Documented receiver architecture support.

## [0.2.0] - 2026-05-11

- Added pushed PC video frame streaming.
- Added PC video policy controls and camera extras.
- Updated tracking UI label casing.
- Documented ARM RealSense dependency behavior.

## [0.1.0] - 2026-05-03

- Published the initial PICO 4 / PICO 4 Ultra APK.
- Added headset, controller, hand, body, and Motion Tracker streaming to PC.
- Added the PC receiver Python SDK and CLI.
- Added optional PC camera video return to the headset.
