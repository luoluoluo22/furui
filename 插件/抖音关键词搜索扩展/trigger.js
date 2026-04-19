const statusNode = document.getElementById("status");
const detailNode = document.getElementById("detail");

run();

async function run() {
  const params = new URLSearchParams(window.location.search);
  const keyword = (params.get("keyword") || "").trim();
  const closeTab = params.get("closeTab") === "1";
  const active = params.get("active") !== "0";
  const downloadVideos = params.get("downloadVideos") === "1";
  const maxVideos = params.get("maxVideos") || "";

  if (!keyword) {
    statusNode.textContent = "Missing keyword query parameter.";
    detailNode.textContent = 'Example: ?keyword=%E7%B3%96%E5%B0%BF%E7%97%85';
    return;
  }

  statusNode.textContent = `Running export for: ${keyword}`;
  detailNode.textContent = "Please wait while the extension collects download links.";

  try {
    const response = await chrome.runtime.sendMessage({
      type: "export-search-results",
      keyword,
      options: {
        closeTab,
        active,
        downloadVideos,
        maxVideos,
        skipJsonDownload: true
      }
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Unknown error");
    }

    const savedFilename = downloadJsonText(
      response.jsonText || "{}",
      response.suggestedFilename || response.filename || "douyin-export.json"
    );

    statusNode.textContent = "Export completed.";
    detailNode.textContent = `Saved ${response.count} items to ${savedFilename}. Downloaded ${response.downloadedCount || 0} videos.`;

    if (closeTab) {
      await delay(500);
      await closeCurrentTab();
    }
  } catch (error) {
    statusNode.textContent = "Export failed.";
    detailNode.textContent = error instanceof Error ? error.message : String(error);
  }
}

function downloadJsonText(text, filename) {
  const blob = new Blob([text], { type: "application/json;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");
  const safeFilename = getBasename(filename) || "douyin-export.json";
  anchor.href = url;
  anchor.download = safeFilename.endsWith(".json") ? safeFilename : `${safeFilename}.json`;
  anchor.style.display = "none";
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
  return anchor.download;
}

function getBasename(filename) {
  return String(filename || "").split(/[\\/]/).filter(Boolean).pop() || "";
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function closeCurrentTab() {
  try {
    const tab = await chrome.tabs.getCurrent();
    if (tab?.id) {
      await chrome.tabs.remove(tab.id);
      return;
    }
  } catch {}

  window.close();
}
