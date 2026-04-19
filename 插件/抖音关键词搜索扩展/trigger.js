const statusNode = document.getElementById("status");
const resultNode = document.getElementById("result");

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
    resultNode.textContent = 'Example: ?keyword=%E7%B3%96%E5%B0%BF%E7%97%85';
    return;
  }

  statusNode.textContent = `Running export for: ${keyword}`;

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

    statusNode.textContent = `Exported ${response.count} items to ${response.filename} (downloaded ${response.downloadedCount || 0})`;
    resultNode.textContent = JSON.stringify(response, null, 2);
  } catch (error) {
    statusNode.textContent = "Export failed.";
    resultNode.textContent = error instanceof Error ? error.stack || error.message : String(error);
  }
}
