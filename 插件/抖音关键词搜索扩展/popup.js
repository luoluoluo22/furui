const form = document.getElementById("search-form");
const keywordInput = document.getElementById("keyword");
const status = document.getElementById("status");

initialize();

form.addEventListener("submit", async (event) => {
  event.preventDefault();

  const keyword = keywordInput.value.trim();
  if (!keyword) {
    setStatus("\u8bf7\u8f93\u5165\u5173\u952e\u8bcd");
    keywordInput.focus();
    return;
  }

  try {
    await chrome.storage.local.set({ lastKeyword: keyword });
    setStatus("\u6b63\u5728\u6253\u5f00\u641c\u7d22\u9875\u5e76\u5bfc\u51fa JSON...");

    const response = await chrome.runtime.sendMessage({
      type: "export-search-results",
      keyword,
      options: {
        active: true,
        closeTab: false,
        downloadVideos: true
      }
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Unknown error");
    }

    setStatus(`\u5df2\u5bfc\u51fa JSON\uff1a${response.count}\uff0c\u5df2\u4e0b\u8f7d\uff1a${response.downloadedCount || 0}`);
  } catch (error) {
    console.error(error);
    setStatus("\u5bfc\u51fa JSON \u5931\u8d25");
  }
});

async function initialize() {
  try {
    const { lastKeyword } = await chrome.storage.local.get("lastKeyword");
    if (lastKeyword) {
      keywordInput.value = lastKeyword;
    }
  } catch (error) {
    console.error(error);
  }

  keywordInput.focus();
  keywordInput.select();
}

function setStatus(message) {
  status.textContent = message;
}
