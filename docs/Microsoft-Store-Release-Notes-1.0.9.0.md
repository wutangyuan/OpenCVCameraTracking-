# Microsoft Store 更新说明 / Release Notes — 1.0.9.0

## 中文（简体）

- 优化事件回放列表：新事件会立即显示录制状态，录像和快照完成后自动更新，无需重新打开窗口。
- 按需加载并缩小事件缩略图，关闭回放窗口时释放图像和列表资源，降低长时间浏览的内存占用。
- 限制录像编码队列；系统繁忙时优先保留事件快照，避免录像任务积压造成内存持续增长。
- 改进事件媒体清理：删除失败时保留记录以便重试，并安全清理长期未被引用的媒体文件。
- 改进事件快照选帧，降低暗光或瞬时黑帧导致缩略图过暗的情况；增强禁区告警横幅的可见性。

## English (United States)

- Improved the event library so new events appear immediately with a recording status, then update automatically when snapshots and clips are ready.
- Load smaller event thumbnails on demand and release image and list resources when the playback window closes to reduce memory use during long sessions.
- Bounded the video-encoding queue. When the system is busy, event snapshots are retained without queuing additional clips, preventing sustained memory growth.
- Improved event media cleanup: failed deletions remain eligible for retry, and old unreferenced media is cleaned up safely.
- Improved snapshot selection in low-light or transient dark-frame conditions and increased the visibility of restricted-zone alert banners.
