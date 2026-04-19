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
        maxVideos
      }
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Unknown error");
    }

    statusNode.textContent = "Export completed.";
    detailNode.textContent = `Saved ${response.count} items to ${response.filename}. Downloaded ${response.downloadedCount || 0} videos.`;

    if (closeTab) {
      await closeCurrentTab();
    }
  } catch (error) {
    statusNode.textContent = "Export failed.";
    detailNode.textContent = error instanceof Error ? error.message : String(error);
  }
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
