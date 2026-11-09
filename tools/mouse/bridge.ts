// Pulse's bridge to OpenMouse's mouse protocol library (AGPL-3.0, https://github.com/OpenMouse-Project/mouse-protocol).
// Bundled at build time into Assets/Mouse/openmouse.js and run inside Pulse's script engine. Pulse provides a WebHID
// device made from Windows' HID functions, so OpenMouse's drivers run unchanged, without a browser.
// @ts-nocheck
import { deviceImage, deviceImageFilename, UNKNOWN_DEVICE_FILENAME, UNKNOWN_KEYBOARD_FILENAME } from "../../.mouse-app/src/shared/device-images.ts";
import { createSupportedClient, clientSupportScore, deviceBrand } from "../../.mouse-protocol/src/drivers/registry.ts";

// ───────── what a browser normally provides ─────────

const g = globalThis;
g.window = g;
let timerSeq = 1;
const timers = new Map();
g.setTimeout = (fn, ms, ...args) => { const id = timerSeq++; timers.set(id, () => fn(...args)); __pulseHost.timer(id, Number(ms) || 0); return id; };
g.clearTimeout = (id) => { timers.delete(id); };
g.setInterval = (fn, ms) => { let id; const tick = () => { fn(); id = g.setTimeout(tick, ms); }; id = g.setTimeout(tick, ms); return { cancel: () => g.clearTimeout(id) }; };
g.clearInterval = (h) => h?.cancel?.();
g.__pulseFire = (id) => { const f = timers.get(id); timers.delete(id); if (f) f(); };
g.performance = g.performance ?? { now: () => Date.now() };
g.crypto = g.crypto ?? { getRandomValues: (a) => { for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 256); return a; } };
g.structuredClone = g.structuredClone ?? ((v) => (v === undefined ? v : JSON.parse(JSON.stringify(v))));
const store = new Map();
g.localStorage = g.localStorage ?? { getItem: (k) => (store.has(k) ? store.get(k) : null), setItem: (k, v) => store.set(k, String(v)), removeItem: (k) => store.delete(k) };
g.console = { log: (...a) => __pulseHost.log(a.map(String).join(" ")), warn: (...a) => __pulseHost.log(a.map(String).join(" ")), error: (...a) => __pulseHost.log(a.map(String).join(" ")), debug: () => {}, info: () => {} };
class Utf8Decoder { decode(b) { const u = b instanceof Uint8Array ? b : new Uint8Array(b?.buffer ?? b ?? [], b?.byteOffset ?? 0, b?.byteLength); let s = ""; for (let i = 0; i < u.length; i++) s += "%" + u[i].toString(16).padStart(2, "0"); try { return decodeURIComponent(s); } catch { return String.fromCharCode(...u); } } }
class Utf8Encoder { encode(t) { const s = unescape(encodeURIComponent(String(t))); const u = new Uint8Array(s.length); for (let i = 0; i < s.length; i++) u[i] = s.charCodeAt(i); return u; } }
g.TextDecoder = g.TextDecoder ?? Utf8Decoder;
g.TextEncoder = g.TextEncoder ?? Utf8Encoder;
if (typeof g.Event === "undefined") g.Event = class Event { constructor(type) { this.type = type; } };

// ───────── calls into Pulse (answered later through __pulseDone) ─────────

let callSeq = 1;
const pending = new Map();
function host(method, ...args) {
  return new Promise((resolve, reject) => {
    const token = callSeq++;
    pending.set(token, { resolve, reject });
    __pulseHid[method](token, ...args);
  });
}
g.__pulseDone = (token, ok, value) => {
  const p = pending.get(token);
  pending.delete(token);
  if (!p) return;
  if (ok) p.resolve(value); else p.reject(new Error(value));
};
const hex = (bytes) => Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
const unhex = (s) => { const u = new Uint8Array(s.length / 2); for (let i = 0; i < u.length; i++) u[i] = parseInt(s.substr(i * 2, 2), 16); return u; };
const bytesOf = (data) => (data instanceof Uint8Array ? data : ArrayBuffer.isView(data) ? new Uint8Array(data.buffer, data.byteOffset, data.byteLength) : new Uint8Array(data));

// ───────── a WebHID device made of Windows' HID ─────────

class PulseHidDevice {
  constructor(info) {
    this.key = info.key;
    this.vendorId = info.vendorId;
    this.productId = info.productId;
    this.productName = info.productName;
    this.collections = info.collections;
    this.opened = false;
    this.listeners = new Set();
  }
  async open() { if (!this.opened) { await host("open", this.key); this.opened = true; } }
  async close() { if (this.opened) { this.opened = false; await host("close", this.key); } }
  async forget() { await this.close(); }
  sendReport(id, data) { return host("send", this.key, id, hex(bytesOf(data))); }
  sendFeatureReport(id, data) { return host("setFeature", this.key, id, hex(bytesOf(data))); }
  async receiveFeatureReport(id) { const u = unhex(await host("getFeature", this.key, id)); return new DataView(u.buffer); }
  addEventListener(type, fn) { if (type === "inputreport") this.listeners.add(fn); }
  removeEventListener(type, fn) { if (type === "inputreport") this.listeners.delete(fn); }
  set oninputreport(fn) { this._on && this.listeners.delete(this._on); this._on = fn; if (fn) this.listeners.add(fn); }
  get oninputreport() { return this._on ?? null; }
  _emit(reportId, h) {
    const u = unhex(h);
    const event = { type: "inputreport", device: this, reportId, data: new DataView(u.buffer) };
    for (const fn of [...this.listeners]) { try { fn(event); } catch (e) { console.error(e?.message ?? e); } }
  }
}

