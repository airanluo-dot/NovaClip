(function () {
  "use strict";

  const schemaVersion = 1;
  const send = (type, payload) => {
    if (!window.chrome || !window.chrome.webview) return;
    window.chrome.webview.postMessage({ schemaVersion, type, payload });
  };

  const isBilibiliHost = (host) => host === "bilibili.com" || host.endsWith(".bilibili.com");
  const isVideoPath = (path) => /^\/video\/(?:av|bv)[0-9a-z]+/i.test(path);
  const isBangumiPath = (path) => /^\/bangumi\/play\//i.test(path);
  const text = (value, fallback) => typeof value === "string" && value.trim() ? value.trim() : fallback;
  const number = (value) => {
    const result = Number(value);
    return Number.isFinite(result) ? result : null;
  };

  const currentKind = () => isVideoPath(location.pathname) ? "video" : isBangumiPath(location.pathname) ? "bangumi" : "other";

  function getPageData() {
    const scripts = Array.from(document.scripts);
    for (const script of scripts) {
      const value = script.textContent || "";
      const initialMatch = value.match(/window\.__INITIAL_STATE__\s*=\s*([\s\S]+?);?\s*(?:<|$)/);
      if (initialMatch) {
        try { return JSON.parse(initialMatch[1].replace(/;\s*$/, "")); } catch (_) { /* Continue to the next source. */ }
      }
      if (script.id === "__NEXT_DATA__") {
        try { return JSON.parse(value); } catch (_) { /* Continue to the next source. */ }
      }
    }
    return window.__INITIAL_STATE__ || window.__NEXT_DATA__ || null;
  }

  function findObject(root, names) {
    if (!root || typeof root !== "object") return null;
    if (!Array.isArray(root)) {
      for (const name of names) if (root[name] && typeof root[name] === "object") return root[name];
      for (const key of Object.keys(root)) {
        const result = findObject(root[key], names);
        if (result) return result;
      }
    } else {
      for (const item of root) {
        const result = findObject(item, names);
        if (result) return result;
      }
    }
    return null;
  }

  function readContext() {
    const kind = currentKind();
    if (kind === "other") return;
    const pageData = getPageData();
    const video = findObject(pageData, ["videoData", "videoInfo", "arc"]);
    const title = text(video && (video.title || video.long_title), text(document.title.replace(/_哔哩哔哩_bilibili$/, ""), "Bilibili media"));
    const match = location.pathname.match(/\/video\/(av|bv)([0-9a-z]+)/i);
    const bvid = video && text(video.bvid, null) || (match && /^bv/i.test(match[1]) ? `${match[1]}${match[2]}` : null);
    const aid = video && number(video.aid || video.avid) || (match && /^av/i.test(match[1]) ? number(match[2]) : null);
    const cid = video && number(video.cid);
    const episodeId = video && number(video.ep_id || video.episode_id);
    send("pageContextChanged", { url: location.href, kind, aid, bvid, cid, episodeId, page: number(new URLSearchParams(location.search).get("p")) || 1, title, episodeTitle: text(video && video.long_title, null) });
    if (window.__PLAYURL_HYDRATE_DATA__) send("hydrateDataFound", { url: location.href, kind });
  }

  function wrapHistory(name) {
    const original = history[name];
    if (typeof original !== "function" || original.__novaClipWrapped) return;
    const wrapped = function () {
      const result = original.apply(this, arguments);
      window.setTimeout(readContext, 0);
      return result;
    };
    wrapped.__novaClipWrapped = true;
    history[name] = wrapped;
  }

  function start() {
    if (!isBilibiliHost(location.hostname)) return;
    send("bridgeReady", { url: location.href });
    wrapHistory("pushState");
    wrapHistory("replaceState");
    window.addEventListener("popstate", readContext, { passive: true });
    window.addEventListener("hashchange", readContext, { passive: true });
    readContext();
    window.setTimeout(readContext, 1200);
    window.setTimeout(readContext, 3500);
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
