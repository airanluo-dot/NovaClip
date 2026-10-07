(function () {
  "use strict";

  if (window.__novaClipProbeMedia) return;
  const schemaVersion = 1;
  const documentId = typeof crypto !== "undefined" && typeof crypto.randomUUID === "function"
    ? crypto.randomUUID() : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  const maxJsonLength = 1_500_000;
  const send = (type, payload) => {
    if (!window.chrome || !window.chrome.webview) return;
    try { window.chrome.webview.postMessage({ schemaVersion, type, payload: { ...payload, documentId } }); }
    catch (_) { /* The WebView may be closing. */ }
  };
  const isBilibiliHost = (host) => {
    const normalized = String(host || "").replace(/\.+$/, "").toLowerCase();
    return normalized === "bilibili.com" || normalized.endsWith(".bilibili.com") || normalized === "b23.tv";
  };
  const object = (value) => value && typeof value === "object" && !Array.isArray(value) ? value : null;
  const text = (value, fallback = null) => typeof value === "string" && value.trim() ? value.trim().slice(0, 4096) : fallback;
  const number = (value) => {
    if (value === null || value === undefined || value === "") return null;
    const result = Number(value);
    return Number.isSafeInteger(result) && result > 0 ? result : null;
  };
  const canonicalBvid = (value) => typeof value === "string" && /^bv/i.test(value) ? `BV${value.slice(2)}` : value || "";
  const sameBvid = (left, right) => canonicalBvid(left) === canonicalBvid(right);
  const route = () => {
    const video = location.pathname.match(/^\/video\/(BV[0-9a-z]+|av\d+)(?:\/|$)/i);
    const bangumi = location.pathname.match(/^\/bangumi\/play\/(ep|ss)(\d+)(?:\/|$)/i);
    const page = number(new URLSearchParams(location.search).get("p")) || 1;
    const kind = video ? "video" : bangumi ? "bangumi" : "other";
    return {
      kind, page, key: `${video
        ? location.pathname.replace(/^\/video\/bv/i, "/video/BV").replace(/^\/video\/av/i, "/video/av")
        : location.pathname.toLowerCase()}?p=${page}`,
      bvid: video && /^bv/i.test(video[1]) ? video[1] : null,
      aid: video && /^av/i.test(video[1]) ? number(video[1].slice(2)) : null,
      episodeId: bangumi && bangumi[1].toLowerCase() === "ep" ? number(bangumi[2]) : null,
      seasonId: bangumi && bangumi[1].toLowerCase() === "ss" ? number(bangumi[2]) : null
    };
  };
  let active = true;
  let routeKey = null;
  let routeRevision = 0;
  let observedCid = null;
  let identityRevision = 0;
  let timers = [];
  let request = null;
  let lastContext = "";
  let lastMedia = "";
  let lastMediaIdentity = "";
  let lastMediaPayload = null;
  let attempts = new Set();
  let terminalIdentity = "";
  let terminalError = "";
  let eventProbeScheduled = false;
  let videoEventBudget = 4;
  let embeddedPageData;
  const playbackOrigins = new WeakMap();
  const identityKey = (context) => [context.kind, canonicalBvid(context.bvid), context.aid || "", context.cid || "", context.episodeId || "", context.page].join(":");
  const complete = (context) => context && context.cid && (context.bvid || context.aid || context.episodeId);

  function syncRoute() {
    const current = route();
    if (current.key === routeKey) return current;
    routeKey = current.key;
    routeRevision++;
    observedCid = null;
    identityRevision = 0;
    for (const timer of timers) window.clearTimeout(timer);
    timers = [];
    if (request) request.abort();
    request = null;
    attempts = new Set();
    lastContext = lastMedia = lastMediaIdentity = terminalIdentity = terminalError = "";
    lastMediaPayload = null;
    eventProbeScheduled = false;
    videoEventBudget = 4;
    return current;
  }

  // Only JSON script sources are inspected, once per document and within a byte budget.
  // Live globals take precedence so stale server state cannot overwrite SPA state.
  function pageData() {
    if (object(window.__INITIAL_STATE__)) return window.__INITIAL_STATE__;
    if (object(window.__NEXT_DATA__)) return window.__NEXT_DATA__;
    if (embeddedPageData !== undefined) return embeddedPageData;
    embeddedPageData = null;
    let remaining = maxJsonLength;
    const scripts = document.scripts || [];
    for (let index = 0; index < Math.min(scripts.length, 24); index++) {
      const script = scripts[index];
      const value = script.textContent || "";
      if (value.length > remaining) continue;
      remaining -= value.length;
      try {
        if (script.id === "__NEXT_DATA__") {
          embeddedPageData = JSON.parse(value);
          break;
        }
        const match = /(?:^|\s)window\.__INITIAL_STATE__\s*=\s*/.exec(value);
        if (!match) continue;
        const start = match.index + match[0].length;
        if (value[start] !== "{") continue;
        let depth = 0, quoted = false, escaped = false;
        for (let cursor = start; cursor < value.length; cursor++) {
          const character = value[cursor];
          if (quoted) {
            if (escaped) escaped = false;
            else if (character === "\\") escaped = true;
            else if (character === '"') quoted = false;
          } else if (character === '"') quoted = true;
          else if (character === "{" || character === "[") depth++;
          else if (character === "}" || character === "]") {
            if (--depth === 0) {
              embeddedPageData = JSON.parse(value.slice(start, cursor + 1));
              break;
            }
          }
        }
        if (embeddedPageData) break;
      } catch (_) { /* A non-JSON script is not page evidence. */ }
    }
    return embeddedPageData;
  }

  // Metadata traversal has a fixed node/depth budget; media tracks are never discovered this way.
  function metadataCandidates(root, names) {
    if (!object(root)) return [];
    const queue = [{ value: root, depth: 0 }], seen = new WeakSet(), result = [];
    for (let cursor = 0; cursor < queue.length && cursor < 128; cursor++) {
      const { value, depth } = queue[cursor];
      if (!value || typeof value !== "object" || seen.has(value)) continue;
      seen.add(value);
      if (!Array.isArray(value)) {
        for (const name of names) {
          const named = value[name];
          if (object(named) && result.length < 1024) result.push(named);
          else if (Array.isArray(named)) {
            for (let index = 0; index < Math.min(named.length, 1000) && result.length < 1024; index++)
              if (object(named[index])) result.push(named[index]);
          }
        }
      }
      if (depth >= 5) continue;
      const values = Array.isArray(value) ? value.slice(0, 32) : Object.keys(value).slice(0, 32).map((key) => value[key]);
      for (const child of values) {
        if (child && typeof child === "object" && queue.length < 128) queue.push({ value: child, depth: depth + 1 });
      }
    }
    return result;
  }

  function contextFor(current) {
    if (current.kind === "other") return null;
    const state = pageData();
    const roots = [state];
    if (object(window.__NEXT_DATA__) && window.__NEXT_DATA__ !== state) roots.push(window.__NEXT_DATA__);
    let video = null, part = null;
    if (current.kind === "video") {
      video = roots.flatMap((root) => metadataCandidates(root, ["videoData", "videoInfo", "arc"])).find((candidate) =>
        current.bvid ? sameBvid(candidate.bvid, current.bvid) : number(candidate.aid || candidate.avid) === current.aid) || null;
      const pages = video && Array.isArray(video.pages) ? video.pages.slice(0, 1000) : [];
      const indexedPart = pages[current.page - 1];
      part = pages.find((candidate) => candidate && number(candidate.page) === current.page) ||
        (indexedPart && !number(indexedPart.page) ? indexedPart : null);
      // A missing requested part must not inherit the first part's video.cid.
      if (!part && current.page === 1 && video && !pages.length) part = video;
    } else {
      const episodes = roots.flatMap((root) => metadataCandidates(root, ["epInfo", "episodeInfo", "epList", "episodes"]));
      video = episodes.find((candidate) => current.episodeId
        ? number(candidate.id || candidate.ep_id || candidate.episode_id) === current.episodeId
        : number(candidate.season_id || (state && state.mediaInfo && state.mediaInfo.season_id)) === current.seasonId) || null;
      part = video;
    }
    const mediaTitle = state && state.mediaInfo && text(state.mediaInfo.title);
    return {
      url: location.href, kind: current.kind,
      aid: video && number(video.aid || video.avid) || current.aid,
      bvid: video && text(video.bvid) || current.bvid,
      cid: part && number(part.cid),
      episodeId: current.episodeId || (current.kind === "bangumi" && video && number(video.id || video.ep_id || video.episode_id)) || null,
      page: current.page,
      title: text(video && video.title, mediaTitle || text(document.title.replace(/_哔哩哔哩_bilibili$/, ""), "Bilibili media")),
      episodeTitle: text(part && (part.part || part.long_title))
    };
  }

  const playbackData = (root) => {
    if (!object(root)) return null;
    const containers = ["data", "result", "video_info", "videoInfo", "playurl", "playUrl", "playurlData", "playUrlData"];
    const queue = [{ value: root, chain: [root] }], seen = new WeakSet();
    for (let cursor = 0; cursor < queue.length && cursor < 32; cursor++) {
      const entry = queue[cursor];
      if (seen.has(entry.value)) continue;
      seen.add(entry.value);
      if (object(entry.value.dash) || Array.isArray(entry.value.durl)) return { data: entry.value, chain: entry.chain };
      if (entry.chain.length >= 4) continue;
      for (const name of containers) {
        if (object(entry.value[name]) && queue.length < 32)
          queue.push({ value: entry.value[name], chain: [...entry.chain, entry.value[name]] });
      }
    }
    return null;
  };
  function matchesEvidence(value, context) {
    if (!object(value)) return true;
    const bvid = text(value.bvid), aid = number(value.aid || value.avid), cid = number(value.cid);
    const episodeId = number(value.ep_id || value.episode_id);
    return (!bvid || !context.bvid || sameBvid(bvid, context.bvid)) &&
      (!aid || !context.aid || aid === context.aid) &&
      (!cid || cid === context.cid) && (!episodeId || episodeId === context.episodeId);
  }

  // Copy only known PlayURL fields. This excludes unrelated page state/authentication fields
  // and caps strings, arrays and total work before serialization.
  const playbackFields = new Set(["is_drm", "drm_tech_type", "quality", "format", "timelength", "accept_format", "accept_description", "accept_quality", "video_codecid", "support_formats", "dash", "durl", "video", "audio", "dolby", "flac", "duration", "id", "quality", "codecid", "codecs", "codec", "new_id", "new_description", "display_desc", "description", "base_url", "baseUrl", "backup_url", "backupUrl", "url", "size", "length", "order", "width", "height", "bandwidth", "mimeType", "mime_type", "frameRate", "frame_rate", "sar", "startWithSap", "start_with_sap", "SegmentBase", "segment_base", "Initialization", "initialization", "indexRange", "index_range", "bvid", "aid", "avid", "cid", "ep_id", "episode_id"]);
  function boundedPlayback(chain, data) {
    let nodes = 0, characters = 0;
    const seen = new WeakSet();
    function copy(value, depth) {
      if (++nodes > 12_000 || depth > 10) throw new Error("bounded");
      if (typeof value === "string") {
        if (value.length > 16_384 || (characters += value.length) > maxJsonLength) throw new Error("bounded");
        return value;
      }
      if (value === null || typeof value === "number" || typeof value === "boolean") return value;
      if (!value || typeof value !== "object" || seen.has(value)) return undefined;
      seen.add(value);
      if (Array.isArray(value)) {
        if (value.length > 256) throw new Error("bounded");
        return value.map((item) => copy(item, depth + 1));
      }
      const result = {};
      for (const key of Object.keys(value).slice(0, 128)) {
        if (!playbackFields.has(key)) continue;
        const child = copy(value[key], depth + 1);
        if (child !== undefined) result[key] = child;
      }
      return result;
    }
    const denied = chain.find((value) => Number.isSafeInteger(Number(value.code)) && Number(value.code) !== 0);
    const code = denied ? Number(denied.code) : 0;
    const result = { code, data: copy(data, 0) };
    // Restrictions can live on an envelope above video_info. Retain safe markers
    // so selecting a nested playback object cannot discard native DRM checks.
    const drm = chain.find((value) => value.is_drm === true || Number(value.is_drm) > 0);
    const technology = chain.find((value) => Number(value.drm_tech_type) > 0);
    if (drm) result.data.is_drm = true;
    if (technology) result.data.drm_tech_type = Number(technology.drm_tech_type);
    // API messages are diagnostic data, never arbitrary HTML or thrown exception text.
    if (code !== 0) result.message = code === -101 ? "Login required" : [-10403, -403, 6002003].includes(code) ? "Permission required" : "Playback API rejected the request";
    if (JSON.stringify(result).length > maxJsonLength) throw new Error("bounded");
    return result;
  }

  function publishPlayback(root, context, source, endpoint, forceSend = false) {
    if (!complete(context)) return false;
    const selected = playbackData(root);
    if (!selected || !selected.chain.every((value) => matchesEvidence(value, context))) return false;
    const { data, chain } = selected;
    const origin = playbackOrigins.get(root);
    const explicitCid = chain.map((value) => number(value.cid)).find((value) => value !== null);
    // New object references after an SPA switch can still be late results from
    // the previous video. Only the original document's playback state may use
    // route provenance without an explicit CID; later data needs its own ID.
    if (source !== "apiFallback" && !explicitCid && (!origin || origin.revision !== 1 || origin.key !== routeKey || identityRevision !== 1)) return false;
    if (!origin) playbackOrigins.set(root, { key: routeKey, revision: routeRevision });
    try {
      const playUrl = boundedPlayback(chain, data);
      const serialized = JSON.stringify(playUrl), identity = identityKey(context);
      if (!forceSend && identity === lastMediaIdentity && serialized === lastMedia) return true;
      lastMedia = serialized;
      lastMediaIdentity = identity;
      lastMediaPayload = { source, endpoint, playUrl };
      send("hydrateDataFound", { ...context, ...lastMediaPayload });
      return true;
    } catch (_) {
      return false;
    }
  }

  function readCurrent(forceSend = false) {
    if (!active) return null;
    const current = syncRoute(), context = contextFor(current);
    if (!context) return null;
    if (complete(context) && context.cid !== observedCid) {
      observedCid = context.cid;
      identityRevision++;
    }
    const serialized = JSON.stringify(context);
    if (forceSend || serialized !== lastContext) {
      lastContext = serialized;
      send("pageContextChanged", context);
    }
    const globals = [[window.__playinfo__, "pageData"], [window.__PLAYURL_HYDRATE_DATA__, "hydrateData"]];
    let found = false;
    for (const [root, source] of globals) {
      if (!object(root)) continue;
      // Remember a global's route even while CID is still being populated.
      if (!playbackOrigins.has(root)) playbackOrigins.set(root, { key: routeKey, revision: routeRevision });
      found = publishPlayback(root, context, source, undefined, forceSend) || found;
    }
    if (forceSend && !found && lastMediaPayload && lastMediaIdentity === identityKey(context)) {
      send("hydrateDataFound", { ...context, ...lastMediaPayload });
      found = true;
    }
    if (forceSend && !found && terminalIdentity === identityKey(context) && terminalError)
      send("detectionCompleted", { ...context, errorCode: terminalError });
    return { context, found };
  }

  function finish(context, errorCode) {
    const identity = identityKey(context);
    if (terminalIdentity === identity) return;
    terminalIdentity = identity;
    terminalError = errorCode;
    send("detectionCompleted", { ...context, errorCode });
  }

  async function readBoundedResponse(response) {
    if (!response.body || typeof response.body.getReader !== "function") throw new Error("response");
    const reader = response.body.getReader(), decoder = new TextDecoder();
    let result = "", bytes = 0;
    try {
      while (true) {
        const chunk = await reader.read();
        if (chunk.done) break;
        if ((bytes += chunk.value.byteLength) > maxJsonLength * 4) throw new Error("bounded");
        result += decoder.decode(chunk.value, { stream: true });
        if (result.length > maxJsonLength) throw new Error("bounded");
      }
      result += decoder.decode();
      if (result.length > maxJsonLength) throw new Error("bounded");
      return JSON.parse(result);
    } finally {
      try { await reader.cancel(); } catch (_) { /* Already consumed or aborted. */ }
      reader.releaseLock();
    }
  }

  async function probeMedia(finalize = false, forceSend = false) {
    const initial = readCurrent(forceSend);
    if (!initial || initial.found) return;
    const context = initial.context;
    if (!complete(context)) {
      if (finalize) finish(context, "PAGE_CONTEXT_UNAVAILABLE");
      return;
    }
    const identity = identityKey(context), revision = routeRevision;
    if (attempts.has(identity)) return;
    if (attempts.size >= 4 || typeof window.fetch !== "function") { finish(context, "PLAYURL_NOT_FOUND"); return; }
    attempts.add(identity);
    const endpoint = new URL(context.kind === "bangumi" ? "https://api.bilibili.com/pgc/player/web/playurl" : "https://api.bilibili.com/x/player/playurl");
    const parameters = { cid: context.cid, qn: 127, fnval: 4048, fnver: 0, fourk: 1 };
    if (context.aid) parameters.avid = context.aid;
    if (context.bvid) parameters.bvid = context.bvid;
    if (context.episodeId) parameters.ep_id = context.episodeId;
    for (const [key, value] of Object.entries(parameters)) endpoint.searchParams.set(key, String(value));
    const controller = new AbortController();
    if (request) request.abort();
    request = controller;
    const deadline = window.setTimeout(() => controller.abort(), 10_000);
    try {
      const response = await window.fetch(endpoint.href, { credentials: "include", signal: controller.signal });
      const root = await readBoundedResponse(response);
      const latest = readCurrent();
      if (controller.signal.aborted || revision !== routeRevision || !latest || latest.found || identity !== identityKey(latest.context)) return;
      const code = Number(root && root.code) || 0;
      if (code !== 0) {
        lastMediaPayload = { source: "apiFallback", endpoint: endpoint.href,
          playUrl: { code, message: code === -101 ? "Login required" : [-10403, -403, 6002003].includes(code) ? "Permission required" : "Playback API rejected the request", data: {} } };
        lastMediaIdentity = identity;
        lastMedia = JSON.stringify(lastMediaPayload.playUrl);
        send("hydrateDataFound", { ...latest.context, ...lastMediaPayload });
        finish(latest.context, code === -101 ? "RESOLVE_LOGIN_REQUIRED" :
          [-10403, -403, 6002003].includes(code) ? "RESOLVE_VIP_REQUIRED" : "RESOLVE_PLAYURL_ERROR");
      } else if (!publishPlayback(root, latest.context, "apiFallback", endpoint.href)) finish(latest.context, "PLAYURL_NOT_FOUND");
    } catch (_) {
      const latest = readCurrent();
      if (revision === routeRevision && latest && identity === identityKey(latest.context) && !latest.found)
        finish(latest.context, "PLAYURL_REQUEST_FAILED");
    } finally {
      window.clearTimeout(deadline);
      if (request === controller) request = null;
    }
  }

  function scheduleRouteProbes() {
    syncRoute();
    const revision = routeRevision;
    for (const timer of timers) window.clearTimeout(timer);
    timers = [0, 250, 1200, 3500].map((delay, index) => window.setTimeout(() => {
      if (revision !== routeRevision) return;
      if (index === 3) void probeMedia(true);
      else readCurrent();
    }, delay));
  }
  function wrapHistory(name) {
    const original = history[name];
    if (typeof original !== "function" || original.__novaClipWrapped) return;
    const wrapped = function () {
      const result = original.apply(this, arguments);
      scheduleRouteProbes();
      return result;
    };
    wrapped.__novaClipWrapped = true;
    try { history[name] = wrapped; } catch (_) { /* A read-only history object remains usable. */ }
  }
  function onVideoEvent(event) {
    if (!active || !event.target || String(event.target.tagName).toLowerCase() !== "video" || eventProbeScheduled || videoEventBudget <= 0) return;
    videoEventBudget--;
    eventProbeScheduled = true;
    const revision = routeRevision;
    timers.push(window.setTimeout(() => {
      eventProbeScheduled = false;
      if (revision === routeRevision) void probeMedia();
    }, 0));
  }
  function start() {
    if (!isBilibiliHost(location.hostname)) return;
    send("bridgeReady", { url: location.href });
    wrapHistory("pushState");
    wrapHistory("replaceState");
    window.addEventListener("popstate", scheduleRouteProbes, { passive: true });
    window.addEventListener("hashchange", scheduleRouteProbes, { passive: true });
    document.addEventListener("loadedmetadata", onVideoEvent, true);
    document.addEventListener("playing", onVideoEvent, true);
    window.addEventListener("pageshow", (event) => {
      if (!event.persisted) return;
      active = true;
      routeRevision++;
      attempts = new Set();
      lastContext = lastMedia = lastMediaIdentity = terminalIdentity = terminalError = "";
      lastMediaPayload = null;
      videoEventBudget = 4;
      send("bridgeReady", { url: location.href });
      readCurrent();
      scheduleRouteProbes();
    }, { passive: true });
    window.addEventListener("pagehide", () => {
      active = false;
      routeRevision++;
      for (const timer of timers) window.clearTimeout(timer);
      timers = [];
      if (request) request.abort();
    }, { passive: true });
    readCurrent();
    scheduleRouteProbes();
  }

  Object.defineProperty(window, "__novaClipDocumentId", { value: documentId });
  Object.defineProperty(window, "__novaClipProbeMedia", { value: () => probeMedia(false, true) });
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start, { once: true });
  else start();
})();
