const SEARCH_BASE_URL = "https://www.douyin.com/search/";
const DOUYIN_ORIGIN = "https://www.douyin.com";
const ITEM_INFO_URL = "https://www.douyin.com/web/api/v2/aweme/iteminfo/";
const DETAIL_API_URL = "https://www.douyin.com/aweme/v1/web/aweme/detail/";
const DOWNLOAD_REQUEST_HEADERS = {
  Accept: "*/*",
  "Accept-Language": "zh-SG,zh-CN;q=0.9,zh;q=0.8",
  Range: "bytes=0-",
  Referer: `${DOUYIN_ORIGIN}/`,
  "User-Agent":
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
};
const SEARCH_SETTLE_DELAY_MS = 3000;
const SCROLL_ROUNDS = 6;
const SCROLL_DELAY_MS = 2500;
const EXTRACTION_RETRY_ROUNDS = 6;

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  return handleRuntimeMessage(message, sendResponse);
});

chrome.runtime.onMessageExternal.addListener((message, _sender, sendResponse) => {
  return handleRuntimeMessage(message, sendResponse);
});

async function openSearch(rawKeyword) {
  const keyword = String(rawKeyword || "").trim();
  if (!keyword) {
    throw new Error("Keyword is required.");
  }

  const searchUrl = `${SEARCH_BASE_URL}${encodeURIComponent(keyword)}`;
  await chrome.tabs.create({ url: searchUrl, active: true });
  return { searchUrl };
}

async function exportSearchResults(rawKeyword, options = {}) {
  const keyword = String(rawKeyword || "").trim();
  if (!keyword) {
    throw new Error("Keyword is required.");
  }

  const normalizedOptions = {
    active: options.active !== false,
    closeTab: options.closeTab === true,
    includeVideoDetails: options.includeVideoDetails !== false,
    downloadVideos: options.downloadVideos === true,
    maxVideos: normalizeMaxVideos(options.maxVideos)
  };

  const searchUrl = `${SEARCH_BASE_URL}${encodeURIComponent(keyword)}`;
  const tab = await chrome.tabs.create({
    url: searchUrl,
    active: normalizedOptions.active
  });

  try {
    await waitForTabComplete(tab.id);
    await delay(SEARCH_SETTLE_DELAY_MS);

    const extraction = await collectSearchResultsWithRetry(tab.id, keyword);
    if (!extraction?.ok) {
      throw new Error(extraction?.error || "Failed to extract search results.");
    }

    const enrichedResults = normalizedOptions.includeVideoDetails
      ? await enrichSearchResults(extraction.results, normalizedOptions)
      : extraction.results;

    const payload = {
      keyword,
      searchUrl,
      exportedAt: new Date().toISOString(),
      count: enrichedResults.length,
      results: enrichedResults
    };

    const filename = buildJsonFilename(keyword);
    const downloadUrl = `data:application/json;charset=utf-8,${encodeURIComponent(JSON.stringify(payload, null, 2))}`;

    await chrome.downloads.download({
      url: downloadUrl,
      filename,
      saveAs: false,
      conflictAction: "uniquify"
    });

    return {
      filename,
      searchUrl,
      count: payload.count,
      downloadedCount: payload.results.filter((item) => item.downloaded).length,
      results: payload.results
    };
  } finally {
    if (normalizedOptions.closeTab) {
      await chrome.tabs.remove(tab.id).catch(() => {});
    }
  }
}

async function downloadVideoFromMetadata(metadata) {
  const videoId = String(metadata?.videoId || "").trim();
  if (!videoId) {
    throw new Error("Video ID is required.");
  }

  const detailUrl = metadata?.detailUrl || `https://www.douyin.com/video/${videoId}`;
  const resolved = await resolveVideoForDownload({ videoId, detailUrl });
  if (!resolved.videoUrl) {
    throw new Error(resolved.resolveError || "Playable video URL was not found.");
  }

  const filename = buildVideoFilename({
    videoId,
    title: metadata?.title || resolved.title || "",
    author: metadata?.author || resolved.author || ""
  });

  const downloadResult = await performVideoDownload({
    videoUrl: resolved.videoUrl,
    filename,
    referer: detailUrl
  });
  if (!downloadResult.downloaded) {
    throw new Error(downloadResult.error || "Single video download did not complete.");
  }

  return {
    filename: downloadResult.filename || filename,
    videoUrl: resolved.videoUrl,
    provider: resolved.provider,
    downloadId: downloadResult.downloadId ?? null,
    downloadState: downloadResult.state
  };
}

