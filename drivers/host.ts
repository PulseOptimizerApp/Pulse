// Pulse mouse driver host.
//
// Runs inside a hidden WebView2 page in Pulse. It hosts OpenMouse's mouse-protocol drivers (AGPL-3.0-or-later,
// https://github.com/OpenMouse-Project/mouse-protocol) unchanged, and gives them a WebHID API (navigator.hid)
// backed by Pulse's own USB HID access in C# (Core/Devices/NativeHid.cs). Nothing here touches the network.
//
// Messages from Pulse:  { t: "api", id, cmd: "scan" | "status" | "invoke" | "close", key?, method?, args? }
//                       { t: "hid-reply", id, ok, data?, error? }   (answers to our HID requests)
//                       { t: "input", key, reportId, data }         (input reports from an open device)
// Messages to Pulse:    { t: "api-reply", id, ok, data?, error? }
//                       { t: "hid", id, op: "list" | "open" | "close" | "send" | "feature-send" | "feature-get", ... }

import { createSupportedClient, clientSupportScore, deviceBrand } from "./vendor/mouse-protocol/src/drivers/index.ts";

type Json = Record<string, unknown>;
declare const chrome: { webview: { postMessage(m: unknown): void; addEventListener(t: "message", f: (e: { data: any }) => void): void } };

const post = (m: unknown) => chrome.webview.postMessage(m);

// ───────── HID requests to Pulse ─────────

let nextId = 1;
const waiting = new Map<number, { ok: (v: any) => void; fail: (e: Error) => void }>();

function hid(op: string, args: Json = {}): Promise<any> {
  const id = nextId++;
  return new Promise((ok, fail) => {
    waiting.set(id, { ok, fail });
    post({ t: "hid", id, op, ...args });
  });
}

const toB64 = (buf: BufferSource): string => {
  const bytes = buf instanceof ArrayBuffer ? new Uint8Array(buf) : new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
  let s = "";
  for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
  return btoa(s);
};
const fromB64 = (s: string): Uint8Array => Uint8Array.from(atob(s), (c) => c.charCodeAt(0));

// ───────── a WebHID HIDDevice backed by Pulse ─────────

interface NativeDevice {
  key: string;
  vendorId: number;
  productId: number;
  productName: string;
  collections: HIDCollectionInfo[];
}

class PulseHidDevice extends EventTarget {
  readonly key: string;
  readonly vendorId: number;
  readonly productId: number;
  readonly productName: string;
  readonly collections: readonly HIDCollectionInfo[];
  opened = false;
  oninputreport: ((e: HIDInputReportEvent) => void) | null = null;

  constructor(d: NativeDevice) {
    super();
    this.key = d.key;
    this.vendorId = d.vendorId;
    this.productId = d.productId;
    this.productName = d.productName;
    this.collections = d.collections;
  }

  async open(): Promise<void> {
    if (this.opened) return;
    await hid("open", { key: this.key });
    this.opened = true;
  }

  async close(): Promise<void> {
    if (!this.opened) return;
    this.opened = false;
    await hid("close", { key: this.key }).catch(() => undefined);
  }

  async forget(): Promise<void> { await this.close(); }

  async sendReport(reportId: number, data: BufferSource): Promise<void> {
    if (!this.opened) throw new DOMException("The device must be opened first.", "InvalidStateError");
    await hid("send", { key: this.key, reportId, data: toB64(data) });
  }

  async sendFeatureReport(reportId: number, data: BufferSource): Promise<void> {
    if (!this.opened) throw new DOMException("The device must be opened first.", "InvalidStateError");
    await hid("feature-send", { key: this.key, reportId, data: toB64(data) });
  }

  async receiveFeatureReport(reportId: number): Promise<DataView> {
    if (!this.opened) throw new DOMException("The device must be opened first.", "InvalidStateError");
    const b64: string = await hid("feature-get", { key: this.key, reportId });
    const bytes = fromB64(b64);
    return new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  }

  deliver(reportId: number, data: Uint8Array): void {
    const ev = new Event("inputreport") as any;
    Object.defineProperties(ev, {
      device: { value: this },
      reportId: { value: reportId },
      data: { value: new DataView(data.buffer, data.byteOffset, data.byteLength) },
    });
    this.oninputreport?.(ev);
    this.dispatchEvent(ev);
  }
}

