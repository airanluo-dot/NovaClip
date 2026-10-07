const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");

const bridge = fs.readFileSync(path.join(__dirname, "../../assets/js/bilibili-bridge.js"), "utf8");
const playback = (cid, url = "https://media.example.test/video.m4s") => ({
  code: 0, data: { ...(cid ? { cid } : {}), quality: 80, accept_quality: [80, 64], accept_description: ["1080P", "720P"],
    dash: { video: [{ id: 80, base_url: url, codecs: "avc1" }], audio: [{ id: 30280, base_url: "https://media.example.test/audio.m4s" }] } }
});
const state = (bvid = "BV1CURRENT", pages = [{ page: 1, cid: 101, part: "Part 1" }]) => ({
  videoData: { bvid, aid: bvid === "BV1CURRENT" ? 11 : 22, cid: pages[0].cid, title: bvid, pages }
});

function harness(options = {}) {
  let timerId = 0;
  const timers = new Map(), messages = [], listeners = new Map(), requests = [];
  const location = {};
  // URL's getters are not enumerable; use an explicit location snapshot.
  const setLocation = (href) => {
    const url = new URL(href);
    Object.assign(location, { href: url.href, hostname: url.hostname, pathname: url.pathname, search: url.search });
  };
  setLocation(options.url || "https://www.bilibili.com/video/BV1CURRENT");
  const addEventListener = (name, callback) => {
    if (!listeners.has(name)) listeners.set(name, []);
    listeners.get(name).push(callback);
  };
  const document = { readyState: "complete", title: "Example_哔哩哔哩_bilibili", scripts: options.scripts || [], addEventListener };
  const window = {
    chrome: { webview: { postMessage: (message) => messages.push(JSON.parse(JSON.stringify(message))) } },
    __INITIAL_STATE__: options.state,
    __playinfo__: options.playinfo,
    __PLAYURL_HYDRATE_DATA__: options.hydrate,
    __NEXT_DATA__: options.nextState,
    addEventListener,
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
    clearTimeout: (id) => timers.delete(id),
    fetch: async (endpoint, requestOptions) => {
      requests.push({ endpoint, options: requestOptions });
      if (!options.fetch) throw new Error("offline");
      const json = await options.fetch(endpoint, requestOptions);
      const bytes = new TextEncoder().encode(JSON.stringify(json));
      let read = false;
      return { body: { getReader: () => ({ read: async () => read ? { done: true } : (read = true, { done: false, value: bytes }), cancel: async () => {}, releaseLock: () => {} }) } };
    }
  };
  const history = {
    pushState: (_state, _title, href) => { if (href) setLocation(new URL(href, location.href).href); },
    replaceState: (_state, _title, href) => { if (href) setLocation(new URL(href, location.href).href); }
  };
  vm.runInNewContext(bridge, { window, document, location, history, crypto: { randomUUID: () => "document-1" }, URL, URLSearchParams, AbortController, TextDecoder, Date, Math });
  return {
    window, document, history, location, messages, requests, timers,
    context: () => messages.filter((message) => message.type === "pageContextChanged").at(-1)?.payload,
    media: () => messages.filter((message) => message.type === "hydrateDataFound"),
    completed: () => messages.filter((message) => message.type === "detectionCompleted"),
    event: (name, event = {}) => { for (const callback of listeners.get(name) || []) callback(event); },
    flush: async (maximumDelay = Infinity) => {
      // One finite probe group; the bridge must not schedule itself forever.
      for (const [id, item] of [...timers].sort((left, right) => left[1].delay - right[1].delay)) {
        if (!timers.has(id) || item.delay > maximumDelay) continue;
        timers.delete(id);
        item.callback();
        for (let turn = 0; turn < 8; turn++) await Promise.resolve();
      }
    }
  };
}

test("hydrates actual DASH tracks after context and sends each unchanged context/result once", async () => {
  const h = harness({ state: state(), playinfo: playback(101) });
  assert.equal(h.context().cid, 101);
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.playUrl.data.dash.audio.length, 1);
  await h.flush();
  assert.equal(h.messages.filter((message) => message.type === "pageContextChanged").length, 1);
  assert.equal(h.media().length, 1);
  assert.equal(h.requests.length, 0);
  assert.ok(h.messages.every((message) => message.payload.documentId === "document-1"));
  assert.equal(h.timers.size, 0);
});