async function enrichSearchResults(results, options) {
  const finalResults = [];
  let remainingDownloads = options.maxVideos;

  for (const result of results) {
    const enriched = await enrichSingleResult(result);

    if (options.downloadVideos && remainingDownloads > 0 && enriched.videoUrl) {
      const downloadResult = await downloadVideoFile(enriched);
      enriched.downloaded = downloadResult.downloaded;
      enriched.downloadFilename = downloadResult.filename;
      enriched.downloadId = downloadResult.downloadId;
      enriched.downloadState = downloadResult.state;
      enriched.downloadError = downloadResult.error || "";
      if (downloadResult.downloaded) {
        remainingDownloads -= 1;
      }
    } else {
      enriched.downloaded = false;
    }

    finalResults.push(enriched);
  }

  return finalResults;
}

async function enrichSingleResult(result) {
  const videoId = String(result?.videoId || "").trim();
  if (!videoId) {
    return {
      ...result,
      videoUrl: "",
      downloadFilename: "",
      downloadError: "Missing video ID."
    };
  }

  try {
    const detailUrl = result.detailUrl || `https://www.douyin.com/video/${videoId}`;
    const resolved = await resolveVideoForDownload({ videoId, detailUrl });

    return {
      ...result,
      title: resolved.title || result.title || "",
      author: resolved.author || result.author || "",
      cover: resolved.cover || result.cover || "",
      detailUrl,
      videoUrl: resolved.videoUrl || "",
      awemeType: resolved.awemeType ?? null,
      provider: resolved.provider,
      resolveError: resolved.resolveError || ""
    };
  } catch (error) {
    return {
      ...result,
      videoUrl: "",
      downloadFilename: "",
      downloadError: error instanceof Error ? error.message : String(error),
      provider: ""
    };
  }
}

async function resolveVideoForDownload({ videoId, detailUrl }) {
  const detailAttempt = await resolveViaDetail(videoId, detailUrl);
  if (detailAttempt.videoUrl) {
    return detailAttempt;
  }

  return resolveViaItemInfo(videoId, detailUrl, detailAttempt.resolveError || "");
}

async function resolveViaDetail(videoId, detailUrl) {
  try {
    const detailInfo = await fetchAwemeDetail(videoId, detailUrl);
    const aweme = detailInfo?.aweme_detail || {};
    const videoUrl = extractVideoUrl(detailInfo);
    const cookieMap = await getDouyinCookieMap();

    return {
      videoId,
      detailUrl,
      provider: "detail",
      title: aweme.desc || "",
      author: aweme.author?.nickname || "",
      cover: aweme.video?.cover?.url_list?.[0] || aweme.video?.origin_cover?.url_list?.[0] || "",
      videoUrl,
      awemeType: aweme.aweme_type ?? null,
      resolveError: videoUrl ? "" : `Detail API returned no playable URL. cookieCount=${Object.keys(cookieMap).length}`
    };
  } catch (error) {
    return {
      videoId,
      detailUrl,
      provider: "detail",
      title: "",
      author: "",
      cover: "",
      videoUrl: "",
      awemeType: null,
      resolveError: error instanceof Error ? error.message : String(error)
    };
  }
}

