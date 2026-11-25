// Fill flow shared by the side panel and the service worker (keyboard shortcut and context menu).
globalThis.OdradekFill = (() => {
  const DEFAULT_FIELDS = [
    { key: "firstName", label: "First name", aliases: ["first name", "first_name", "first-name", "firstname", "given name", "given_name", "given-name", "fname", "forename"] },
    { key: "lastName", label: "Last name", aliases: ["last name", "last_name", "last-name", "lastname", "family name", "family_name", "family-name", "surname", "lname"] },
    { key: "fullName", label: "Full name", aliases: ["full name", "full_name", "full-name", "fullname", "candidate name", "applicant name", "your name", "name"] },
    { key: "email", label: "Email", aliases: ["email", "e-mail", "email address", "email_address", "user email"] },
    { key: "phone", label: "Phone", aliases: ["phone", "phone number", "telephone", "mobile", "mobile phone", "cell phone", "tel", "celular", "telefone"] },
    { key: "phoneCountryCode", label: "Phone country code", aliases: ["phone country code", "country code", "calling code", "dial code", "country calling code", "código do país", "codigo do pais", "ddi", "prefixo"] },
    { key: "city", label: "City", aliases: ["city", "address-level2", "address level 2", "town"] },
    { key: "state", label: "State", aliases: ["state", "province", "region", "address-level1", "address level 1"] },
    { key: "country", label: "Country", aliases: ["country", "country name", "país", "pais"] },
    { key: "zip", label: "Postal code", aliases: ["zip", "zip code", "postal", "postal code", "postcode", "cep"] },
    { key: "streetAddress", label: "Street address", aliases: ["street address", "address line", "address line 1", "street", "logradouro", "endereço", "endereco"] },
    { key: "currentCompany", label: "Current company", aliases: ["current company", "current employer", "employer name", "empresa atual"] },
    { key: "cpf", label: "CPF / tax ID", aliases: ["cpf", "tax id", "tax identification", "national id"] },
    { key: "salaryExpectation", label: "Salary expectation", aliases: ["salary expectation", "expected salary", "compensation", "pretensão salarial", "pretensao salarial"] },
    { key: "contractType", label: "Preferred contract type", aliases: ["contract type", "employment type", "type of contract", "tipo de contratação", "tipo de contratacao"] },
    { key: "linkedin", label: "LinkedIn URL", aliases: ["linkedin", "linkedin url", "linkedin_url", "linkedin profile"] },
    { key: "github", label: "GitHub URL", aliases: ["github", "github url", "github_url", "github profile"] },
    { key: "website", label: "Website / portfolio", aliases: ["website", "portfolio", "personal site", "homepage", "website url"] },
  ];
  const WILDCARD_PREFIX = "*.";
  const HEALTH_TIMEOUT_MS = 12000;
  const HEALTH_WAIT_MS = 120000;

  // Built-in fields with the user's saved labels and aliases, followed by the custom ones.
  function fieldDefinitionsFrom(saved = []) {
    const savedByKey = new Map(saved.map((field) => [field.key, field]));
    const builtInKeys = new Set(DEFAULT_FIELDS.map((field) => field.key));
    return [
      ...DEFAULT_FIELDS.map((field) => ({ ...field, ...(savedByKey.get(field.key) || {}) })),
      ...saved.filter((field) => !builtInKeys.has(field.key)).map((field) => ({ ...field, custom: true })),
    ];
  }

  // Site setting entries are either an exact origin ("https://jobs.foo.com") or a subdomain rule
  // ("https://*.inhire.app") that covers the bare domain and every subdomain of it.
  const isWildcardEntry = (entry) => String(entry).includes(`//${WILDCARD_PREFIX}`);

  function entryCovers(entry, origin) {
    if (entry === origin) return true;
    if (!isWildcardEntry(entry)) return false;
    let url;
    try { url = new URL(origin); } catch (_) { return false; }
    const [scheme, domain] = entry.split(`//${WILDCARD_PREFIX}`);
    const host = url.hostname.toLowerCase();
    return url.protocol === scheme && (host === domain || host.endsWith(`.${domain}`));
  }

  // The phone notation chosen for this site: its own entry first, then a covering subdomain rule.
  function phoneFormatFor(origin, entries = [], formats = {}) {
    if (formats[origin]) return formats[origin];
    const covering = entries.find((entry) => formats[entry] && entryCovers(entry, origin));
    return covering ? formats[covering] : "auto";
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

  async function waitForPanel(backendUrl) {
    const deadline = Date.now() + HEALTH_WAIT_MS;
    while (Date.now() < deadline) {
      await chrome.runtime.getPlatformInfo(); // an extension API call keeps the service worker alive while waiting
      const controller = new AbortController();
      const timeout = setTimeout(() => controller.abort(), HEALTH_TIMEOUT_MS);
      try {
        const response = await fetch(new URL("/health", `${backendUrl}/`), { cache: "no-store", credentials: "include", signal: controller.signal });
        if (response.ok) {
          const health = await response.json();
          if (health.ok === true && (health.database === undefined || health.database === "ok")) return;
        }
      } catch (_) {
        // A sleeping server may need a few requests to wake.
      } finally {
        clearTimeout(timeout);
      }
      if (Date.now() < deadline) await sleep(4000);
    }
    throw new Error("The panel did not become ready within two minutes.");
  }

  const resumeCache = new Map();
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && (changes.backendUrl || changes.backendToken || changes.resumeLanguage)) resumeCache.clear();
  });

  // FileReader does not exist in service workers.
  async function blobToBase64(blob) {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let binary = "";
    for (let index = 0; index < bytes.length; index += 0x8000) {
      binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
    }
    return btoa(binary);
  }

  // Download the stored PDF from the panel; fall back to the other language if this one is missing.
  async function fetchResume(language, { backendUrl, backendToken }) {
    for (const lang of [language, language === "pt" ? "en" : "pt"]) {
      const cacheKey = `${backendUrl}|${lang}`;
      if (resumeCache.has(cacheKey)) return resumeCache.get(cacheKey);
      const response = await fetch(new URL(`/api/autofill-resume?language=${lang}`, `${backendUrl}/`), {
        cache: "no-store",
        headers: backendToken ? { Authorization: `Bearer ${backendToken}` } : {},
        credentials: backendToken ? "omit" : "include",
      });
      if (response.status === 404) continue;
      if (response.status === 401) throw new Error("The panel rejected the access token. Check it in Settings.");
      if (!response.ok) throw new Error(`Panel returned ${response.status} for the resume`);
      const resume = { base64: await blobToBase64(await response.blob()), language: lang };
      resumeCache.set(cacheKey, resume);
      return resume;
    }
    throw new Error("No resume is registered in the panel.");
  }

  function pickResumeLanguage(preference, pageLang) {
    if (preference === "pt" || preference === "en") return preference;
    return /^en/i.test(pageLang || "") ? "en" : "pt";
  }

  // Short name with the resume's real language: CV_Ana_PT.pdf / CV_Ana_EN.pdf.
  function resumeFileName(profile, language) {
    const firstName = String(profile.firstName || profile.fullName || "").trim().split(/\s+/)[0]
      .normalize("NFD").replace(/[^A-Za-z0-9-]/g, "");
    return `${["CV", firstName, language.toUpperCase()].filter(Boolean).join("_")}.pdf`;
  }

  async function attachResume(tabId, target, settings, ready) {
    const { backendUrl, profile, resumeLanguage } = settings;
    if (!backendUrl) return " No resume attached: set a backend URL in Settings.";
    try {
      await ready(backendUrl);
      const resume = await fetchResume(pickResumeLanguage(resumeLanguage, target.pageLang), settings);
      const fileName = resumeFileName(profile, resume.language);
      const [response] = await chrome.scripting.executeScript({
        target: { tabId, frameIds: [target.frameId] },
        func: (message) => globalThis.__odradekAttachResume(message),
        args: [{ base64: resume.base64, mimeType: "application/pdf", fileName }],
      });
      return response?.result?.attached
        ? ` Resume attached (${fileName}).`
        : ` Resume not attached: ${response?.result?.reason || "unknown reason"}.`;
    } catch (error) {
      return ` Resume not attached: ${error.message || "unknown error"}`;
    }
  }

  // Fill every frame of the tab with the saved profile, then attach the resume to the first frame that asks for one.
  async function fillTab(tabId, { ready = waitForPanel } = {}) {
    const stored = await chrome.storage.local.get(["profile", "fields", "whitelist", "phoneFormats", "backendUrl", "backendToken", "resumeLanguage"]);
    const profile = stored.profile || {};
    const tab = await chrome.tabs.get(tabId);
    let origin = "";
    try { origin = new URL(tab.url).origin; } catch (_) { /* no readable URL: the phone format stays automatic */ }

    const target = { tabId, allFrames: true };
    await chrome.scripting.executeScript({ target, files: ["content.js"] });
    const injected = await chrome.scripting.executeScript({
      target,
      func: (message) => globalThis.__odradekFill?.(message),
      args: [{ profile, fields: fieldDefinitionsFrom(stored.fields || []), phoneFormat: phoneFormatFor(origin, stored.whitelist || [], stored.phoneFormats || {}) }],
    });
    const frames = injected.filter((item) => item.result).map((item) => ({ frameId: item.frameId, ...item.result }))
      .sort((a, b) => a.frameId - b.frameId);
    if (!frames.length) throw new Error("The page helper did not respond. Reload the webpage and try again.");

    const filled = frames.flatMap((frame) => frame.filled || []);
    const skipped = frames.flatMap((frame) => frame.skipped || []);
    // Name the embedded sites that received data, so it never happens unnoticed.
    const embedded = [...new Set(frames.filter((frame) => frame.frameId !== 0 && frame.filled?.length).map((frame) => frame.host).filter(Boolean))];
    const frameNote = embedded.length ? ` Includes embedded form(s) from ${embedded.join(", ")}.` : "";

    const resumeFrame = frames.find((frame) => frame.resumeInputs > 0);
    const resumeNote = resumeFrame
      ? await attachResume(tabId, { frameId: resumeFrame.frameId, pageLang: frames[0].pageLang || resumeFrame.pageLang }, {
        profile, backendUrl: stored.backendUrl || "", backendToken: stored.backendToken || "", resumeLanguage: stored.resumeLanguage || "auto",
      }, ready)
      : "";
    return { filled, skipped, frameNote, resumeNote };
  }

  return { DEFAULT_FIELDS, WILDCARD_PREFIX, fieldDefinitionsFrom, isWildcardEntry, entryCovers, phoneFormatFor, fillTab, waitForPanel };
})();
