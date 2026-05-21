// WebGL Build Host device log shim. Injected (server-side) as the first script in
// <head> of every served build, so it wraps console BEFORE Unity loads and
// streams logs back to the host over a WebSocket. Zero project changes needed.
(function () {
  if (window.__webhostDevlog) return;
  window.__webhostDevlog = true;

  // Stable per-device id so refreshes/reconnects map to the same editor tab
  // instead of spawning a new one each page load.
  var KEY = "__webhost_device_id";
  var id;
  try {
    id = localStorage.getItem(KEY);
    if (!id) {
      id = (window.crypto && crypto.randomUUID) ? crypto.randomUUID()
                                                : (Date.now() + "-" + Math.random().toString(16).slice(2));
      localStorage.setItem(KEY, id);
    }
  } catch (e) {
    id = Date.now() + "-" + Math.random().toString(16).slice(2);
  }

  var queue = [];
  var ws = null;
  var connected = false;

  // Render-frame counter so each log reports the frame it was emitted on.
  var frame = 0;
  function rafTick() { frame++; if (window.requestAnimationFrame) requestAnimationFrame(rafTick); }
  if (window.requestAnimationFrame) requestAnimationFrame(rafTick);

  // Brief full-screen flash (+ vibrate) so you can tell which physical device
  // maps to a tab in the editor.
  function showIdentify() {
    try {
      var o = document.createElement("div");
      o.style.cssText = "position:fixed;left:0;top:0;right:0;bottom:0;z-index:2147483647;" +
        "background:rgba(45,120,255,0.88);display:flex;align-items:center;justify-content:center;" +
        "color:#fff;font:bold 30px -apple-system,system-ui,sans-serif;pointer-events:none;";
      o.textContent = "WebGL Build Host";
      (document.body || document.documentElement).appendChild(o);
      setTimeout(function () { try { o.parentNode.removeChild(o); } catch (e) {} }, 1200);
      if (navigator.vibrate) { try { navigator.vibrate([200, 100, 200]); } catch (e) {} }
    } catch (e) {}
  }

  function connect() {
    try {
      var proto = location.protocol === "https:" ? "wss:" : "ws:";
      ws = new WebSocket(proto + "//" + location.host + "/__webhost/logs?id=" + encodeURIComponent(id));
      ws.onopen = function () {
        connected = true;
        send({
          t: "hello",
          ua: navigator.userAgent,
          w: screen.width, h: screen.height,
          dpr: window.devicePixelRatio || 1,
          gpu: "", // never create a WebGL context here - see note below
          url: location.href
        });
        flush();
      };
      ws.onmessage = function (ev) {
        var m; try { m = JSON.parse(ev.data); } catch (e) { return; }
        if (!m || !m.t) return;
        if (m.t === "reload") { try { location.reload(); } catch (e) {} }
        else if (m.t === "identify") { showIdentify(); }
      };
      ws.onclose = function () { connected = false; ws = null; setTimeout(connect, 1000); };
      ws.onerror = function () { try { ws.close(); } catch (e) {} };
    } catch (e) {
      setTimeout(connect, 1000);
    }
  }

  function send(obj) {
    try { if (ws && connected) { ws.send(JSON.stringify(obj)); return true; } } catch (e) {}
    return false;
  }
  function flush() { while (queue.length && connected) { if (!send(queue[0])) break; queue.shift(); } }

  function emit(level, args) {
    var msg;
    try { msg = Array.prototype.map.call(args, fmt).join(" "); }
    catch (e) { msg = String(args); }
    queue.push({ t: "log", level: level, msg: msg, ts: Date.now(), frame: frame });
    if (queue.length > 2000) queue.shift(); // bound memory if offline
    flush();
  }

  function fmt(a) {
    if (a instanceof Error) return a.stack || (a.name + ": " + a.message);
    if (a === null) return "null";
    if (a === undefined) return "undefined";
    if (typeof a === "object") { try { return JSON.stringify(a); } catch (e) { return String(a); } }
    return String(a);
  }

  // NOTE: we deliberately do NOT probe the GPU here. Reading the renderer name
  // needs a WebGL context, and on iOS Safari (which caps simultaneous WebGL
  // contexts) creating a throwaway context races with Unity's own context
  // creation - Unity can lose, createUnityInstance fails, and the AR content
  // never appears. The shim must be completely non-invasive to the page. GPU
  // info, if wanted, should come from Unity's SystemInfo via the bridge.

  // Wrap console.*
  ["log", "info", "warn", "error", "debug"].forEach(function (lv) {
    var orig = console[lv] ? console[lv].bind(console) : function () {};
    console[lv] = function () {
      emit(lv === "debug" ? "log" : lv, arguments);
      orig.apply(console, arguments);
    };
  });

  // Uncaught errors + promise rejections (where WebGL crashes surface).
  window.addEventListener("error", function (e) {
    var where = e.filename ? " (" + e.filename + ":" + e.lineno + ":" + e.colno + ")" : "";
    emit("error", [(e.message || "Uncaught error") + where]);
  });
  window.addEventListener("unhandledrejection", function (e) {
    var r = e.reason;
    emit("error", ["Unhandled promise rejection: " + (r && r.stack ? r.stack : fmt(r))]);
  });

  // Liveness heartbeat.
  setInterval(function () { send({ t: "ping", ts: Date.now() }); }, 5000);

  connect();
})();
