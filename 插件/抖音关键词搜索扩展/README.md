# Douyin Keyword Search Extension

This browser extension can be triggered externally with a keyword, opens the
Douyin search page automatically, extracts visible results, enriches them with
video URLs from Douyin web APIs, exports them as JSON, and can download the
videos in batch.

Search URL format:

```text
https://www.douyin.com/search/<url-encoded-keyword>
```

Example:

```text
https://www.douyin.com/search/%E7%B3%96%E5%B0%BF%E7%97%85
```

## Behavior

1. Enter a keyword in the popup.
2. The extension opens the Douyin search page.
3. It waits for the page to settle and scrolls several times.
4. It extracts visible result cards into structured JSON.
5. It requests video details for each result through Douyin web APIs.
6. It downloads the JSON file to `douyin-json/` in the browser downloads
   folder.
7. It can also download video files to `douyin-video/`.

Each result currently includes:

- `videoId`
- `title`
- `author`
- `duration`
- `likes`
- `cover`
- `detailUrl`
- `videoUrl`
- `downloaded`
- `downloadFilename`

## External Trigger

You can trigger the export from another web page or extension with:

```js
chrome.runtime.sendMessage(extensionId, {
  type: "export-search-results",
  keyword: "diabetes",
  options: {
    downloadVideos: true,
    maxVideos: 10
  }
});
```

You can also open the trigger page directly:

```text
chrome-extension://<extension-id>/trigger.html?keyword=%E7%B3%96%E5%B0%BF%E7%97%85&downloadVideos=1&maxVideos=10
```

## Install

1. Open the extensions page in Chrome or Edge.
2. Enable developer mode.
3. Click "Load unpacked".
4. Select `D:\chajian`.

## Files

- `manifest.json`: extension manifest
- `popup.html`: popup UI
- `popup.css`: popup styles
- `popup.js`: popup interaction
- `background.js`: search open, export, detail API resolution, and download flow
- `content.js`: search page extraction and button injection
- `content.css`: injected button styles
- `trigger.html`: external URL trigger page
- `trigger.js`: auto-run trigger logic