const devices = new Map();
function refresh(listJson) {
  const list = JSON.parse(listJson ?? __pulseHid.list());
  const seen = new Set();
  for (const info of list) {
    seen.add(info.key);
    if (!devices.has(info.key)) devices.set(info.key, new PulseHidDevice(info));
  }
  for (const k of [...devices.keys()]) if (!seen.has(k)) devices.delete(k);
  return [...devices.values()];
}
g.__pulseInput = (key, reportId, h) => devices.get(key)?._emit(reportId, h);
g.navigator = g.navigator ?? {};
g.navigator.hid = {
  getDevices: async () => refresh(),
  requestDevice: async () => [],
  addEventListener() {},
  removeEventListener() {},
};

// ───────── what Pulse calls ─────────

const clients = new Map();
const users = new Map();   // how many open clients use each device: it's only closed when the last one is done
let clientSeq = 1;
const toJson = (v) => JSON.stringify(v ?? null, (_, x) => (x instanceof Map ? Object.fromEntries(x) : ArrayBuffer.isView(x) ? Array.from(x) : typeof x === "bigint" ? Number(x) : x));
function methodsOf(o) {
  const names = new Set();
  for (let p = o; p && p !== Object.prototype; p = Object.getPrototypeOf(p))
    for (const n of Object.getOwnPropertyNames(p)) if (typeof o[n] === "function" && n !== "constructor" && !n.startsWith("_")) names.add(n);
  return [...names].sort();
}

/** OpenMouse's product picture for a device name, or "" when it has none (then Pulse shows the "?" silhouette). */
function imageFor(name, kind) {
  if (!name) return "";
  const file = deviceImageFilename(name);
  if (file === UNKNOWN_DEVICE_FILENAME || file === UNKNOWN_KEYBOARD_FILENAME) return "";
  const url = deviceImage(null, name, kind === "keyboard" ? "keyboard" : "mouse");
  return url.startsWith("/") ? "https://openmouse.app" + url : url;
}

g.PulseMouse = {
  image(name, kind) { return imageFor(name, kind); },
  /** Interfaces OpenMouse has a driver for, best first: [{ key, vendorId, productId, productName, score }]. */
  /** Same as scan, from a device list instead of the PC's devices (for tests). */
  scanList(listJson) { return this.scan(listJson); },
  scan(listJson) {
    const out = [];
    for (const d of refresh(listJson)) {
      let score = 0;
      try { score = clientSupportScore(d); } catch { score = 0; }
      if (score > 0) out.push({ key: d.key, vendorId: d.vendorId, productId: d.productId, productName: d.productName, score, image: imageFor(d.productName) });
    }
    out.sort((a, b) => b.score - a.score);
    return toJson(out);
  },
  async open(key) {
    const d = devices.get(key) ?? refresh().find((x) => x.key === key);
    if (!d) throw new Error("That device isn't connected any more.");
    const client = createSupportedClient(d);
    if (!client) throw new Error("No driver for this device.");
    if (typeof client.open === "function") await client.open(); else await d.open();
    const id = clientSeq++;
    clients.set(id, { client, key });
    users.set(key, (users.get(key) ?? 0) + 1);
    let brand = "";
    try { brand = deviceBrand(client); } catch {}
    // Option lists a driver publishes (sleep timeouts in seconds, debounce choices), as OpenMouse reads them.
    const list = (name) => { try { const v = typeof client[name] === "function" ? client[name]() : null; return Array.isArray(v) ? [...v] : null; } catch { return null; } };
    const num = (name) => { try { const v = typeof client[name] === "function" ? client[name]() : null; return typeof v === "number" ? v : null; } catch { return null; } };
    const options = { sleep: list("getSleepOptions"), debounce: list("getDebounceOptions"), debounceMax: num("getDebounceMaxMs"), canDisableSleep: client.canDisableSleep === true };
    return toJson({ id, brand, methods: methodsOf(client), options });
  },
  async status(id) {
    const c = clients.get(id)?.client;
    if (!c) throw new Error("Not open.");
    return toJson(await c.readStatus());
  },
  async call(id, name, argsJson) {
    const c = clients.get(id)?.client;
    if (!c) throw new Error("Not open.");
    if (typeof c[name] !== "function") throw new Error(`This mouse can't do ${name}.`);
    return toJson(await c[name](...JSON.parse(argsJson || "[]")));
  },
  async close(id) {
    const entry = clients.get(id);
    clients.delete(id);
    if (!entry) return "null";
    const left = (users.get(entry.key) ?? 1) - 1;
    if (left > 0) { users.set(entry.key, left); return "null"; }
    users.delete(entry.key);
    try { if (typeof entry.client.close === "function") await entry.client.close(); else await devices.get(entry.key)?.close(); } catch {}
    return "null";
  },
};