// ───────── navigator.hid ─────────

const devices = new Map<string, PulseHidDevice>();

class PulseHid extends EventTarget {
  onconnect: ((e: Event) => void) | null = null;
  ondisconnect: ((e: Event) => void) | null = null;
  async getDevices(): Promise<PulseHidDevice[]> { return [...devices.values()]; }
  async requestDevice(options: { filters: HIDDeviceFilter[] }): Promise<PulseHidDevice[]> {
    const filters = options?.filters ?? [];
    return [...devices.values()].filter((d) => filters.length === 0 || filters.some((f) =>
      (f.vendorId === undefined || f.vendorId === d.vendorId) &&
      (f.productId === undefined || f.productId === d.productId) &&
      (f.usagePage === undefined || d.collections.some((c) => c.usagePage === f.usagePage && (f.usage === undefined || c.usage === f.usage)))));
  }
}

const shim = new PulseHid();
Object.defineProperty(navigator, "hid", { value: shim, configurable: true });

async function refreshDevices(): Promise<void> {
  const list: NativeDevice[] = await hid("list");
  const seen = new Set<string>();
  for (const d of list) {
    seen.add(d.key);
    if (!devices.has(d.key)) {
      const dev = new PulseHidDevice(d);
      devices.set(d.key, dev);
      const ev = new Event("connect") as any;
      Object.defineProperty(ev, "device", { value: dev });
      shim.onconnect?.(ev);
      shim.dispatchEvent(ev);
    }
  }
  for (const [key, dev] of devices) {
    if (seen.has(key)) continue;
    devices.delete(key);
    clients.delete(key);
    const ev = new Event("disconnect") as any;
    Object.defineProperty(ev, "device", { value: dev });
    shim.ondisconnect?.(ev);
    shim.dispatchEvent(ev);
  }
}

// ───────── drivers ─────────

const clients = new Map<string, any>();

/** One entry per physical device: of a mouse's several HID interfaces, the one the best driver wants. */
async function scan(): Promise<Json[]> {
  await refreshDevices();
  const best = new Map<string, { dev: PulseHidDevice; score: number }>();
  for (const dev of devices.values()) {
    const score = clientSupportScore(dev as unknown as HIDDevice);
    if (score <= 0) continue;
    const group = `${dev.vendorId}:${dev.productId}`;
    const cur = best.get(group);
    if (!cur || score > cur.score) best.set(group, { dev, score });
  }
  const out: Json[] = [];
  for (const { dev } of best.values()) {
    let client = clients.get(dev.key);
    if (!client) {
      client = createSupportedClient(dev as unknown as HIDDevice);
      if (!client) continue;
      clients.set(dev.key, client);
    }
    out.push({ key: dev.key, brand: deviceBrand(client), productName: dev.productName, vendorId: dev.vendorId, productId: dev.productId });
  }
  return out;
}

/** Only settings and reads: no firmware updates, pairing, resets or anything that could brick a device. */
const ALLOWED = /^(set[A-Z]\w*|readStatus|getDpiOptions|readButtons|writeActiveProfile)$/;

async function invoke(key: string, method: string, args: unknown[]): Promise<unknown> {
  const client = clients.get(key);
  if (!client) throw new Error("This device isn't connected.");
  if (!ALLOWED.test(method) || typeof client[method] !== "function") throw new Error(`Not available on this device: ${method}`);
  return await client[method](...(args ?? []));
}

/** JSON-safe copy of what drivers return (Maps, typed arrays, BigInts, class instances). */
function plain(v: unknown, depth = 0): unknown {
  if (depth > 12 || v === undefined || typeof v === "function") return undefined;
  if (v === null || typeof v === "string" || typeof v === "boolean") return v;
  if (typeof v === "number") return Number.isFinite(v) ? v : null;
  if (typeof v === "bigint") return Number(v);
  if (v instanceof Map) return Object.fromEntries([...v].map(([k, x]) => [String(k), plain(x, depth + 1)]));
  if (v instanceof Set) return [...v].map((x) => plain(x, depth + 1));
  if (ArrayBuffer.isView(v)) return Array.from(v as unknown as ArrayLike<number>);
  if (Array.isArray(v)) return v.map((x) => plain(x, depth + 1));
  if (typeof v === "object") {
    if ((v as any) instanceof EventTarget) return undefined;
    const o: Json = {};
    for (const [k, x] of Object.entries(v as Json)) { const p = plain(x, depth + 1); if (p !== undefined) o[k] = p; }
    return o;
  }
  return undefined;
}

