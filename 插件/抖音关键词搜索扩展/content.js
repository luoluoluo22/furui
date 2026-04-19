const CARD_SELECTOR = ".search-result-card";
const VIDEO_ID_SELECTOR = "[data-video-id]";
const DOWNLOAD_BUTTON_CLASS = "dy-helper-download-button";
const CARD_MARKER = "data-dy-helper-bound";
const TITLE_SELECTORS = [".BjLsdJMi", "[data-e2e='search-card-desc']"];
const AUTHOR_SELECTORS = [".WldPmwm5", "[data-e2e='search-card-author-name']"];

initialize();

function initialize() {
  scanCards();
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message?.type === "has-search-results") {
      const count = Math.max(
        document.querySelectorAll(CARD_SELECTOR).length,
        document.querySelectorAll(VIDEO_ID_SELECTOR).length
      );
      sendResponse({
        ok: true,
        count
      });
      return false;
    }

    if (message?.type === "extract-search-results") {
      try {
        sendResponse({
          ok: true,
          keyword: message.keyword || "",
          results: extractSearchResults()
        });
      } catch (error) {
        sendResponse({
          ok: false,
          error: error instanceof Error ? error.message : String(error)
        });
      }

      return true;
    }

    if (message?.type === "scroll-search-page") {
      window.scrollTo({
        top: document.documentElement.scrollHeight,
        behavior: "smooth"
      });
      sendResponse({ ok: true });
      return false;
    }
  });

  const observer = new MutationObserver(() => {
    scanCards();
  });

  observer.observe(document.documentElement, {
    childList: true,
    subtree: true
  });
}

function scanCards() {
  const bound = new Set();

  for (const card of document.querySelectorAll(CARD_SELECTOR)) {
    bindCard(card);
    bound.add(card);
  }

  for (const node of document.querySelectorAll(VIDEO_ID_SELECTOR)) {
    const host = findCardHost(node);
    if (host && !bound.has(host)) {
      bindCard(host);
      bound.add(host);
    }
  }
}

function bindCard(card) {
  if (!(card instanceof HTMLElement) || card.hasAttribute(CARD_MARKER)) {
    return;
  }

  const videoId = extractVideoId(card);
  if (!videoId) {
    return;
  }

  card.setAttribute(CARD_MARKER, "1");

  const button = document.createElement("button");
  button.type = "button";
  button.className = DOWNLOAD_BUTTON_CLASS;
  button.textContent = "\u4e0b\u8f7d";

  button.addEventListener("click", async (event) => {
    event.preventDefault();
    event.stopPropagation();

    const metadata = {
      videoId,
      title: extractText(card, TITLE_SELECTORS),
      author: extractText(card, AUTHOR_SELECTORS)
    };

    await handleDownload(button, metadata);
  });

  const anchorHost = card.querySelector(".PtY9QFFE, .TLxYU_vw") || card;
  if (anchorHost instanceof HTMLElement && getComputedStyle(anchorHost).position === "static") {
    anchorHost.style.position = "relative";
  }

  anchorHost.appendChild(button);
}

async function handleDownload(button, metadata) {
  const previousText = button.textContent;
  button.disabled = true;
  button.textContent = "\u5904\u7406\u4e2d";

  try {
    const response = await chrome.runtime.sendMessage({
      type: "download-video",
      metadata
    });

    if (!response?.ok) {
      throw new Error(response?.error || "Download failed");
    }

    button.textContent = "\u5df2\u4e0b\u8f7d";
    setTimeout(() => {
      button.disabled = false;
      button.textContent = previousText;
    }, 1500);
  } catch (error) {
    console.error(error);
    button.textContent = "\u5931\u8d25";
    setTimeout(() => {
      button.disabled = false;
      button.textContent = previousText;
    }, 1500);
  }
}

function extractVideoId(card) {
  const dataIdNode = card.closest("[data-video-id]") || card.querySelector("[data-video-id]");
  if (dataIdNode instanceof HTMLElement && dataIdNode.dataset.videoId) {
    return dataIdNode.dataset.videoId;
  }

  const anchors = card.querySelectorAll("a[href*='/video/']");
  for (const anchor of anchors) {
    const match = anchor.href.match(/\/video\/(\d+)/);
    if (match) {
      return match[1];
    }
  }

  return null;
}

function extractText(root, selectors) {
  for (const selector of selectors) {
    const element = root.querySelector(selector);
    const text = element?.textContent?.trim();
    if (text) {
      return text;
    }
  }

  return "";
}

function extractSearchResults() {
  const results = [];
  const seen = new Set();

  for (const card of getResultHosts()) {
    if (!(card instanceof HTMLElement)) {
      continue;
    }

    const videoId = extractVideoId(card);
    if (!videoId || seen.has(videoId)) {
      continue;
    }

    seen.add(videoId);

    const title = extractText(card, TITLE_SELECTORS);
    const author = extractText(card, AUTHOR_SELECTORS);
    const duration = extractText(card, [".FnM1bbIQ", "[data-e2e='search-card-duration']"]);
    const likes = extractText(card, [".pMq55q1M span", "[data-e2e='search-card-like-count']"]);
    const cover = extractCover(card);
    const detailUrl = extractDetailUrl(card, videoId);

    results.push({
      videoId,
      title,
      author,
      duration,
      likes,
      cover,
      detailUrl
    });
  }

  return results;
}

function extractCover(card) {
  const image = card.querySelector("img[src^='https://']");
  if (image instanceof HTMLImageElement && image.src) {
    return image.src;
  }

  const coverNode = card.querySelector("[style*='background-image']");
  const style = coverNode?.getAttribute("style") || "";
  const match = style.match(/url\("?(https:[^")]+)"?\)/);
  return match ? match[1] : "";
}

function extractDetailUrl(card, videoId) {
  const anchor = card.querySelector("a[href*='/video/']");
  if (anchor instanceof HTMLAnchorElement && anchor.href) {
    return anchor.href;
  }

  return videoId ? `https://www.douyin.com/video/${videoId}` : "";
}

function getResultHosts() {
  const hosts = [];
  const seen = new Set();

  for (const card of document.querySelectorAll(CARD_SELECTOR)) {
    if (!seen.has(card)) {
      seen.add(card);
      hosts.push(card);
    }
  }

  for (const node of document.querySelectorAll(VIDEO_ID_SELECTOR)) {
    const host = findCardHost(node);
    if (host && !seen.has(host)) {
      seen.add(host);
      hosts.push(host);
    }
  }

  return hosts;
}

function findCardHost(node) {
  if (!(node instanceof HTMLElement)) {
    return null;
  }

  return (
    node.closest(CARD_SELECTOR) ||
    node.closest(".AMqhOzPC") ||
    node.closest("[id^='waterfall_item_']") ||
    node
  );
}
