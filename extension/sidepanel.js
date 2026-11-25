const { WILDCARD_PREFIX, isWildcardEntry, entryCovers } = OdradekFill;

const $ = (id) => document.getElementById(id);
const DEFAULT_BACKEND_URL = "http://127.0.0.1:8765"; // the Odradek desktop app
const status = (msg) => {
  const target = !$("home-view").hidden
    ? $("home-status")
    : !$("fields-view").hidden
      ? $("fields-status")
      : !$("whitelist-view").hidden
        ? $("whitelist-status")
        : !$("settings-view").hidden
          ? $("settings-status")
          : $("assistant-status");
  target.textContent = msg;
};
let fieldDefinitions = [];
let whitelistEntries = [];
// Per-site phone notation, keyed by site setting entry: "international" (+CC…) or "national"; a missing entry means "auto".
let phoneFormats = {};
const PHONE_FORMATS = [
  ["auto", "Phone: auto"],
  ["international", "Phone: with country code"],
  ["national", "Phone: without country code"],
];
let viewTransitioning = false;
let assistantHistory = [];
let pickerTabId = null;
let panelHealthPromise = null;
let backendUrl = DEFAULT_BACKEND_URL;
let backendToken = "";
let resumeLanguage = "auto";
const HEALTH_TIMEOUT_MS = 12000;
const HEALTH_WAIT_MS = 120000;
const ASSISTANT_REQUEST_TIMEOUT_MS = 300000;
const STEP_LABELS = [
  "Check panel availability",
  "Check server and database health",
  "Request profile and form rules",
  "Merge and save blank fields",
];
let activeStep = -1;

const ASSISTANT_STEP_LABELS = [
  "Check panel availability",
  "Check server and database health",
  "Make request",
  "Receive response",
];

function setStep(index, state, detail = "", listId = "process-steps", labels = STEP_LABELS, preserveOther = false) {
  const list = $(listId);
  list.hidden = false;
  activeStep = index;
  if (!preserveOther) {
    [...list.children].forEach((item, itemIndex) => {
      item.className = itemIndex < index ? "done" : "";
      item.textContent = labels[itemIndex];
    });
  }
  const current = list.children[index];
  current.className = state;
  current.textContent = detail ? `${labels[index]} — ${detail}` : labels[index];
}

function resetSteps(listId = "process-steps", labels = STEP_LABELS) {
  const list = $(listId);
  list.hidden = false;
  activeStep = -1;
  [...list.children].forEach((item, index) => {
    item.className = "";
    item.textContent = labels[index];
  });
}

async function pingPanel(listId = "process-steps", labels = STEP_LABELS, updateStatus = true) {
  await ensureBackendAccess();
  const deadline = Date.now() + HEALTH_WAIT_MS;
  let attempt = 0;
  setStep(0, "done", "health check started", listId, labels, true);
  while (Date.now() < deadline) {
    attempt++;
    setStep(1, "active", attempt === 1 ? "checking now" : `waiting for Render to wake; retry ${attempt}`, listId, labels, true);
    if (updateStatus) status(attempt === 1 ? "Checking the panel and database…" : `Waiting for the panel to wake up (attempt ${attempt})…`);
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), HEALTH_TIMEOUT_MS);
    try {
      const response = await fetch(backendEndpoint("/health"), {
        cache: "no-store",
        credentials: "include",
        signal: controller.signal,
      });
      if (response.ok) {
        const health = await response.json();
        // `/health` is a liveness probe: the Odradek panel and the local app answer {ok, service} without touching the database,
        // and only the deep `/health/db` check adds `database`. Keep waiting only when a backend reports the database as not ready.
        if (health.ok === true && (health.database === undefined || health.database === "ok")) {
          setStep(1, "done", "server is ready", listId, labels, true);
          return;
        }
      }
    } catch (_) {
      // Render may need a few requests to wake a sleeping service.
    } finally {
      clearTimeout(timeout);
    }
    if (Date.now() < deadline) await new Promise((resolve) => setTimeout(resolve, 4000));
  }
  throw new Error("The panel did not become ready within two minutes.");
}

function ensurePanelReady(listId = "process-steps", labels = STEP_LABELS, updateStatus = true) {
  if (!panelHealthPromise) {
    panelHealthPromise = pingPanel(listId, labels, updateStatus).finally(() => {
      panelHealthPromise = null;
    });
  }
  return panelHealthPromise;
}

async function ensureSiteAccess(origin) {
  const entry = entryForOrigin(origin) || origin;
  const origins = [permissionPatternForEntry(entry)];
  if (await chrome.permissions.contains({ origins })) return;
  let granted = false;
  try {
    granted = await chrome.permissions.request({ origins });
  } catch (_) {
    throw new Error(`Chrome could not request access to ${origin}. Allow site access in the extension settings and try again.`);
  }
  if (!granted) throw new Error(`Allow Odradek Autofill access to ${origin} in Chrome to use this page.`);
}

async function load() {
  const { profile = {}, fields = [], whitelist = [], backendUrl: savedBackendUrl = DEFAULT_BACKEND_URL, backendToken: savedBackendToken = "", resumeLanguage: savedResumeLanguage = "auto", phoneFormats: savedPhoneFormats = {} } = await chrome.storage.local.get(["profile", "fields", "whitelist", "backendUrl", "backendToken", "resumeLanguage", "phoneFormats"]);
  backendToken = typeof savedBackendToken === "string" ? savedBackendToken : "";
  $("backend-token").value = backendToken;
  resumeLanguage = ["auto", "pt", "en"].includes(savedResumeLanguage) ? savedResumeLanguage : "auto";
  $("resume-language").value = resumeLanguage;
  try {
    backendUrl = normalizeBackendUrl(savedBackendUrl);
  } catch (_) {
    backendUrl = DEFAULT_BACKEND_URL;
  }
  $("backend-url").value = backendUrl;
  fieldDefinitions = OdradekFill.fieldDefinitionsFrom(fields);
  whitelistEntries = Array.isArray(whitelist)
    ? [...new Set(whitelist.map(normalizeWhitelistEntry).filter(Boolean))]
    : [];
  phoneFormats = {};
  for (const [entry, format] of Object.entries(savedPhoneFormats && typeof savedPhoneFormats === "object" ? savedPhoneFormats : {})) {
    if (whitelistEntries.includes(entry) && ["international", "national"].includes(format)) phoneFormats[entry] = format;
  }
  renderFields(profile);
  renderWhitelist();
}

function normalizeOrigin(value) {
  try {
    const url = new URL(value);
    return ["http:", "https:"].includes(url.protocol) ? url.origin : "";
  } catch (_) {
    return "";
  }
}

// Site setting entries: an exact origin or a subdomain rule such as "https://*.inhire.app" (matching lives in fill-runner.js).
const MULTI_PART_SUFFIXES = new Set([
  "com.br", "org.br", "net.br", "gov.br", "edu.br", "co.uk", "org.uk", "ac.uk", "gov.uk",
  "com.au", "net.au", "org.au", "co.nz", "co.jp", "co.in", "co.za", "com.mx", "com.ar", "com.pt",
]);

