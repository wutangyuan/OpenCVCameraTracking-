# Implementation Plan: Event Evidence and Review

**Branch**: `001-event-evidence` | **Date**: 2026-10-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-event-evidence/spec.md`

## Summary

Add local event clips and snapshots for recognition transitions and restricted-zone entries, a WPF event library for reviewing/exporting footage, and a selectable 3/7/14/30-day retention policy. Capture uses a bounded asynchronous JPEG-frame pipeline so recording work cannot stall detection or the UI.

## Technical Context

**Language/Version**: C# / .NET 8
**Primary Dependencies**: Existing OpenCvSharp, WPF, System.Text.Json
**Storage**: `%LocalAppData%\OpenCVCameraTracking\Events`
**Testing**: Build verification; no test suite requested
**Target Platform**: Windows 10/11 x64
**Project Type**: WPF desktop application with a .NET core library
**Performance Goals**: Sample at 5 frames/second; bounded input queue and five-second pre-event ring buffer
**Constraints**: Never block capture/detection or the UI; tolerate incomplete clips and missing media; local-only by default
**Scale/Scope**: Main active tracking source; event grouping and local library

## Constitution Check

The repository constitution file contains only the untouched template placeholders, so there are no ratified project-specific gates. This plan follows existing architecture: reusable camera frame contract in Core, application persistence and UI in the WPF project, backward-compatible JSON settings.

## Design

### Capture and persistence

- Extend `FrameReadyEventArgs` with the capture timestamp already known by `CameraTrackingEngine`.
- Add an application-layer `EventRecordingService` that samples frame callbacks into a bounded queue, JPEG-encodes on a background worker, maintains a five-second encoded ring buffer, and groups triggers until ten seconds after the latest trigger.
- Write snapshots and video files beneath a date-partitioned LocalAppData `Events` directory. Prefer MP4; fall back to a playable AVI codec when MP4 writing is unavailable.
- Persist event metadata atomically in a JSON index. On startup, remove expired event rows and their files according to the configured retention period.
- Keep clip errors isolated from tracking and notification handlers; report them through application logging.

### UI

- Add an Event Library entry point in the main window.
- Provide a newest-first list, snapshot and event details, in-window playback, export, deletion of broken entries, and a retention selector.
- Keep user-facing text localized in Simplified Chinese and English and use existing theme resources.

## Project Structure

```text
src/OpenCVCameraTracking.Core/CameraTrackingEngine.cs
src/OpenCVCameraTracking/Configuration/ApplicationSettings.cs
src/OpenCVCameraTracking/Configuration/SettingsStore.cs
src/OpenCVCameraTracking/Configuration/CameraEventStore.cs
src/OpenCVCameraTracking/Configuration/EventRecordingService.cs
src/OpenCVCameraTracking/EventLibraryWindow.xaml
src/OpenCVCameraTracking/EventLibraryWindow.xaml.cs
src/OpenCVCameraTracking/MainWindow.xaml
src/OpenCVCameraTracking/MainWindow.xaml.cs
src/OpenCVCameraTracking/Languages/Strings.zh-CN.xaml
src/OpenCVCameraTracking/Languages/Strings.en-US.xaml
README.md
```

**Structure Decision**: Keep camera processing contracts in Core and event files, settings, and WPF presentation in the existing desktop application project.

## Complexity Tracking

No additional project or external service is required.