async function resolveViaItemInfo(videoId, detailUrl, previousError = "") {
  const itemInfo = await fetchItemInfo(videoId);
  const item = itemInfo?.item_list?.[0] || {};
  const itemVideoUrl = extractVideoUrl(itemInfo);
  const cookieMap = await getDouyinCookieMap();

  return {
    videoId,
    detailUrl,
    provider: "iteminfo",
    title: item.desc || "",
    author: item.author?.nickname || "",
    cover: item.video?.cover?.url_list?.[0] || item.video?.origin_cover?.url_list?.[0] || "",
    videoUrl: itemVideoUrl,
    awemeType: item.aweme_type ?? null,
    resolveError: itemVideoUrl
      ? previousError
      : joinErrors(previousError, `Item info returned no playable URL. cookieCount=${Object.keys(cookieMap).length}`)
  };
}

async function fetchAwemeDetail(videoId, detailUrl) {
  const url = new URL(DETAIL_API_URL);
  const cookieMap = await getDouyinCookieMap();
  const params = buildDetailParams(videoId, cookieMap);
  Object.entries(params).forEach(([key, value]) => {
    url.searchParams.set(key, String(value));
  });

  return fetchDouyinJson(url.toString(), detailUrl, "Detail request failed");
}

async function fetchItemInfo(videoId) {
  const url = new URL(ITEM_INFO_URL);
  url.searchParams.set("item_ids", videoId);

  return fetchDouyinJson(
    url.toString(),
    `https://www.douyin.com/video/${videoId}`,
    "Item info request failed"
  );
}

async function fetchDouyinJson(url, referer, errorPrefix) {
  const backgroundText = await fetchDouyinTextViaBackground(url, referer);
  if (backgroundText) {
    try {
      return JSON.parse(backgroundText);
    } catch {}
  }

  const pageText = await fetchDouyinTextViaPage(url, referer);
  if (!pageText) {
    throw new Error(`${errorPrefix}: empty response.`);
  }

  try {
    return JSON.parse(pageText);
  } catch {
    throw new Error(`${errorPrefix}: invalid JSON response.`);
  }
}

async function fetchDouyinTextViaBackground(url, referer) {
  const response = await fetch(url, {
    credentials: "include",
    headers: buildDouyinHeaders(referer)
  });
  return response.ok ? response.text() : "";
}

async function fetchDouyinTextViaPage(url, referer) {
  const tabId = await getDouyinPageTabId(referer);
  const [result] = await chrome.scripting.executeScript({
    target: { tabId },
    world: "MAIN",
    func: async (requestUrl, requestReferer) => {
      const response = await fetch(requestUrl, {
        credentials: "include",
        headers: {
          Accept: "application/json, text/plain, */*",
          Referer: requestReferer,
          "X-Requested-With": "XMLHttpRequest"
        }
      });

      return {
        ok: response.ok,
        status: response.status,
        text: await response.text()
      };
    },
    args: [url, referer]
  });

  if (!result?.result?.ok) {
    throw new Error(`Page fetch failed: ${result?.result?.status || "unknown"}`);
  }

  return result.result.text || "";
}

async function getDouyinPageTabId(preferredUrl) {
  const tabs = await chrome.tabs.query({ url: ["https://www.douyin.com/*"] });
  const existingTab = tabs.find((tab) => tab.url?.startsWith(preferredUrl)) || tabs.find((tab) => tab.id);
  if (existingTab?.id) {
    return existingTab.id;
  }

  const createdTab = await chrome.tabs.create({
    url: preferredUrl || `${DOUYIN_ORIGIN}/?recommend=1`,
    active: false
  });
  await waitForTabComplete(createdTab.id);
  await delay(2000);
  return createdTab.id;
}

async function getDouyinCookieMap() {
  const cookies = await chrome.cookies.getAll({ url: DOUYIN_ORIGIN });
  return Object.fromEntries(cookies.map((item) => [item.name, item.value]));
}

async function fetchJson(url, options, errorPrefix) {
  const response = await fetch(url, options);
  const text = await response.text();

  if (!response.ok) {
    throw new Error(`${errorPrefix}: ${response.status}`);
  }

  try {
    return text ? JSON.parse(text) : {};
  } catch {
    throw new Error(`${errorPrefix}: invalid JSON response.`);
  }
}