// Returns the base domain when it is specific enough to wildcard, otherwise "".
function wildcardDomainOrEmpty(domain) {
  const host = String(domain || "").toLowerCase().replace(/\.$/, "");
  if (!/^[a-z0-9-]+(\.[a-z0-9-]+)+$/.test(host) || host.split(".").some((label) => !label || label.startsWith("-") || label.endsWith("-"))) return "";
  if (MULTI_PART_SUFFIXES.has(host)) return "";
  return host;
}

function normalizeWhitelistEntry(value) {
  const text = String(value || "").trim();
  if (!isWildcardEntry(text)) return normalizeOrigin(text);
  const match = /^(https?):\/\/\*\.([^/?#:@\s]+)$/i.exec(text);
  const domain = match ? wildcardDomainOrEmpty(match[2]) : "";
  return domain ? `${match[1].toLowerCase()}://${WILDCARD_PREFIX}${domain}` : "";
}

// "inhire.app", "*.inhire.app" or "https://*.inhire.app" -> "https://*.inhire.app".
function parseWildcardInput(text) {
  let input = String(text || "").trim().toLowerCase();
  if (!input) throw new Error("Enter a domain such as *.inhire.app.");
  let scheme = "https";
  const schemeMatch = /^(https?):\/\//.exec(input);
  if (schemeMatch) {
    scheme = schemeMatch[1];
    input = input.slice(schemeMatch[0].length);
  }
  input = input.replace(/\/.*$/, "");
  if (input.startsWith(WILDCARD_PREFIX)) input = input.slice(WILDCARD_PREFIX.length);
  const domain = wildcardDomainOrEmpty(input);
  if (!domain) throw new Error("That domain is too broad or invalid. Use a specific domain such as *.inhire.app.");
  return `${scheme}://${WILDCARD_PREFIX}${domain}`;
}

// https://acme.inhire.app -> https://*.inhire.app (falls back to the full host when that would be too broad).
function suggestWildcard(origin) {
  const url = new URL(origin);
  const labels = url.hostname.split(".");
  const parent = wildcardDomainOrEmpty(labels.slice(1).join("."));
  return `${url.protocol}//${WILDCARD_PREFIX}${parent || url.hostname}`;
}

// Prefers a subdomain rule so one Chrome permission grant covers every company.
function entryForOrigin(origin) {
  const matches = whitelistEntries.filter((entry) => entryCovers(entry, origin));
  return matches.find(isWildcardEntry) || matches[0] || "";
}

function permissionPatternForEntry(entry) {
  if (isWildcardEntry(entry)) {
    const [scheme, domain] = entry.split(`//${WILDCARD_PREFIX}`);
    return `${scheme}//*.${domain}/*`;
  }
  const url = new URL(entry);
  return `${url.protocol}//${url.hostname}/*`;
}

function normalizeBackendUrl(value) {
  const input = String(value || "").trim();
  if (!input) return "";
  let url;
  try {
    url = new URL(input);
  } catch (_) {
    throw new Error("Enter a valid backend URL, such as https://odradek.example.com.");
  }
  if (!["http:", "https:"].includes(url.protocol)) {
    throw new Error("The backend URL must use HTTPS.");
  }
  if (url.username || url.password || url.search || url.hash || !["", "/"].includes(url.pathname)) {
    throw new Error("Enter only the backend origin, without a path, credentials, query, or fragment.");
  }
  if (url.protocol === "http:" && !["localhost", "127.0.0.1"].includes(url.hostname.toLowerCase())) {
    throw new Error("Use HTTPS for remote backends. HTTP is allowed only on localhost or 127.0.0.1.");
  }
  return url.origin;
}

function backendPermissionPattern(value) {
  const url = new URL(value);
  return `${url.protocol}//${url.hostname}/*`;
}

function backendEndpoint(path) {
  if (!backendUrl) throw new Error("Configure a backend URL in Settings first.");
  return new URL(path, `${backendUrl}/`).toString();
}

// Calls the backend with the pairing token. Without one, falls back to the panel session cookie.
function backendFetch(path, init = {}) {
  const headers = new Headers(init.headers || {});
  if (backendToken) headers.set("Authorization", `Bearer ${backendToken}`);
  return fetch(backendEndpoint(path), { ...init, headers, credentials: backendToken ? "omit" : "include" });
}

function panelHttpError(response, what) {
  if (response.status === 401) return new Error(backendToken
    ? "The panel rejected the access token. Generate a new one in the panel and paste it in Settings."
    : "Connect to Odradek in Settings, or paste an access token there.");
  return new Error(`Panel returned ${response.status} for ${what}`);
}

async function ensureBackendAccess() {
  if (!backendUrl) throw new Error("Configure a backend URL in Settings before using import or the assistant.");
  const origins = [backendPermissionPattern(backendUrl)];
  if (!(await chrome.permissions.contains({ origins }))) {
    throw new Error("Backend access is not allowed. Open Settings and save the backend URL to grant access.");
  }
}

async function sendTabMessage(tabId, message, options = { frameId: 0 }) {
  try {
    return await chrome.tabs.sendMessage(tabId, message, options);
  } catch (firstError) {
    if (!/receiving end does not exist|could not establish connection/i.test(String(firstError?.message || firstError))) {
      throw firstError;
    }
    try {
      await chrome.scripting.executeScript({
        target: { tabId, frameIds: [options.frameId ?? 0] },
        files: ["content.js"],
      });
      return await chrome.tabs.sendMessage(tabId, message, options);
    } catch (error) {
      const detail = String(error?.message || error);
      if (/cannot access contents|extensions gallery|chrome:\/\//i.test(detail)) {
        throw new Error("Chrome denied access to this page. In the extension’s site access settings, allow this website, then reload it and try again.");
      }
      throw new Error(`Could not start the page helper (${detail}). Reload the website and try again.`);
    }
  }
}

async function getActiveTabAndOrigin() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) throw new Error("No active browser tab was found.");

  let pageUrl = tab.url || "";
  let origin = normalizeOrigin(pageUrl);
  if (!origin) {
    try {
      const page = await sendTabMessage(tab.id, { type: "GET_PAGE_URL" }, { frameId: 0 });
      pageUrl = page?.url || "";
      origin = normalizeOrigin(pageUrl);
    } catch (_) {
      // Restricted browser pages do not have the extension content script.
    }
  }
  if (!origin) {
    let scheme = "";
    try { scheme = new URL(pageUrl).protocol; } catch (_) { /* no readable URL */ }
    if (scheme && !["http:", "https:"].includes(scheme)) {
      throw new Error(`This page uses ${scheme}, which cannot be added. Open a regular website beginning with http:// or https://.`);
    }
    throw new Error("Chrome could not provide this tab's website address. Open a regular http:// or https:// page and try again.");
  }
  return { tab, origin };
}