test("playback arriving before identity survives later CID enrichment", async () => {
  const h = harness({ playinfo: playback() });
  assert.equal(h.context().cid, null);
  assert.equal(h.media().length, 0);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.completed().length, 0);
  h.window.__INITIAL_STATE__ = state();
  await h.window.__novaClipProbeMedia();
  assert.equal(h.context().cid, 101);
  assert.equal(h.media().length, 1);
  assert.equal(h.requests.length, 0);
});

test("identity arriving before playback converges on a finite video event probe", async () => {
  const h = harness({ state: state() });
  h.window.__PLAYURL_HYDRATE_DATA__ = { video_info: playback(101).data };
  h.event("playing", { target: { tagName: "VIDEO" } });
  await h.flush(0);
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.source, "hydrateData");
  assert.equal(h.requests.length, 0);
});

test("selected part takes pages[p-1].cid instead of first-part video.cid", () => {
  const h = harness({ url: "https://www.bilibili.com/video/BV1CURRENT?p=2", state: state("BV1CURRENT", [{ page: 1, cid: 101 }, { page: 2, cid: 102, part: "Second" }]), playinfo: playback(102) });
  assert.equal(h.context().page, 2);
  assert.equal(h.context().cid, 102);
  assert.equal(h.context().episodeTitle, "Second");
  assert.equal(h.media()[0].payload.cid, 102);
});

test("a nonexistent part never inherits another part's CID", async () => {
  const h = harness({ url: "https://www.bilibili.com/video/BV1CURRENT?p=2", state: state(), playinfo: playback(101) });
  assert.equal(h.context().cid, null);
  await h.flush();
  assert.equal(h.media().length, 0);
  assert.equal(h.requests.length, 0);
  assert.equal(h.completed().at(-1).payload.errorCode, "PAGE_CONTEXT_UNAVAILABLE");
});

test("station navigation rejects stale metadata and a stale identity-free playback global", async () => {
  const h = harness({ state: state(), playinfo: playback() });
  h.history.pushState({}, "", "/video/BV1NEXT");
  await h.flush(0);
  assert.equal(h.context().bvid, "BV1NEXT");
  assert.equal(h.context().cid, null);
  h.window.__INITIAL_STATE__ = state("BV1NEXT", [{ page: 1, cid: 202 }]);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.context().cid, 202);
  assert.equal(h.media().length, 1);
  assert.equal(h.requests.length, 1);
});

test("part switch rejects old embedded CID even after page identity updates", async () => {
  const h = harness({ state: state("BV1CURRENT", [{ page: 1, cid: 101 }, { page: 2, cid: 102 }]), playinfo: playback(101) });
  h.history.pushState({}, "", "?p=2");
  await h.flush(0);
  assert.equal(h.context().cid, 102);
  assert.equal(h.media().length, 1);
  h.window.__playinfo__ = playback(102);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.media().length, 2);
  assert.equal(h.media().at(-1).payload.cid, 102);
});

test("ordinary authenticated fallback is bounded to one request per current identity", async () => {
  const h = harness({ state: state(), fetch: async () => playback(101) });
  await h.window.__novaClipProbeMedia();
  await h.window.__novaClipProbeMedia();
  await h.flush();
  assert.equal(h.requests.length, 1);
  assert.equal(h.requests[0].options.credentials, "include");
  const endpoint = new URL(h.requests[0].endpoint);
  assert.equal(endpoint.pathname, "/x/player/playurl");
  assert.equal(endpoint.searchParams.get("cid"), "101");
  assert.equal(endpoint.searchParams.has("w_rid"), false);
  assert.equal(h.media()[0].payload.source, "apiFallback");
});

