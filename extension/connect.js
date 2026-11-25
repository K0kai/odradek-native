// Connection with the Odradek desktop app: one-click pairing, and self-reload when the app ships a newer extension.
// Loaded after sidepanel.js and shares its globals (backendUrl, backendToken, DEFAULT_BACKEND_URL).

const PAIR_TIMEOUT_MS = 130000; // the app gives up after two minutes

// The desktop app is always on this computer: keep a custom local port, ignore a remote backend.
function localAppUrl() {
  try {
    const url = new URL(backendUrl);
    if (url.protocol === "http:" && ["localhost", "127.0.0.1"].includes(url.hostname.toLowerCase())) return url.origin;
  } catch (_) {
    // no backend configured
  }
  return DEFAULT_BACKEND_URL;
}

// The app's /health answer, or null when it is not running.
async function readAppHealth() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 3000);
  try {
    const response = await fetch(new URL("/health", `${localAppUrl()}/`), { cache: "no-store", credentials: "omit", signal: controller.signal });
    const health = response.ok ? await response.json() : null;
    return health?.service === "odradek-scraper" ? health : null;
  } catch (_) {
    return null;
  } finally {
    clearTimeout(timeout);
  }
}

// Asks the app for a token. The app only answers this extension, and only after the user allows it there.
async function connectToApp() {
  const url = localAppUrl();
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), PAIR_TIMEOUT_MS);
  let response;
  try {
    response = await fetch(new URL("/api/extension/pair", `${url}/`), { method: "POST", credentials: "omit", cache: "no-store", signal: controller.signal });
  } catch (_) {
    throw new Error("Could not reach Odradek. Open the Odradek app on this computer and try again.");
  } finally {
    clearTimeout(timeout);
  }
  const body = await response.json().catch(() => ({}));
  if (response.status === 404) throw new Error("This Odradek version does not support one-click connection. Update the app, or paste a token in Settings.");
  if (!response.ok || typeof body.token !== "string" || !body.token) throw new Error(body.error || `Odradek returned ${response.status}.`);
  await chrome.storage.local.set({ backendUrl: url, backendToken: body.token });
  backendUrl = url;
  backendToken = body.token;
  $("backend-url").value = url;
  $("backend-token").value = body.token;
}

async function runConnect(button) {
  const buttons = [...document.querySelectorAll("[data-connect]")];
  buttons.forEach((item) => { item.disabled = true; });
  status("Allow the connection in the Odradek app window…");
  try {
    await connectToApp();
    $("connect-offer").hidden = true;
    status("Connected to Odradek.");
  } catch (error) {
    status(error.message || "Could not connect to Odradek.");
  } finally {
    buttons.forEach((item) => { item.disabled = false; });
    button.blur();
  }
}

// The app keeps the extension folder up to date; reloading reads it again. Tried once per version, so a folder that did not
// change (or an extension loaded from somewhere else) cannot reload forever. storage.session would be cleared by the reload.
async function reloadIfOutdated(health) {
  const available = health?.extensionVersion;
  if (!available || available === chrome.runtime.getManifest().version) return false;
  const { reloadedForVersion = "" } = await chrome.storage.local.get("reloadedForVersion");
  if (reloadedForVersion === available) return false;
  await chrome.storage.local.set({ reloadedForVersion: available });
  chrome.runtime.reload();
  return true;
}

async function startConnection() {
  await loaded; // the saved address (a custom local port) must be in place first
  const health = await readAppHealth();
  if (!health || (await reloadIfOutdated(health))) return;
  const { backendToken: savedToken = "", backendUrl: savedUrl = "" } = await chrome.storage.local.get(["backendToken", "backendUrl"]);
  // Someone using a remote backend (with or without a token) is not offered the local app.
  $("connect-offer").hidden = Boolean(savedToken) || Boolean(savedUrl && savedUrl !== localAppUrl());
}

document.querySelectorAll("[data-connect]").forEach((button) => {
  button.onclick = () => runConnect(button);
});

document.querySelector(".panel-copyright").append(` · v${chrome.runtime.getManifest().version}`);

startConnection();