function renderWhitelist() {
  const list = $("whitelist-sites");
  list.replaceChildren();
  for (const entry of whitelistEntries) {
    const row = document.createElement("li");
    row.className = "whitelist-entry";
    const label = document.createElement("span");
    label.className = "whitelist-origin";
    label.textContent = entry;
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "whitelist-remove";
    remove.dataset.entry = entry;
    remove.setAttribute("aria-label", `Remove settings for ${entry}`);
    remove.title = "Remove website";
    remove.textContent = "×";
    const phone = document.createElement("select");
    phone.className = "whitelist-phone";
    phone.dataset.entry = entry;
    phone.title = "How to write the phone number on this site: auto reads the page; with country code types +5531…; without it types only the national number.";
    phone.setAttribute("aria-label", `Phone notation for ${entry}`);
    for (const [value, text] of PHONE_FORMATS) {
      const option = document.createElement("option");
      option.value = value;
      option.textContent = text;
      phone.appendChild(option);
    }
    phone.value = phoneFormats[entry] || "auto";
    if (isWildcardEntry(entry)) {
      const tag = document.createElement("span");
      tag.className = "whitelist-tag";
      tag.textContent = "all subdomains";
      row.append(label, tag, phone, remove);
    } else {
      row.append(label, phone, remove);
    }
    list.appendChild(row);
  }
}

function readForm() {
  const profile = {};
  document.querySelectorAll("input[data-profile-key]").forEach((el) => {
    profile[el.dataset.profileKey] = el.value.trim();
  });
  return profile;
}

function aliasesFromText(text) {
  return [...new Set(String(text || "").split(/[\n,]/).map((alias) => alias.trim()).filter(Boolean))];
}

function commitOpenEditors() {
  document.querySelectorAll(".field-card").forEach((card) => {
    const editor = card.querySelector(".field-editor");
    if (editor.hidden) return;
    const field = fieldDefinitions.find((item) => item.key === card.dataset.fieldKey);
    if (!field) return;
    field.label = card.querySelector("[data-editor-label]").value.trim() || field.label;
    field.aliases = aliasesFromText(card.querySelector("[data-editor-aliases]").value);
  });
}

function makeLabel(text, input) {
  const label = document.createElement("label");
  label.textContent = text;
  label.appendChild(input);
  return label;
}

// Extra guidance shown under a field: what to type, and what the extension does with it.
const FIELD_GUIDE = {
  phoneCountryCode: {
    placeholder: "55",
    hint: "Digits only, such as 55. The + is optional: 55 and +55 work the same, and the extension adds the + only where the site’s code field expects it.",
  },
};

function renderFields(profile = readForm()) {
  const root = $("fields");
  root.replaceChildren();
  for (const field of fieldDefinitions) {
    const card = document.createElement("section");
    card.className = "field-card";
    card.dataset.fieldKey = field.key;

    const top = document.createElement("div");
    top.className = "field-top";
    const value = document.createElement("input");
    value.dataset.profileKey = field.key;
    value.value = profile[field.key] || "";
    value.autocomplete = "off";
    const guide = FIELD_GUIDE[field.key];
    if (guide) value.placeholder = guide.placeholder;
    const valueLabel = makeLabel(field.label, value);
    top.appendChild(valueLabel);

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "field-edit";
    edit.textContent = "Edit";
    edit.dataset.action = "edit";
    top.appendChild(edit);
    card.appendChild(top);
    if (guide) {
      const note = document.createElement("p");
      note.className = "hint";
      note.textContent = guide.hint;
      card.appendChild(note);
    }

    const editor = document.createElement("div");
    editor.className = "field-editor";
    editor.hidden = true;
    const labelInput = document.createElement("input");
    labelInput.value = field.label;
    labelInput.dataset.editorLabel = "";
    const aliasInput = document.createElement("textarea");
    aliasInput.value = (field.aliases || []).join("\n");
    aliasInput.placeholder = "One website name or ID per line";
    aliasInput.dataset.editorAliases = "";
    editor.appendChild(makeLabel("Field label", labelInput));
    editor.appendChild(makeLabel("Website field names or IDs (one per line)", aliasInput));

    const actions = document.createElement("div");
    actions.className = "field-actions";
    const done = document.createElement("button");
    done.type = "button";
    done.className = "small-button";
    done.textContent = "Done";
    done.dataset.action = "done";
    actions.appendChild(done);
    if (field.custom) {
      const remove = document.createElement("button");
      remove.type = "button";
      remove.className = "small-button";
      remove.textContent = "Remove field";
      remove.dataset.action = "remove";
      actions.appendChild(remove);
    }
    editor.appendChild(actions);
    card.appendChild(editor);
    root.appendChild(card);
  }
}

function switchView(from, to, direction) {
  if (viewTransitioning) return;
  if (matchMedia("(prefers-reduced-motion: reduce)").matches) {
    from.hidden = true;
    to.hidden = false;
    window.scrollTo(0, 0);
    return;
  }

  viewTransitioning = true;
  const exitOffset = direction === "forward" ? -10 : 10;
  const enterOffset = -exitOffset;
  const finish = () => {
    from.getAnimations().forEach((animation) => animation.cancel());
    to.getAnimations().forEach((animation) => animation.cancel());
    from.hidden = true;
    to.hidden = false;
    window.scrollTo(0, 0);
    const enter = to.animate(
      [
        { opacity: 0, transform: `translateX(${enterOffset}px)` },
        { opacity: 1, transform: "translateX(0)" },
      ],
      { duration: 190, easing: "cubic-bezier(.2,.75,.25,1)" },
    );
    enter.onfinish = () => {
      enter.cancel();
      viewTransitioning = false;
    };
  };
  const exit = from.animate(
    [
      { opacity: 1, transform: "translateX(0)" },
      { opacity: 0, transform: `translateX(${exitOffset}px)` },
    ],
    { duration: 150, easing: "ease-in", fill: "forwards" },
  );
  exit.onfinish = finish;
}

$("open-fields").onclick = () => {
  switchView($("home-view"), $("fields-view"), "forward");
};

$("back-home").onclick = () => {
  switchView($("fields-view"), $("home-view"), "backward");
};

$("open-whitelist").onclick = () => {
  switchView($("home-view"), $("whitelist-view"), "forward");
};

$("back-home-whitelist").onclick = () => {
  switchView($("whitelist-view"), $("home-view"), "backward");
};

$("open-assistant").onclick = () => {
  switchView($("home-view"), $("assistant-view"), "forward");
};

$("back-home-assistant").onclick = () => {
  switchView($("assistant-view"), $("home-view"), "backward");
};

$("open-settings").onclick = () => {
  switchView($("home-view"), $("settings-view"), "forward");
};

$("back-home-settings").onclick = () => {
  switchView($("settings-view"), $("home-view"), "backward");
};