function buildDetailParams(videoId, cookieMap = {}) {
  const browserVersion = getBrowserVersion();

  return {
    aweme_id: videoId,
    device_platform: "webapp",
    aid: "6383",
    channel: "channel_pc_web",
    update_version_code: "170400",
    pc_client_type: "1",
    version_code: "290100",
    version_name: "29.1.0",
    cookie_enabled: "true",
    screen_width: String(globalThis.screen?.width || 1536),
    screen_height: String(globalThis.screen?.height || 864),
    browser_language: navigator.language || "zh-CN",
    browser_platform: navigator.platform || "Win32",
    browser_name: "Chrome",
    browser_version: browserVersion,
    browser_online: String(navigator.onLine !== false),
    engine_name: "Blink",
    engine_version: browserVersion,
    os_name: detectOsName(),
    os_version: detectOsVersion(),
    cpu_core_num: String(navigator.hardwareConcurrency || 8),
    device_memory: String(navigator.deviceMemory || 8),
    platform: "PC",
    downlink: "10",
    effective_type: "4g",
    round_trip_time: "200",
    msToken: cookieMap.msToken || ""
  };
}

function buildDouyinHeaders(referer) {
  return {
    Accept: "application/json, text/plain, */*",
    Referer: referer,
    "X-Requested-With": "XMLHttpRequest"
  };
}

function extractVideoUrl(payload) {
  const item = payload?.item_list?.[0] || payload?.aweme_detail || {};
  const candidates = [
    ...(item?.video?.play_addr?.url_list || []),
    ...(item?.video?.play_addr_h264?.url_list || []),
    ...(item?.video?.download_addr?.url_list || []),
    ...collectBitRateUrls(item?.video?.bit_rate)
  ];

  for (const candidate of candidates) {
    if (!candidate) {
      continue;
    }

    return candidate.replace("playwm", "play");
  }

  return "";
}

async function downloadVideoFile(metadata) {
  const filename = buildVideoFilename({
    videoId: metadata.videoId,
    title: metadata.title || "",
    author: metadata.author || ""
  });

  return performVideoDownload({
    videoUrl: metadata.videoUrl,
    filename,
    referer: metadata.detailUrl || `https://www.douyin.com/video/${metadata.videoId || ""}`
  });
}

function collectBitRateUrls(bitRates) {
  if (!Array.isArray(bitRates)) {
    return [];
  }

  return bitRates.flatMap((entry) => entry?.play_addr?.url_list || []);
}

function buildJsonFilename(keyword) {
  const timestamp = new Date().toISOString().replace(/[:.]/g, "-");
  const safeKeyword = sanitizePathPart(keyword) || "keyword";
  return `douyin-json/${safeKeyword}-${timestamp}.json`;
}

function buildVideoFilename({ videoId, title, author }) {
  const parts = [author, title, videoId].filter(Boolean).map((part) => sanitizePathPart(part));
  return `douyin-video/${parts.join("-") || videoId}.mp4`;
}

function sanitizePathPart(value) {
  return String(value || "")
    .normalize("NFKC")
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "_")
    .replace(/\s+/g, "_")
    .slice(0, 60);
}

function normalizeMaxVideos(value) {
  const number = Number(value);
  if (!Number.isFinite(number) || number <= 0) {
    return Number.MAX_SAFE_INTEGER;
  }

  return Math.floor(number);
}

function joinErrors(...messages) {
  return messages.filter(Boolean).join(" | ");
}

function buildDownloadFailureMessage(downloadResult, fallback) {
  const parts = [fallback, `state=${downloadResult.state || "unknown"}`];
  if (downloadResult.error) {
    parts.push(`error=${downloadResult.error}`);
  }
  return parts.join(" | ");
}

async function waitForDownloadCompletion(downloadId, timeoutMs = 30000) {
  const start = Date.now();

  while (Date.now() - start < timeoutMs) {
    const [item] = await chrome.downloads.search({ id: downloadId });
    if (!item) {
      return {
        state: "missing",
        filename: "",
        error: "download item not found"
      };
    }

    if (item.state === "complete") {
      return {
        state: "complete",
        filename: item.filename || "",
        error: ""
      };
    }

    if (item.state === "interrupted") {
      return {
        state: "interrupted",
        filename: item.filename || "",
        error: item.error || "unknown"
      };
    }

    await delay(500);
  }

  const [latest] = await chrome.downloads.search({ id: downloadId });
  return {
    state: latest?.state || "timeout",
    filename: latest?.filename || "",
    error: latest?.error || "timeout waiting for completion"
  };
}