test("late API result cannot publish after rapid video switches", async () => {
  let resolveOld;
  const old = new Promise((resolve) => { resolveOld = resolve; });
  const h = harness({ state: state(), fetch: async (endpoint) => new URL(endpoint).searchParams.get("cid") === "101" ? old : playback(202) });
  const pending = h.window.__novaClipProbeMedia();
  h.history.pushState({}, "", "/video/BV1NEXT");
  h.window.__INITIAL_STATE__ = state("BV1NEXT", [{ page: 1, cid: 202 }]);
  await h.window.__novaClipProbeMedia();
  resolveOld(playback(101));
  await pending;
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.bvid, "BV1NEXT");
  assert.equal(h.media()[0].payload.cid, 202);
  assert.equal(h.requests[0].options.signal.aborted, true);
});

test("permission failure is reported without copying arbitrary API text or auth data", async () => {
  const h = harness({ state: state(), fetch: async () => ({ code: -101, message: "secret-cookie-value", token: "secret-token" }) });
  await h.window.__novaClipProbeMedia();
  assert.equal(h.media()[0].payload.playUrl.code, -101);
  assert.equal(h.media()[0].payload.playUrl.message, "Login required");
  assert.equal(JSON.stringify(h.messages).includes("secret"), false);
  assert.equal(h.completed()[0].payload.errorCode, "RESOLVE_LOGIN_REQUIRED");
  h.window.__playinfo__ = playback(101);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.media().at(-1).payload.playUrl.code, 0);
  assert.equal(h.requests.length, 1);
});

test("page evidence strips authentication fields and refuses oversized tracks", async () => {
  const data = playback(101);
  data.data.cookie = "secret-cookie";
  data.data.dash.video[0].token = "secret-token";
  const h = harness({ state: state(), playinfo: data });
  assert.equal(JSON.stringify(h.messages).includes("secret"), false);
  const oversized = playback(101, "https://media.example.test/" + "a".repeat(17_000));
  const rejected = harness({ state: state(), playinfo: oversized });
  assert.equal(rejected.media().length, 0);
});

test("bangumi chooses the current episode and uses the ordinary PGC endpoint", async () => {
  const h = harness({ url: "https://www.bilibili.com/bangumi/play/ep2", state: { epInfo: { id: 1, cid: 11 }, epList: [{ id: 1, cid: 11 }, { id: 2, cid: 22, aid: 33, bvid: "BV1EP2", long_title: "Episode 2" }], mediaInfo: { title: "Series" } }, fetch: async () => playback(22) });
  assert.equal(h.context().episodeId, 2);
  assert.equal(h.context().cid, 22);
  await h.window.__novaClipProbeMedia();
  assert.equal(new URL(h.requests[0].endpoint).pathname, "/pgc/player/web/playurl");
  assert.equal(new URL(h.requests[0].endpoint).searchParams.get("ep_id"), "2");
});

test("pagehide cancels requests and pending probes", async () => {
  let resolve;
  const h = harness({ state: state(), fetch: () => new Promise((complete) => { resolve = complete; }) });
  const pending = h.window.__novaClipProbeMedia();
  h.event("pagehide");
  assert.equal(h.requests[0].options.signal.aborted, true);
  resolve(playback(101));
  await pending;
  assert.equal(h.media().length, 0);
  assert.equal(h.timers.size, 0);
});

test("high-frequency playback events consume a finite coalesced probe budget", async () => {
  const h = harness({ state: state(), playinfo: playback(101) });
  await h.flush();
  for (let group = 0; group < 20; group++) {
    for (let index = 0; index < 100; index++) h.event("playing", { target: { tagName: "VIDEO" } });
    await h.flush();
  }
  assert.equal(h.media().length, 1);
  assert.equal(h.messages.filter((message) => message.type === "pageContextChanged").length, 1);
  assert.equal(h.requests.length, 0);
  assert.equal(h.timers.size, 0);
});


test("host document handshake replays deduplicated context and current media", async () => {
  const h = harness({ state: state(), playinfo: playback(101) });
  assert.equal(h.window.__novaClipDocumentId, "document-1");
  assert.equal(Object.getOwnPropertyDescriptor(h.window, "__novaClipDocumentId").writable, false);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.messages.filter((message) => message.type === "pageContextChanged").length, 2);
  assert.equal(h.media().length, 2);
  assert.equal(h.media().at(-1).payload.cid, 101);
});