$("save-backend").onclick = async (event) => {
  const button = event.currentTarget;
  button.disabled = true;
  try {
    resumeLanguage = $("resume-language").value;
    await chrome.storage.local.set({ resumeLanguage });
    backendToken = $("backend-token").value.trim();
    if (backendToken) await chrome.storage.local.set({ backendToken });
    else await chrome.storage.local.remove("backendToken");
    const nextBackendUrl = normalizeBackendUrl($("backend-url").value);
    if (!nextBackendUrl) {
      await chrome.storage.local.remove("backendUrl");
      backendUrl = "";
      $("backend-url").value = "";
      status("Backend URL cleared. Set one before importing or using the assistant.");
      return;
    }

    const nextPattern = backendPermissionPattern(nextBackendUrl);
    const granted = await chrome.permissions.request({ origins: [nextPattern] });
    if (!granted) throw new Error(`Chrome access was not granted for ${nextBackendUrl}.`);
    await chrome.storage.local.set({ backendUrl: nextBackendUrl });
    backendUrl = nextBackendUrl;
    $("backend-url").value = nextBackendUrl;
    status("Backend saved on this device.");
  } catch (error) {
    status(error.message || "Could not save the backend URL.");
  } finally {
    button.disabled = false;
  }
};

$("close-panel").onclick = async () => {
  const currentWindow = await chrome.windows.getCurrent();
  await chrome.sidePanel.close({ windowId: currentWindow.id });
};

$("add-current-site").onclick = async (event) => {
  const button = event.currentTarget;
  button.disabled = true;
  status("Checking the current website…");
  try {
    const { origin } = await getActiveTabAndOrigin();
    const covering = entryForOrigin(origin);
    if (covering) {
      status(covering === origin ? `${origin} is already listed.` : `${origin} is already covered by ${covering}.`);
      return;
    }
    whitelistEntries.push(origin);
    await chrome.storage.local.set({ whitelist: whitelistEntries });
    renderWhitelist();
    status(`Added ${origin}. Choose how this site is filled.`);
  } catch (error) {
    status(error.message || "Could not add this website.");
  } finally {
    button.disabled = false;
  }
};

$("add-subdomains").onclick = async () => {
  $("wildcard-editor").hidden = false;
  $("wildcard-pattern").value = "";
  $("wildcard-pattern").focus();
  try {
    const { origin } = await getActiveTabAndOrigin();
    if (!$("wildcard-editor").hidden && !$("wildcard-pattern").value) {
      $("wildcard-pattern").value = suggestWildcard(origin);
    }
  } catch (_) {
    // No usable tab: the user can type the domain manually.
  }
};

$("cancel-wildcard").onclick = () => {
  $("wildcard-editor").hidden = true;
  $("wildcard-pattern").value = "";
};

$("add-wildcard").onclick = async () => {
  try {
    const entry = parseWildcardInput($("wildcard-pattern").value);
    if (whitelistEntries.includes(entry)) {
      status(`${entry} is already listed.`);
      return;
    }
    const covered = whitelistEntries.filter((existing) => !isWildcardEntry(existing) && entryCovers(entry, existing));
    whitelistEntries = whitelistEntries.filter((existing) => !covered.includes(existing));
    whitelistEntries.push(entry);
    await chrome.storage.local.set({ whitelist: whitelistEntries });
    renderWhitelist();
    $("cancel-wildcard").click();
    status(covered.length
      ? `Added ${entry} and merged ${covered.length} existing site(s) into it.`
      : `Added ${entry}. Its settings apply to every subdomain.`);
  } catch (error) {
    status(error.message || "Could not add this rule.");
  }
};

$("whitelist-sites").addEventListener("change", async (event) => {
  const select = event.target.closest("select.whitelist-phone");
  if (!select) return;
  if (select.value === "auto") delete phoneFormats[select.dataset.entry];
  else phoneFormats[select.dataset.entry] = select.value;
  await chrome.storage.local.set({ phoneFormats });
  status(select.value === "auto"
    ? `Phone on ${select.dataset.entry}: the extension reads the page.`
    : `Phone on ${select.dataset.entry}: ${select.value === "international" ? "always with the country code (+55…)" : "never with the country code"}.`);
});

$("whitelist-sites").addEventListener("click", async (event) => {
  const button = event.target.closest("button.whitelist-remove");
  if (!button) return;
  whitelistEntries = whitelistEntries.filter((entry) => entry !== button.dataset.entry);
  delete phoneFormats[button.dataset.entry];
  await chrome.storage.local.set({ whitelist: whitelistEntries, phoneFormats });
  renderWhitelist();
  status(`Removed the settings for ${button.dataset.entry}.`);
});

$("fields").addEventListener("click", (event) => {
  const button = event.target.closest("button[data-action]");
  if (!button) return;
  const card = button.closest(".field-card");
  const field = fieldDefinitions.find((item) => item.key === card.dataset.fieldKey);
  if (button.dataset.action === "edit") {
    const editor = card.querySelector(".field-editor");
    editor.hidden = !editor.hidden;
    button.textContent = editor.hidden ? "Edit" : "Cancel";
    if (editor.hidden) renderFields();
    return;
  }
  if (button.dataset.action === "remove") {
    fieldDefinitions = fieldDefinitions.filter((item) => item.key !== field.key);
    renderFields();
    status("Field removed. Click Save to keep this change.");
    return;
  }
  if (button.dataset.action === "done") {
    field.label = card.querySelector("[data-editor-label]").value.trim() || field.label;
    field.aliases = aliasesFromText(card.querySelector("[data-editor-aliases]").value);
    renderFields();
  }
});

$("add-field").onclick = () => {
  $("create-editor").hidden = false;
  $("new-label").focus();
};

$("cancel-create").onclick = () => {
  $("create-editor").hidden = true;
  $("new-label").value = "";
  $("new-value").value = "";
  $("new-aliases").value = "";
};

$("create-field").onclick = () => {
  const label = $("new-label").value.trim();
  if (!label) {
    status("Add a field label first.");
    $("new-label").focus();
    return;
  }
  const key = `custom_${crypto.randomUUID().replaceAll("-", "")}`;
  const profile = readForm();
  profile[key] = $("new-value").value.trim();
  fieldDefinitions.push({ key, label, aliases: aliasesFromText($("new-aliases").value), custom: true });
  renderFields(profile);
  $("cancel-create").click();
  status("Field added. Click Save to keep it.");
};

$("save").onclick = async () => {
  commitOpenEditors();
  const profile = readForm();
  renderFields(profile);
  await chrome.storage.local.set({ profile, fields: fieldDefinitions });
  status("Saved.");
};