async function performVideoDownload({ videoUrl, filename, referer }) {
  const blobResult = await downloadThroughFetchBlob(videoUrl, filename, referer);
  if (blobResult.downloaded) {
    return {
      downloaded: true,
      filename: blobResult.filename || filename,
      downloadId: blobResult.downloadId ?? null,
      state: blobResult.state,
      error: ""
    };
  }

  const directResult = await downloadThroughBrowser(videoUrl, filename);
  if (directResult.state === "complete") {
    return {
      downloaded: true,
      filename: directResult.filename || filename,
      downloadId: directResult.downloadId,
      state: directResult.state,
      error: ""
    };
  }

  const pageResult = await downloadThroughPage(videoUrl, filename, referer);
  if (pageResult.ok) {
    return {
      downloaded: true,
      filename,
      downloadId: null,
      state: "page_triggered",
      error: ""
    };
  }

  return {
    downloaded: false,
    filename: directResult.filename || filename,
    downloadId: directResult.downloadId ?? null,
    state: directResult.state || "failed",
    error: joinErrors(
      blobResult.error || "",
      buildDownloadFailureMessage(directResult, "Batch video download did not complete."),
      pageResult.error || ""
    )
  };
}

async function downloadThroughFetchBlob(videoUrl, filename, referer) {
  let objectUrl = "";

  try {
    const response = await fetch(videoUrl, {
      headers: {
        ...DOWNLOAD_REQUEST_HEADERS,
        Referer: referer || DOWNLOAD_REQUEST_HEADERS.Referer
      }
    });

    if (!response.ok) {
      return {
        downloaded: false,
        filename,
        downloadId: null,
        state: "fetch_failed",
        error: `fetch blob failed: status=${response.status}`
      };
    }

    const blob = await response.blob();
    objectUrl = URL.createObjectURL(blob);
    const downloadId = await chrome.downloads.download({
      url: objectUrl,
      filename,
      saveAs: false,
      conflictAction: "uniquify"
    });

    const downloadResult = await waitForDownloadCompletion(downloadId);
    return {
      downloaded: downloadResult.state === "complete",
      filename: downloadResult.filename || filename,
      downloadId,
      state: downloadResult.state,
      error:
        downloadResult.state === "complete"
          ? ""
          : buildDownloadFailureMessage(downloadResult, "Blob download did not complete.")
    };
  } catch (error) {
    return {
      downloaded: false,
      filename,
      downloadId: null,
      state: "fetch_failed",
      error: error instanceof Error ? error.message : String(error)
    };
  } finally {
    if (objectUrl) {
      setTimeout(() => URL.revokeObjectURL(objectUrl), 15000);
    }
  }
}

async function downloadThroughBrowser(videoUrl, filename) {
  const downloadId = await chrome.downloads.download({
    url: videoUrl,
    filename,
    saveAs: false,
    conflictAction: "uniquify"
  });

  const downloadResult = await waitForDownloadCompletion(downloadId);
  return {
    ...downloadResult,
    downloadId
  };
}

async function downloadThroughPage(videoUrl, filename, referer) {
  try {
    const tabId = await getDouyinPageTabId(referer);
    const [result] = await chrome.scripting.executeScript({
      target: { tabId },
      world: "MAIN",
      func: async (requestUrl, targetFilename) => {
        try {
          const response = await fetch(requestUrl, {
            credentials: "include"
          });

          if (!response.ok) {
            return {
              ok: false,
              error: `page fetch status=${response.status}`
            };
          }

          const blob = await response.blob();
          const objectUrl = URL.createObjectURL(blob);
          const anchor = document.createElement("a");
          anchor.href = objectUrl;
          anchor.download = (targetFilename || "video.mp4").split("/").pop();
          anchor.rel = "noopener";
          document.body.appendChild(anchor);
          anchor.click();
          anchor.remove();

          setTimeout(() => URL.revokeObjectURL(objectUrl), 15000);
          return {
            ok: true
          };
        } catch (error) {
          return {
            ok: false,
            error: error instanceof Error ? error.message : String(error)
          };
        }
      },
      args: [videoUrl, filename]
    });

    return result?.result || { ok: false, error: "page download returned no result" };
  } catch (error) {
    return {
      ok: false,
      error: error instanceof Error ? error.message : String(error)
    };
  }
}