test("a delayed host handshake can replay the one bounded API result", async () => {
  const h = harness({ state: state(), fetch: async () => playback(101) });
  await h.window.__novaClipProbeMedia();
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().length, 2);
  assert.equal(h.media().at(-1).payload.source, "apiFallback");
});


test("a delayed host handshake replays login failure without a duplicate request", async () => {
  const h = harness({ state: state(), fetch: async () => ({ code: -101 }) });
  await h.window.__novaClipProbeMedia();
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().length, 2);
  assert.equal(h.media().at(-1).payload.playUrl.code, -101);
});

test("a delayed host handshake replays exhausted finite context probes", async () => {
  const h = harness();
  await h.flush();
  assert.equal(h.completed().length, 1);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.completed().length, 2);
  assert.equal(h.completed().at(-1).payload.errorCode, "PAGE_CONTEXT_UNAVAILABLE");
});


test("live global state bypasses all script text scanning", async () => {
  const script = { get textContent() { throw new Error("unexpected script inspection"); } };
  const h = harness({ state: state(), playinfo: playback(101), scripts: Array(24).fill(script) });
  await h.flush();
  assert.equal(h.media().length, 1);
});

test("current Next page metadata can replace stale initial-state metadata", () => {
  const h = harness({ url: "https://www.bilibili.com/video/BV1NEXT", state: state(), nextState: { props: { pageProps: state("BV1NEXT", [{ page: 1, cid: 202 }]) } }, playinfo: playback(202) });
  assert.equal(h.context().cid, 202);
  assert.equal(h.context().aid, 22);
  assert.equal(h.media().length, 1);
});


test("back-forward cache restoration reannounces the current document and runs finite probes", async () => {
  const h = harness({ state: state(), playinfo: playback(101) });
  h.event("pagehide");
  const before = h.messages.length;
  await h.window.__novaClipProbeMedia();
  assert.equal(h.messages.length, before);
  h.event("pageshow", { persisted: true });
  assert.equal(h.messages.filter((message) => message.type === "bridgeReady").length, 2);
  assert.equal(h.media().length, 2);
  await h.flush();
  assert.equal(h.timers.size, 0);
  assert.equal(h.requests.length, 0);
});

test("cancelled page cannot emit a terminal state from an old failed API request", async () => {
  let reject;
  const h = harness({ state: state(), fetch: () => new Promise((_resolve, failure) => { reject = failure; }) });
  const pending = h.window.__novaClipProbeMedia();
  h.event("pagehide");
  const before = h.messages.length;
  reject(new Error("network"));
  await pending;
  assert.equal(h.messages.length, before);
  assert.equal(h.completed().length, 0);
});


test("page fallback preserves nonsecret DRM markers for the native permission check", () => {
  const data = playback(101);
  data.data.is_drm = true;
  data.data.drm_tech_type = 2;
  data.data.drm_token = "secret-token";
  const h = harness({ state: state(), playinfo: data });
  assert.equal(h.media()[0].payload.playUrl.data.is_drm, true);
  assert.equal(h.media()[0].payload.playUrl.data.drm_tech_type, 2);
  assert.equal(JSON.stringify(h.messages).includes("secret-token"), false);
});


test("a new identity-free object from an old async completion cannot bind to the next video", async () => {
  const h = harness({ state: state(), playinfo: playback(), fetch: async () => playback(202) });
  h.history.pushState({}, "", "/video/BV1NEXT");
  h.window.__INITIAL_STATE__ = state("BV1NEXT", [{ page: 1, cid: 202 }]);
  h.window.__playinfo__ = playback(null, "https://media.example.test/old-late.m4s");
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().at(-1).payload.cid, 202);
  assert.equal(h.media().at(-1).payload.source, "apiFallback");
  assert.equal(JSON.stringify(h.media().at(-1)).includes("old-late.m4s"), false);
});

test("returning to the original route cannot bind newly assigned identity-free data", async () => {
  const h = harness({ state: state(), playinfo: playback(), fetch: async () => playback(101) });
  h.history.pushState({}, "", "/video/BV1NEXT");
  h.window.__INITIAL_STATE__ = state("BV1NEXT", [{ page: 1, cid: 202 }]);
  await h.flush(0);
  h.history.pushState({}, "", "/video/BV1CURRENT");
  h.window.__INITIAL_STATE__ = state();
  h.window.__playinfo__ = playback(null, "https://media.example.test/old-next.m4s");
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().at(-1).payload.source, "apiFallback");
  assert.equal(JSON.stringify(h.media().at(-1)).includes("old-next.m4s"), false);
});