$("import").onclick = async () => {
  const button = $("import");
  button.disabled = true;
  commitOpenEditors();
  renderFields();
  resetSteps();
  try {
    setStep(0, "active", "starting health check");
    await pingPanel();

    setStep(2, "active", "requesting your saved details");
    status("Panel is awake. Requesting your profile…");
    const [profileResponse, rulesResponse] = await Promise.all([
      backendFetch("/api/autofill-profile", { cache: "no-store" }),
      backendFetch("/api/autofill-rules", { cache: "no-store" }),
    ]);
    if (!profileResponse.ok) throw panelHttpError(profileResponse, "profile data");
    if (!rulesResponse.ok) throw panelHttpError(rulesResponse, "form rules");
    const [payload, rulePayload] = await Promise.all([profileResponse.json(), rulesResponse.json()]);
    setStep(2, "done", "profile data and form rules received");
    setStep(3, "active", "merging without replacing your values");
    const panelProfile = payload.profile || {};
    const { profile: savedProfile = {} } = await chrome.storage.local.get("profile");
    const formProfile = readForm();
    const merged = { ...savedProfile, ...formProfile };
    let imported = 0;
    for (const { key } of fieldDefinitions) {
      const panelValue = String(panelProfile[key] || "").trim();
      if (!String(merged[key] || "").trim() && panelValue) {
        merged[key] = panelValue;
        imported++;
      }
    }
    const aliasesByKey = new Map((rulePayload.fields || []).map((field) => [
      field.key,
      Array.isArray(field.aliases) ? field.aliases : [],
    ]));
    const mergedFields = fieldDefinitions.map((field) => ({
      ...field,
      aliases: [...new Set([...(field.aliases || []), ...(aliasesByKey.get(field.key) || [])])],
    }));
    fieldDefinitions = mergedFields;
    await chrome.storage.local.set({ profile: merged, fields: fieldDefinitions });
    renderFields(merged);
    setStep(3, "done", `${imported} blank profile field(s) imported; form aliases updated`);
    status(imported
      ? `Imported ${imported} blank field(s) and refreshed form aliases; existing values were kept.`
      : "Profile values kept; form aliases were refreshed from Odradek.");
  } catch (e) {
    if (activeStep >= 0) setStep(activeStep, "error", e.message || "request failed");
    status(e.message || "Could not complete the panel request.");
  } finally {
    button.disabled = false;
  }
};

async function fillCurrentPage() {
  const button = $("home-fill");
  button.disabled = true;
  status("Filling the current page…");
  try {
    commitOpenEditors();
    const profile = readForm();
    renderFields(profile);
    await chrome.storage.local.set({ profile, fields: fieldDefinitions });
    const { tab, origin } = await getActiveTabAndOrigin();
    await ensureSiteAccess(origin);
    const { filled, skipped, frameNote, resumeNote } = await OdradekFill.fillTab(tab.id, {
      ready: () => {
        status("Fields filled. Checking for a resume upload…");
        return ensurePanelReady("process-steps", STEP_LABELS, false);
      },
    });
    if (!filled.length && !skipped.length && !resumeNote) {
      status("No matching fields were found. Check the page labels or add aliases in Fields.");
    } else {
      status(`${filled.length} field(s) filled; ${skipped.length} left unchanged.${frameNote}${resumeNote} Review the page and submit it yourself.`);
    }
  } catch (e) {
    const message = String(e?.message || "");
    if (/cannot access|extensions gallery|chrome:\/\//i.test(message)) {
      status("Chrome does not allow filling this page. Open a regular website and try again.");
    } else {
      status(`Could not fill this page: ${message || "unknown error"}`);
    }
  } finally {
    button.disabled = false;
  }
}

$("home-fill").onclick = fillCurrentPage;

// ---- LinkedIn: send the jobs listed on the page the user has open (e.g. Jobs → Recommended) to Odradek.
// Three steps, all from the user's own browser: (1) read the job ids on the page, (2) ask Odradek which ones it does not have yet,
// (3) for those only, read each job's public page (the one LinkedIn serves to anyone, fetched without cookies, at a human pace) and send the details.
// Nothing here uses LinkedIn credentials, and Odradek itself never contacts LinkedIn for this import.
const LINKEDIN_JOBS_URL = /^https:\/\/([\w-]+\.)?linkedin\.com\/jobs\/(collections|search|recommended)/i;
const LINKEDIN_IMPORT_TIMEOUT_MS = 30000;

function isLinkedInJobsUrl(url) {
  return LINKEDIN_JOBS_URL.test(url || "");
}

async function refreshLinkedInButton() {
  let visible = false;
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    visible = isLinkedInJobsUrl(tab?.url);
  } catch (_) { /* no readable tab: keep the button hidden */ }
  $("home-linkedin").hidden = !visible;
  $("home-linkedin-debug").hidden = !visible;
}

