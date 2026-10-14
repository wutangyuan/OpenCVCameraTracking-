# Tasks: Event Evidence and Review

**Input**: Design documents from `specs/001-event-evidence/`
**Prerequisites**: `plan.md`, `spec.md`
**Tests**: No test tasks included because the user did not request tests.

## Phase 1: Core event capture

- [X] T001 [US1] Add capture timestamp to `FrameReadyEventArgs` and populate it in `src/OpenCVCameraTracking.Core/CameraTrackingEngine.cs`.
- [X] T002 [US1] Add event metadata entity and atomic local JSON index with retention cleanup in `src/OpenCVCameraTracking/Configuration/CameraEventStore.cs`.
- [X] T003 [US1] Implement bounded sampled-frame queue, pre-event ring, trigger grouping, two-minute clip cap, snapshot writing, and video encoding/fallback in `src/OpenCVCameraTracking/Configuration/EventRecordingService.cs`.
- [X] T004 [US1] Add a backward-compatible event retention setting and normalization in `src/OpenCVCameraTracking/Configuration/ApplicationSettings.cs` and `src/OpenCVCameraTracking/Configuration/SettingsStore.cs`.
- [X] T005 [US1] Feed every frame to the recorder and trigger it from recognition-state and restricted-zone events in `src/OpenCVCameraTracking/MainWindow.xaml.cs`.
- [X] T006 [US1] Drain and finalize the recorder when the tracking engine stops or the application closes in `src/OpenCVCameraTracking/MainWindow.xaml.cs`.

## Phase 2: Event library

- [X] T007 [US2] Build the event list, snapshot/details panel, playback controls, export, and broken-entry deletion in `src/OpenCVCameraTracking/EventLibraryWindow.xaml` and `src/OpenCVCameraTracking/EventLibraryWindow.xaml.cs`.
- [X] T008 [US2] Add an event-library entry point to `src/OpenCVCameraTracking/MainWindow.xaml` and `src/OpenCVCameraTracking/MainWindow.xaml.cs`.
- [X] T009 [US3] Add retention selection and persist updates through `src/OpenCVCameraTracking/EventLibraryWindow.xaml.cs` and `src/OpenCVCameraTracking/MainWindow.xaml.cs`.

## Phase 3: Localization and documentation

- [X] T010 [P] [US2] Add event-library and recording strings to `src/OpenCVCameraTracking/Languages/Strings.zh-CN.xaml` and `src/OpenCVCameraTracking/Languages/Strings.en-US.xaml`.
- [X] T011 [P] [US1] Document event capture, local storage, playback, retention, and limitations in `README.md`.

## Dependencies & Execution Order

- T001 precedes T003 and T005.
- T002 and T004 precede recorder startup and retention UI (T003, T005, T007, T009).
- T003 precedes T005 and T006.
- T005/T006 and T002 precede the event library (T007/T008/T009).
- Localization and documentation follow feature behavior stabilization.