for (const code of [-10403, -403, 6002003]) test(`permission denial ${code} keeps typed terminal permission status`, async () => {
  const h = harness({ state: state(), fetch: async () => ({ code }) });
  await h.window.__novaClipProbeMedia();
  assert.equal(h.completed()[0].payload.errorCode, "RESOLVE_VIP_REQUIRED");
});


test("a nested hydrate wrapper cannot lose conflicting CID evidence", async () => {
  const h = harness({ state: state("BV1CURRENT", [{ page: 1, cid: 202 }]), hydrate: { code: 0, data: { cid: 101, video_info: playback().data } }, fetch: async () => playback(202) });
  assert.equal(h.media().length, 0);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.source, "apiFallback");
});

test("known camel-case nested hydrate paths retain envelope DRM markers", () => {
  const h = harness({ state: state(), hydrate: { data: { cid: 101, is_drm: true, drm_tech_type: 2, videoInfo: playback().data } } });
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.playUrl.data.is_drm, true);
  assert.equal(h.media()[0].payload.playUrl.data.drm_tech_type, 2);
});

test("a fallback denial arriving after valid page media cannot replace that media", async () => {
  let resolve;
  const h = harness({ state: state(), fetch: () => new Promise((complete) => { resolve = complete; }) });
  const pending = h.window.__novaClipProbeMedia();
  h.window.__playinfo__ = playback(101);
  resolve({ code: -101 });
  await pending;
  assert.equal(h.media().length, 1);
  assert.equal(h.media()[0].payload.playUrl.code, 0);
  assert.equal(h.completed().length, 0);
});

test("same-route CID replacement cannot reuse old identity-free bootstrap media", async () => {
  const h = harness({ state: state(), playinfo: playback(), fetch: async () => playback(202) });
  h.window.__INITIAL_STATE__ = state("BV1CURRENT", [{ page: 1, cid: 202 }]);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().at(-1).payload.source, "apiFallback");
  assert.equal(h.media().at(-1).payload.cid, 202);
});


test("a sparse numbered part list cannot label part 3 as requested part 2", async () => {
  const h = harness({ url: "https://www.bilibili.com/video/BV1CURRENT?p=2", state: state("BV1CURRENT", [{ page: 1, cid: 101 }, { page: 3, cid: 103 }]), playinfo: playback(103) });
  assert.equal(h.context().cid, null);
  await h.flush();
  assert.equal(h.media().length, 0);
  assert.equal(h.requests.length, 0);
});

test("unnumbered page arrays can supply the part by its position", () => {
  const h = harness({ url: "https://www.bilibili.com/video/BV1CURRENT?p=2", state: state("BV1CURRENT", [{ cid: 101 }, { cid: 102 }]), playinfo: playback(102) });
  assert.equal(h.context().cid, 102);
  assert.equal(h.media().length, 1);
});


test("BV payload casing identifies different videos while the BV prefix remains insensitive", async () => {
  const h = harness({ url: "https://www.bilibili.com/video/bv1Current", state: state("BV1Current", [{ page: 1, cid: 101 }]), playinfo: playback(), fetch: async () => playback(202) });
  assert.equal(h.context().cid, 101);
  assert.equal(h.media().length, 1);
  h.history.pushState({}, "", "/video/BV1current");
  await h.flush(0);
  assert.equal(h.context().cid, null);
  assert.equal(h.media().length, 1);
  h.window.__INITIAL_STATE__ = state("BV1current", [{ page: 1, cid: 202 }]);
  await h.window.__novaClipProbeMedia();
  assert.equal(h.requests.length, 1);
  assert.equal(h.media().at(-1).payload.bvid, "BV1current");
  assert.equal(h.media().at(-1).payload.cid, 202);
  assert.equal(h.media().at(-1).payload.source, "apiFallback");
});