// Runs inside the LinkedIn tab (injected, so it must not use anything from this file). Scrolls the job list so lazy cards render and
// collects job ids. It never clicks or navigates. Ids come from the page structure first; the raw-HTML scan is only a fallback because it can
// pick up ids from parts of the page that are not listed jobs.
async function collectLinkedInJobsInPage() {
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const ids = new Map(); // id -> how it was found
  const valid = (id) => /^\d{5,20}$/.test(id || "");
  const add = (id, how) => { if (valid(id) && !ids.has(id)) ids.set(id, how); };
  const fromHref = (href) => (String(href || "").match(/\/jobs\/view\/(?:[^/?#]*-)?(\d{5,20})/) || [])[1] || "";
  const fromCardKey = (key) => (String(key || "").match(/job-card-component-ref-(\d{5,20})/) || [])[1] || "";
  const fromParam = (href) => { try { return new URL(href, location.href).searchParams.get("currentJobId") || ""; } catch (_) { return ""; } };
  const fromUrn = (text) => (String(text || "").match(/jobPosting(?::|%3A)(\d{5,20})/i) || [])[1] || "";
  const harvest = () => {
    for (const el of document.querySelectorAll("[data-occludable-job-id], [data-job-id]")) add(el.getAttribute("data-occludable-job-id") || el.getAttribute("data-job-id"), "attribute");
    for (const el of document.querySelectorAll("[data-entity-urn], [data-urn]")) add(fromUrn(el.getAttribute("data-entity-urn") || el.getAttribute("data-urn")), "urn");
    // Current layout: every card carries componentkey="job-card-component-ref-<id>" (the list is a lazy column that only renders what is near the viewport).
    for (const el of document.querySelectorAll("[componentkey*='job-card-component-ref-']")) add(fromCardKey(el.getAttribute("componentkey")), "card");
    // Earlier layout: the cards are links like /jobs/search-results/?currentJobId=<id>, and the only /jobs/view/ link is the open job's detail pane.
    for (const a of document.querySelectorAll("a[href*='currentJobId=']")) add(fromParam(a.getAttribute("href")), "currentJobId");
    for (const a of document.querySelectorAll("a[href*='/jobs/view/']")) add(fromHref(a.getAttribute("href")), "link");
  };
  const scrollable = (el) => el.scrollHeight > el.clientHeight + 40 && /(auto|scroll)/.test(getComputedStyle(el).overflowY);
  const findScroller = () => {
    // Start from the list's own cards (attributes, then currentJobId links); the /jobs/view/ link can belong to the detail pane instead.
    const anchor = document.querySelector("[componentkey*='job-card-component-ref-'], [data-occludable-job-id], [data-job-id]") || document.querySelector("a[href*='currentJobId=']") || document.querySelector("a[href*='/jobs/view/']");
    for (let el = anchor?.parentElement; el; el = el.parentElement) if (scrollable(el)) return el;
    let best = null;
    for (const el of document.querySelectorAll("div, ul, section, main")) if (scrollable(el) && (!best || el.scrollHeight > best.scrollHeight)) best = el;
    return best;
  };
  harvest();
  const scroller = findScroller();
  let stable = 0;
  let steps = 0;
  for (; steps < 40 && stable < 3; steps += 1) {
    const before = ids.size;
    if (scroller) scroller.scrollBy(0, Math.max(300, scroller.clientHeight * 0.8));
    else window.scrollBy(0, Math.max(300, window.innerHeight * 0.8));
    await sleep(700);
    harvest();
    stable = ids.size === before ? stable + 1 : 0;
  }
  (scroller || window).scrollTo(0, 0);
  let fallback = false;
  if (ids.size < 3) {
    fallback = true;
    const html = document.documentElement.innerHTML;
    for (const m of html.matchAll(/jobPosting(?::|%3A)(\d{5,20})/gi)) add(m[1], "html");
    for (const m of html.matchAll(/\/jobs\/view\/(?:[^/?#"'\\]*-)?(\d{5,20})/g)) add(m[1], "html");
    add(new URL(location.href).searchParams.get("currentJobId"), "url");
  }
  const how = {};
  for (const kind of ids.values()) how[kind] = (how[kind] || 0) + 1;
  return { ids: [...ids.keys()], how, scroller: scroller ? scroller.tagName.toLowerCase() : "window", steps, fallback };
}

async function postLinkedInImport(body) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), LINKEDIN_IMPORT_TIMEOUT_MS);
  try {
    const response = await backendFetch("/api/linkedin-import", {
      method: "POST",
      cache: "no-store",
      signal: controller.signal,
      headers: { "Content-Type": "application/json", "Accept": "application/json", "X-Requested-With": "fetch" },
      body: JSON.stringify(body),
    });
    const payload = await response.json().catch(() => ({}));
    if (response.status === 401) throw panelHttpError(response, "the LinkedIn import");
    if (!response.ok || !payload.ok) throw Object.assign(new Error(payload.error || `Panel returned ${response.status}`), { httpStatus: response.status });
    return payload;
  } finally {
    clearTimeout(timeout);
  }
}

async function importLinkedInJobs() {
  const button = $("home-linkedin");
  button.disabled = true;
  status("");
  try {
    await ensureBackendAccess();
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id || !isLinkedInJobsUrl(tab.url)) throw new Error("Open a LinkedIn jobs page first (for example Jobs → Recommended).");
    await ensureSiteAccess(new URL(tab.url).origin);

    status("Reading the jobs listed on this page…");
    const [collected] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: collectLinkedInJobsInPage });
    const found = collected?.result || { ids: [], how: {} };
    if (!found.ids.length) throw new Error("No jobs were found on this page. Wait for the list to load and try again.");
    const detail = Object.entries(found.how).map(([kind, count]) => `${count} by ${kind}`).join(", ");

    // Odradek reads each job's public page itself, slowly and in the background (it backs off when LinkedIn asks for calm),
    // saves the jobs as they arrive and triages them. Nothing to wait for here.
    status(`Found ${found.ids.length} jobs (${detail}). Sending them to Odradek…`);
    let queued;
    try {
      queued = await postLinkedInImport({ enqueue: found.ids });
    } catch (e) {
      if (e?.httpStatus === 400) throw new Error("This Odradek is an older version without the LinkedIn queue. Update and restart Odradek, then try again.");
      throw e;
    }
    const parts = [`${queued.queued} new job(s) queued in Odradek`];
    if (queued.known) parts.push(`${queued.known} already known`);
    if (queued.alreadyQueued) parts.push(`${queued.alreadyQueued} already waiting`);
    const rest = Number(queued.pending) > 0 ? ` Odradek is reading ${queued.pending} job(s) in the background, a few seconds apart; they appear and get triaged as it goes, and you can close this panel.` : "";
    status(`Found ${found.ids.length} jobs (${detail}): ${parts.join(", ")}.${rest}`);
  } catch (e) {
    const message = String(e?.message || "");
    status(e?.name === "AbortError" ? "Odradek took too long to answer. Try again." : `Could not send the jobs: ${message || "unknown error"}`);
  } finally {
    button.disabled = false;
  }
}

// Diagnostics for the LinkedIn import: structure and request results only. No job titles, companies, descriptions or profile content are read;
// the report is copied to the clipboard for the user to paste, nothing is sent anywhere.
async function diagnoseLinkedInInPage() {
  const report = { url: location.pathname + (location.search ? "?" + [...new URL(location.href).searchParams.keys()].join("&") : ""), when: new Date().toISOString() };
  const idFromHref = (href) => (String(href || "").match(/\/jobs\/view\/(?:[^/?#]*-)?(\d{5,20})/) || [])[1] || "";
  const count = (selector) => document.querySelectorAll(selector).length;
  report.counts = {
    "[data-occludable-job-id]": count("[data-occludable-job-id]"),
    "[data-job-id]": count("[data-job-id]"),
    "[data-entity-urn]": count("[data-entity-urn]"),
    "a[href*='/jobs/view/']": count("a[href*='/jobs/view/']"),
    "a[href*='currentJobId']": count("a[href*='currentJobId']"),
    "li": count("li"),
    "[role=listitem]": count("[role=listitem]"),
    "[componentkey]": count("[componentkey]"),
    "[data-view-name]": count("[data-view-name]"),
    "[data-testid]": count("[data-testid]"),
  };
  const html = document.documentElement.innerHTML;
  report.htmlMatches = {
    jobPostingUrn: (html.match(/jobPosting(?::|%3A)\d{5,20}/gi) || []).length,
    jobsViewPaths: (html.match(/\/jobs\/view\/(?:[^/?#"'\\]*-)?\d{5,20}/g) || []).length,
    currentJobId: (html.match(/currentJobId=\d{5,20}/g) || []).length,
  };
  const names = new Map();
  for (const el of document.querySelectorAll("*")) for (const attr of el.attributes) {
    if (/^data-(?!reactid)/.test(attr.name) && /job|card|entity|urn|occlud|view|component/i.test(attr.name)) names.set(attr.name, (names.get(attr.name) || 0) + 1);
  }
  report.jobRelatedDataAttributes = Object.fromEntries([...names.entries()].sort((a, b) => b[1] - a[1]).slice(0, 25));
  const anchors = [...document.querySelectorAll("a[href*='/jobs/view/']")];
  report.sampleLinks = anchors.slice(0, 4).map((a) => ({
    path: a.getAttribute("href").split("?")[0].replace(/\d{5,20}/, "<id>").slice(0, 80),
    inside: a.closest("li") ? "li" : a.parentElement?.tagName.toLowerCase(),
    ancestors: (() => { const chain = []; for (let el = a.parentElement, n = 0; el && n < 6; el = el.parentElement, n += 1) chain.push(`${el.tagName.toLowerCase()}${el.className && typeof el.className === "string" ? "." + el.className.trim().split(/\s+/).slice(0, 3).join(".") : ""}`); return chain; })(),
  }));
  const scrollables = [];
  for (const el of document.querySelectorAll("div, ul, section, main, aside")) {
    if (el.scrollHeight > el.clientHeight + 40 && /(auto|scroll)/.test(getComputedStyle(el).overflowY))
      scrollables.push({ tag: el.tagName.toLowerCase(), cls: String(el.className || "").trim().split(/\s+/).slice(0, 3).join("."), client: el.clientHeight, scroll: el.scrollHeight, links: el.querySelectorAll("a[href*='/jobs/view/']").length });
  }
  report.scrollContainers = scrollables.slice(0, 8);
  // Where could a card's id live? Attributes whose value holds a long number, and the bare structure (tags and attributes, no text) of the first list items.
  const digitAttrs = new Map();
  for (const el of document.querySelectorAll("*")) for (const attr of el.attributes) {
    if (/\d{8,}/.test(attr.value)) {
      const entry = digitAttrs.get(attr.name) || { count: 0, samples: new Set() };
      entry.count += 1;
      if (entry.samples.size < 3) entry.samples.add(attr.value.slice(0, 90));
      digitAttrs.set(attr.name, entry);
    }
  }
  report.attributesWithLongNumbers = [...digitAttrs.entries()].sort((a, b) => b[1].count - a[1].count).slice(0, 12).map(([name, e]) => ({ name, count: e.count, samples: [...e.samples] }));
  const skeleton = (el, depth) => ({
    tag: el.tagName.toLowerCase(),
    attrs: Object.fromEntries([...el.attributes].filter((a) => a.name !== "style").slice(0, 8).map((a) => [a.name, a.value.slice(0, 60)])),
    children: depth > 0 ? [...el.children].slice(0, 4).map((c) => skeleton(c, depth - 1)) : [...el.children].length,
  });
  report.listItems = [];
  for (const container of scrollables.length ? [...document.querySelectorAll("div, ul, section, main, aside")].filter((el) => el.scrollHeight > el.clientHeight + 40 && /(auto|scroll)/.test(getComputedStyle(el).overflowY)).slice(0, 3) : []) {
    let best = container;
    for (const el of container.querySelectorAll("*")) if (el.children.length > best.children.length) best = el;
    report.listItems.push({ container: container.tagName.toLowerCase(), repeatedChildren: best.children.length, parent: skeleton(best, 0), firstTwo: [...best.children].slice(0, 2).map((c) => skeleton(c, 4)) });
  }
  report.componentKeySamples = [...new Set([...document.querySelectorAll("[componentkey]")].map((el) => el.getAttribute("componentkey").slice(0, 70)))].slice(0, 12);
  report.testIdSamples = [...new Set([...document.querySelectorAll("[data-testid]")].map((el) => el.getAttribute("data-testid").slice(0, 60)))].slice(0, 12);
  report.windowScroll = { client: document.documentElement.clientHeight, scroll: document.documentElement.scrollHeight };

  // The request that gets 429: one job id from the page, tried a few ways, spaced out. Only status and a few headers are kept.
  const testId = [...document.querySelectorAll("[data-occludable-job-id], [data-job-id]")].map((el) => el.getAttribute("data-occludable-job-id") || el.getAttribute("data-job-id"))
    .concat(anchors.map((a) => idFromHref(a.getAttribute("href")))).concat([new URL(location.href).searchParams.get("currentJobId")]).find((id) => /^\d{5,20}$/.test(id || ""));
  report.testId = testId || null;
  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const attempt = async (label, init) => {
    try {
      const response = await fetch(`/jobs-guest/jobs/api/jobPosting/${testId}`, { cache: "no-store", ...init });
      const text = await response.text();
      return { label, status: response.status, type: response.headers.get("content-type"), retryAfter: response.headers.get("retry-after"), bytes: text.length, hasTitle: text.includes("top-card-layout__title"), head: response.ok ? "" : text.replace(/\s+/g, " ").slice(0, 120) };
    } catch (e) {
      return { label, error: String(e?.message || e) };
    }
  };
  report.requests = [];
  if (testId) {
    const variants = [
      ["omit cookies (what the import uses)", { credentials: "omit" }],
      ["omit cookies, no referrer", { credentials: "omit", referrerPolicy: "no-referrer" }],
      ["same-origin cookies", { credentials: "same-origin" }],
    ];
    for (const [label, init] of variants) {
      report.requests.push(await attempt(label, init));
      await sleep(2500);
    }
  }
  return report;
}

async function copyLinkedInDiagnostics() {
  const button = $("home-linkedin-debug");
  button.disabled = true;
  try {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab?.id || !isLinkedInJobsUrl(tab.url)) throw new Error("Open a LinkedIn jobs page first.");
    await ensureSiteAccess(new URL(tab.url).origin);
    status("Collecting diagnostics (about 10 seconds)…");
    const [result] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: diagnoseLinkedInInPage });
    const text = JSON.stringify(result?.result || {}, null, 2);
    await navigator.clipboard.writeText(text);
    status("Diagnostics copied to the clipboard. Paste them in the chat.");
  } catch (e) {
    status(`Could not collect diagnostics: ${String(e?.message || "unknown error")}`);
  } finally {
    button.disabled = false;
  }
}

$("home-linkedin").onclick = importLinkedInJobs;
$("home-linkedin-debug").onclick = copyLinkedInDiagnostics;
chrome.tabs.onActivated.addListener(refreshLinkedInButton);
chrome.tabs.onUpdated.addListener((_tabId, change) => { if (change.url || change.status === "complete") refreshLinkedInButton(); });
refreshLinkedInButton();

function appendAssistantMessage(role, text, isError = false) {
  $("assistant-empty").hidden = true;
  const bubble = document.createElement("div");
  bubble.className = `assistant-message ${role}${isError ? " error" : ""}`;
  bubble.textContent = text;
  $("assistant-messages").appendChild(bubble);
  $("assistant-messages").scrollTop = $("assistant-messages").scrollHeight;
}

async function sendAssistantQuestion(question) {
  const message = String(question || "").trim();
  if (!message) {
    status("Enter or select a question first.");
    return;
  }
  const sendButton = $("assistant-send");
  const input = $("assistant-input");
  sendButton.disabled = true;
  input.disabled = true;
  appendAssistantMessage("user", message);
  resetSteps("assistant-steps", ASSISTANT_STEP_LABELS);
  try {
    setStep(0, "active", "starting health check", "assistant-steps", ASSISTANT_STEP_LABELS);
    const panelReady = ensurePanelReady("assistant-steps", ASSISTANT_STEP_LABELS);
    setStep(0, "done", "health check in progress", "assistant-steps", ASSISTANT_STEP_LABELS, true);
    await panelReady;
    setStep(2, "active", "sending your question to the panel assistant", "assistant-steps", ASSISTANT_STEP_LABELS);
    const body = new URLSearchParams();
    body.set("message", message);
    body.set("history", JSON.stringify(assistantHistory));
    const controller = new AbortController();
    const startedAt = Date.now();
    const timeout = setTimeout(() => controller.abort(), ASSISTANT_REQUEST_TIMEOUT_MS);
    const progress = setInterval(() => {
      const elapsedSeconds = Math.floor((Date.now() - startedAt) / 1000);
      const elapsed = elapsedSeconds < 60
        ? `${elapsedSeconds}s`
        : `${Math.floor(elapsedSeconds / 60)}m ${elapsedSeconds % 60}s`;
      setStep(3, "active", `waiting for the panel assistant (${elapsed})`, "assistant-steps", ASSISTANT_STEP_LABELS, true);
      status(`The assistant is working (${elapsed}).`);
    }, 5000);
    let response;
    let payload;
    try {
      const responsePromise = backendFetch("/api/assistant", {
        method: "POST",
        cache: "no-store",
        signal: controller.signal,
        headers: {
          "Content-Type": "application/x-www-form-urlencoded;charset=UTF-8",
          "Accept": "application/json",
          "X-Requested-With": "fetch",
        },
        body: body.toString(),
      });
      setStep(2, "done", "question sent", "assistant-steps", ASSISTANT_STEP_LABELS, true);
      setStep(3, "active", "waiting for the panel assistant", "assistant-steps", ASSISTANT_STEP_LABELS, true);
      status("Question sent. Waiting for the assistant response…");
      response = await responsePromise;
      payload = await response.json().catch(() => ({}));
    } finally {
      clearTimeout(timeout);
      clearInterval(progress);
    }
    if (!response.ok || !payload.ok || !payload.answer) {
      throw response.status === 401 ? panelHttpError(response, "the assistant") : new Error(payload.error || payload.notice || `Panel returned ${response.status}`);
    }
    assistantHistory.push({ role: "user", content: message }, { role: "assistant", content: payload.answer });
    setStep(2, "done", "question sent", "assistant-steps", ASSISTANT_STEP_LABELS);
    setStep(3, "done", "answer received", "assistant-steps", ASSISTANT_STEP_LABELS);
    appendAssistantMessage("assistant", payload.answer);
    status("Answer received.");
    input.value = "";
  } catch (error) {
    const message = error.name === "AbortError"
      ? "The assistant did not respond within five minutes. Your question is still in the chat; try sending it again."
      : error.message || "Could not reach the panel assistant.";
    if (activeStep >= 0) setStep(activeStep, "error", message, "assistant-steps", ASSISTANT_STEP_LABELS);
    appendAssistantMessage("assistant", message, true);
    status(message);
  } finally {
    sendButton.disabled = false;
    input.disabled = false;
    input.focus();
  }
}

$("assistant-compose").addEventListener("submit", (event) => {
  event.preventDefault();
  sendAssistantQuestion($("assistant-input").value);
});

$("select-question").onclick = async (event) => {
  const button = event.currentTarget;
  button.disabled = true;
  resetSteps("assistant-steps", ASSISTANT_STEP_LABELS);
  setStep(0, "active", "starting background health check", "assistant-steps", ASSISTANT_STEP_LABELS, true);
  void ensurePanelReady("assistant-steps", ASSISTANT_STEP_LABELS, false).catch((error) => {
    setStep(1, "error", error.message || "panel check failed", "assistant-steps", ASSISTANT_STEP_LABELS, true);
  });
  try {
    const { tab, origin } = await getActiveTabAndOrigin();
    await ensureSiteAccess(origin);
    pickerTabId = tab.id;
    setStep(2, "active", "starting page selection", "assistant-steps", ASSISTANT_STEP_LABELS, true);
    await sendTabMessage(tab.id, { type: "START_ELEMENT_PICKER" }, { frameId: 0 });
    setStep(2, "done", "page selection started", "assistant-steps", ASSISTANT_STEP_LABELS, true);
    setStep(3, "done", "page helper responded", "assistant-steps", ASSISTANT_STEP_LABELS, true);
    status("Hover over the question and left-click to add its HTML to the chat. Right-click or press Escape to cancel.");
  } catch (error) {
    pickerTabId = null;
    button.disabled = false;
    setStep(2, "error", error.message || "request failed", "assistant-steps", ASSISTANT_STEP_LABELS, true);
    status(error.message || "Could not start element selection. Reload the webpage and try again.");
  }
};

chrome.runtime.onMessage.addListener((message, sender) => {
  if (!message?.type?.startsWith("ELEMENT_PICKER_") && message?.type !== "ELEMENT_SELECTED") return;
  if (pickerTabId == null || sender.tab?.id !== pickerTabId) return;
  const html = String(message.html || "");
  if (message.type === "ELEMENT_PICKER_HOVER") {
    status(`Hovering <${message.tagName || "element"}>. Left-click to add it to chat; right-click or Escape to cancel.`);
  } else if (message.type === "ELEMENT_SELECTED") {
    const question = html.slice(0, 4000);
    $("assistant-input").value = question;
    $("assistant-input").focus();
    pickerTabId = null;
    $("select-question").disabled = false;
    status(html.length > 4000
      ? "Selected HTML was added to the chat and shortened to 4,000 characters. Review it, then send when ready."
      : "Selected HTML was added to the chat. Review it, then send when ready.");
  } else if (message.type === "ELEMENT_PICKER_CANCELLED") {
    pickerTabId = null;
    $("select-question").disabled = false;
    status("Element selection cancelled.");
  }
});

// (?) help marks: a single floating box. Absolute pseudo-elements would be clipped by the narrow panel.
function wireHelpMarks() {
  const pop = document.createElement("div");
  pop.className = "help-pop";
  pop.id = "help-pop";
  pop.setAttribute("role", "tooltip");
  pop.hidden = true;
  document.body.append(pop);
  let owner = null;
  let pinned = false; // opened by a click: stays until another click, Escape or scrolling

  const hide = () => {
    owner?.removeAttribute("aria-describedby");
    owner = null;
    pinned = false;
    pop.hidden = true;
  };
  const show = (mark) => {
    if (owner !== mark) hide();
    owner = mark;
    mark.setAttribute("aria-describedby", pop.id);
    pop.textContent = mark.dataset.help;
    pop.hidden = false;
    const anchor = mark.getBoundingClientRect();
    const left = Math.max(12, Math.min(anchor.right - pop.offsetWidth, window.innerWidth - pop.offsetWidth - 12));
    const below = anchor.bottom + 6;
    const top = below + pop.offsetHeight > window.innerHeight - 8 ? Math.max(8, anchor.top - pop.offsetHeight - 6) : below;
    pop.style.left = `${left}px`;
    pop.style.top = `${top}px`;
  };

  document.querySelectorAll(".help").forEach((mark) => {
    mark.addEventListener("mouseenter", () => { if (!pinned) show(mark); });
    mark.addEventListener("mouseleave", () => { if (!pinned) hide(); });
    mark.addEventListener("focus", () => { if (!pinned) show(mark); });
    mark.addEventListener("blur", hide);
    mark.addEventListener("click", () => {
      if (pinned && owner === mark) { hide(); return; }
      show(mark);
      pinned = true;
    });
  });
  document.addEventListener("click", (event) => { if (!event.target.closest(".help")) hide(); });
  document.addEventListener("keydown", (event) => { if (event.key === "Escape") hide(); });
  window.addEventListener("scroll", hide, { passive: true });
}

wireHelpMarks();

const loaded = load();