function getBrowserVersion() {
  const userAgent = navigator.userAgent || "";
  const match = userAgent.match(/Chrome\/([\d.]+)/i) || userAgent.match(/Edg\/([\d.]+)/i);
  return match?.[1] || "125.0.0.0";
}

function detectOsName() {
  const platform = (navigator.platform || "").toLowerCase();
  if (platform.includes("win")) {
    return "Windows";
  }
  if (platform.includes("mac")) {
    return "mac";
  }
  if (platform.includes("linux")) {
    return "Linux";
  }
  return "Windows";
}

function detectOsVersion() {
  const userAgent = navigator.userAgent || "";
  const windowsMatch = userAgent.match(/Windows NT ([\d.]+)/i);
  if (windowsMatch?.[1]) {
    return windowsMatch[1];
  }

  const macMatch = userAgent.match(/Mac OS X ([\d_]+)/i);
  if (macMatch?.[1]) {
    return macMatch[1].replace(/_/g, ".");
  }

  return "10";
}

function sendError(sendResponse, error) {
  console.error(error);
  sendResponse({
    ok: false,
    error: error instanceof Error ? error.message : String(error)
  });
}

function handleRuntimeMessage(message, sendResponse) {
  if (message?.type === "open-search") {
    openSearch(message.keyword)
      .then((result) => sendResponse({ ok: true, ...result }))
      .catch((error) => sendError(sendResponse, error));

    return true;
  }

  if (message?.type === "download-video") {
    downloadVideoFromMetadata(message.metadata)
      .then((result) => sendResponse({ ok: true, ...result }))
      .catch((error) => sendError(sendResponse, error));

    return true;
  }

  if (message?.type === "export-search-results") {
    exportSearchResults(message.keyword, message.options)
      .then((result) => sendResponse({ ok: true, ...result }))
      .catch((error) => sendError(sendResponse, error));

    return true;
  }

  return false;
}

function waitForTabComplete(tabId) {
  return new Promise((resolve, reject) => {
    const timeoutId = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(handleUpdated);
      reject(new Error("Timed out waiting for Douyin page to load."));
    }, 30000);

    function handleUpdated(updatedTabId, changeInfo) {
      if (updatedTabId !== tabId || changeInfo.status !== "complete") {
        return;
      }

      clearTimeout(timeoutId);
      chrome.tabs.onUpdated.removeListener(handleUpdated);
      resolve();
    }

    chrome.tabs.onUpdated.addListener(handleUpdated);
  });
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function collectSearchResultsWithRetry(tabId, keyword) {
  let latestExtraction = {
    ok: true,
    keyword,
    results: []
  };

  for (let index = 0; index < EXTRACTION_RETRY_ROUNDS; index += 1) {
    const status = await chrome.tabs
      .sendMessage(tabId, { type: "has-search-results" })
      .catch(() => ({ ok: false, count: 0 }));

    if (status?.count > 0) {
      latestExtraction = await chrome.tabs.sendMessage(tabId, {
        type: "extract-search-results",
        keyword
      });

      if (latestExtraction?.results?.length > 0) {
        return latestExtraction;
      }
    }

    if (index < SCROLL_ROUNDS) {
      await chrome.tabs.sendMessage(tabId, { type: "scroll-search-page" }).catch(() => null);
      await delay(SCROLL_DELAY_MS);
    }
  }

  return chrome.tabs.sendMessage(tabId, {
    type: "extract-search-results",
    keyword
  });
}