// ───────── buttons: one shape for every brand ─────────

const LOGITECH_ACTIONS = ["Left click", "Right click", "Middle click", "Back", "Forward", "Next DPI", "Previous DPI", "Cycle DPI",
  "Default DPI", "DPI Shift", "Tilt left", "Tilt right", "Next profile", "Previous profile", "Cycle profiles", "Battery indicator", "Disabled"];
const LOGITECH_BUTTON_NAMES = ["Left button", "Right button", "Middle button (wheel)", "Back (side)", "Forward (side)", "DPI button"];

/** The mouse's buttons and what each does, plus the actions it accepts. */
async function buttons(key: string): Promise<Json> {
  const client = clients.get(key);
  if (!client) throw new Error("This device isn't connected.");
  if (typeof client.readOnboardProfiles === "function") {
    const profiles = await client.readOnboardProfiles();
    const current = profiles.find((p: any) => p.isCurrent) ?? profiles.find((p: any) => p.enabled) ?? profiles[0];
    if (current) {
      return {
        kind: "onboard",
        buttons: current.buttonAssignments.map((b: any, i: number) => ({ id: String(b.button ?? i), name: LOGITECH_BUTTON_NAMES[i] ?? `Button ${i + 1}`, action: b.action })),
        options: LOGITECH_ACTIONS,
      };
    }
  }
  const status = await client.readStatus();
  if (status?.buttonMappings && typeof client.setButtonMapping === "function") {
    return {
      kind: "mapping",
      buttons: Object.entries(status.buttonMappings).map(([name, action]) => ({ id: name, name, action })),
      options: status.buttonOptions ?? [],
      fixed: status.fixedButtons ?? [],
    };
  }
  return { kind: "none", buttons: [], options: [] };
}

async function setButton(key: string, id: string, action: string): Promise<void> {
  const client = clients.get(key);
  if (!client) throw new Error("This device isn't connected.");
  if (typeof client.readOnboardProfiles === "function" && typeof client.writeActiveProfile === "function" && /^\d+$/.test(id)) {
    await client.writeActiveProfile({ buttonAssignments: [{ layer: "primary", button: Number(id), binding: action }] });
    return;
  }
  if (typeof client.setButtonMapping !== "function") throw new Error("This mouse's buttons can't be changed here.");
  await client.setButtonMapping(id, action);
}

// ───────── messages ─────────

chrome.webview.addEventListener("message", async (e) => {
  const m = e.data;
  if (!m || typeof m !== "object") return;
  if (m.t === "hid-reply") {
    const w = waiting.get(m.id);
    if (!w) return;
    waiting.delete(m.id);
    if (m.ok) w.ok(m.data); else w.fail(new Error(m.error || "HID request failed"));
    return;
  }
  if (m.t === "input") {
    devices.get(m.key)?.deliver(m.reportId, fromB64(m.data));
    return;
  }
  if (m.t !== "api") return;
  try {
    let data: unknown;
    switch (m.cmd) {
      case "scan": data = await scan(); break;
      case "status": {
        const client = clients.get(m.key);
        const status = (await invoke(m.key, "readStatus", [])) as Json;
        // which settings this mouse's driver can change, so Pulse shows only working controls
        const methods = new Set<string>();
        for (let o = client; o && o !== Object.prototype; o = Object.getPrototypeOf(o))
          for (const name of Object.getOwnPropertyNames(o)) if (ALLOWED.test(name) && typeof client[name] === "function") methods.add(name);
        data = { ...status, __methods: [...methods] };
        break;
      }
      case "invoke": data = await invoke(m.key, m.method, m.args); break;
      case "buttons": data = await buttons(m.key); break;
      case "set-button": await setButton(m.key, m.id2, m.action); data = await buttons(m.key); break;
      case "close": await clients.get(m.key)?.close?.(); data = true; break;
      default: throw new Error("Unknown command " + m.cmd);
    }
    post({ t: "api-reply", id: m.id, ok: true, data: plain(data) });
  } catch (err) {
    post({ t: "api-reply", id: m.id, ok: false, error: err instanceof Error ? err.message : String(err) });
  }
});

post({ t: "ready" });
