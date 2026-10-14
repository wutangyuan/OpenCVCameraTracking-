# Feature Specification: Event Evidence and Review

**Feature Branch**: `001-event-evidence`
**Created**: 2026-10-14
**Status**: Approved for implementation
**Input**: User authorized implementation of the highest-priority market recommendation: event clips, snapshots, review/playback, and basic retention.

## User Scenarios & Testing

### User Story 1 - Preserve important camera events (Priority: P1)

When a tracked person or cat changes recognition state, or a supported target enters the configured restricted zone, the user wants a local event clip and snapshot so the event can be checked later.

**Why this priority**: Notifications without visual evidence leave the user unable to understand or verify an alert.

**Independent Test**: With a live camera or sample video, trigger a recognition-state change or restricted-zone entry and confirm an event appears with a snapshot and a playable clip containing footage from before and after the trigger.

**Acceptance Scenarios**:

1. **Given** a tracking session has been running for at least five seconds, **When** an eligible event occurs, **Then** the saved clip includes up to five seconds before the event and continues for ten seconds after it.
2. **Given** the event recorder cannot encode or write a clip, **When** an eligible event occurs, **Then** camera capture, tracking, and notifications continue and the event remains visible with any snapshot that could be saved.
3. **Given** another eligible event occurs while a clip is being captured, **When** the event is within the active post-event window, **Then** the clip is extended and the event summary includes both triggers.

### User Story 2 - Review and export saved events (Priority: P1)

The user wants to browse saved events, inspect their snapshots and details, play the associated clip, and export a clip to another location.

**Why this priority**: Event evidence is only useful if the user can find and review it without searching application data folders.

**Independent Test**: Open the event library after at least one event has been saved, select it, inspect its snapshot and metadata, play its clip, and export it.

**Acceptance Scenarios**:

1. **Given** saved events exist, **When** the user opens the event library, **Then** events are listed newest first with time, source, and event summary.
2. **Given** an event with a clip is selected, **When** the user starts playback, **Then** the clip plays inside the event library.
3. **Given** an event with a clip is selected, **When** the user exports it, **Then** the user can choose a destination and the exported file is playable.

### User Story 3 - Manage local event retention (Priority: P2)

The user wants a simple retention setting so event media does not accumulate indefinitely.

**Why this priority**: Video files can consume significant local storage, and users need control over how long event evidence is kept.

**Independent Test**: Select a retention period, restart or reopen the event library, and confirm the period is preserved and expired event media is removed while newer events remain.

**Acceptance Scenarios**:

1. **Given** the default settings, **When** the event library is opened, **Then** events older than seven days are removed.
2. **Given** the user selects a supported retention period, **When** the setting is saved, **Then** subsequent cleanup uses the selected period.
3. **Given** an event file is missing or unreadable, **When** the event library loads, **Then** the remaining events remain available and the broken entry can be removed.

### Edge Cases

- The camera starts and an event occurs before five seconds of pre-event footage have accumulated.
- The video source resolution changes during a recording.
- The source stops before the ten-second post-event period completes.
- Disk space is unavailable or a media file becomes unreadable.
- Several recognition and restricted-zone events occur close together.
- An application upgrade reads settings created before event-retention settings existed.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST create an event record for recognition-state transitions and restricted-zone entry alerts produced by the active tracking session.
- **FR-002**: The system MUST preserve a snapshot and an event clip locally for each event group.
- **FR-003**: Event clips MUST include up to five seconds of available pre-event footage and continue for ten seconds after the most recent trigger in the clip group; a clip group MUST be split into a new clip when it reaches two minutes.
- **FR-004**: The system MUST keep video sampling and file writing off the user interface thread and MUST NOT let recording failures stop camera processing or alert delivery.
- **FR-005**: The event library MUST list saved events newest first and show the event time, source name, and summary.
- **FR-006**: The user MUST be able to select an event, inspect its snapshot and details, and play its clip when available.
- **FR-007**: The user MUST be able to export a saved clip to a chosen local path.
- **FR-008**: The system MUST support retention periods of 3, 7, 14, and 30 days, defaulting to 7 days, and automatically remove expired event records and media.
- **FR-009**: The system MUST preserve compatibility with existing settings and recognition-event logs.
- **FR-010**: Event media and metadata MUST remain on the local computer unless the user explicitly exports a clip.

### Key Entities

- **Camera event**: A grouped alert occurrence with timestamp, source label, trigger summaries, and references to its snapshot and clip.
- **Event media**: A locally stored snapshot and optional playable clip associated with a camera event.
- **Retention setting**: The user-selected number of days to keep event records and media.

## Success Criteria

### Measurable Outcomes

- **SC-001**: A saved event clip includes the available pre-event interval up to five seconds and post-event footage through ten seconds after the last grouped trigger.
- **SC-002**: Users can open the event library and start playback of a selected saved clip in three interactions or fewer.
- **SC-003**: Event encoding or storage errors do not stop camera preview, tracking, or notification delivery.
- **SC-004**: Expired records and their associated media are removed according to the selected retention period.

## Assumptions

- Event recording applies to the active main tracking session; grid-only preview tiles are not recorded by this feature.
- Events generated less than ten seconds apart are grouped into one clip, with the post-event deadline extended by each trigger.
- A clip group is capped at two minutes to bound in-memory event buffering; a later trigger starts a new clip group.
- Event clips are sampled at a modest fixed frame rate to bound processing cost while retaining useful evidence.
- Event media is stored below the current user's LocalAppData application directory.
- An event snapshot is retained even when video encoding is unavailable.
