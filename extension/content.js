if (!globalThis.__JOB_AUTOFILL_CONTENT_READY__) {
globalThis.__JOB_AUTOFILL_CONTENT_READY__ = true;

// Order matters: the first matching rule wins, so put specific rules first.
const RULES = [
  // Calling code field (DDI). Must come before "phone" (its label usually says "phone country code") and "country".
  { key: "phoneCountryCode", re: /(country|calling|dial)[\s_-]*code|c[oó]digo[\s_-]*(do[\s_-]*)?pa[ií]s|\bddi\b|\bprefixo\b/i },
  { key: "email",     re: /e-?mail/i },
  { key: "linkedin",  re: /linkedin/i },
  { key: "github",    re: /github/i },
  { key: "website",   re: /website|portfolio|personal.?site|homepage/i },
  { key: "phone",     re: /phone|mobile|\btel\b|celular|telefone/i },
  { key: "firstName", re: /first.?name|given.?name|fname|primeiro.?nome|^nome$/i },
  { key: "lastName",  re: /last.?name|family.?name|surname|lname|sobrenome/i },
  { key: "fullName",  re: /full.?name|your.?name|nome.?completo|^name$/i },
  { key: "city",      re: /\bcity\b|cidade|address-level2/i },
  { key: "streetAddress", re: /street.?address|address.?line|logradouro|endereço|endereco/i },
  { key: "state",     re: /\bstate\b|province|estado|address-level1/i },
  { key: "country",   re: /country|pa[ií]s/i },
  { key: "zip",       re: /zip|postal|cep/i },
  { key: "currentCompany", re: /current.?company|current.?employer|empresa.?atual/i },
  { key: "cpf",       re: /\bcpf\b|tax.?id|national.?id/i },
  { key: "salaryExpectation", re: /salary|compensation|pretensão|pretensao|remuneração|remuneracao/i },
  { key: "contractType", re: /contract.?type|employment.?type|tipo.?de.?contrata/i },
];

const SKIP_TYPES = ["hidden", "password", "file", "checkbox", "radio", "submit", "button", "image"];
const NEVER_AUTOFILL_RE = /\b(consent|privacy|terms|agree|authorization|autoriz[oa]|concordo|privacidade|termos)\b/i;

// Optional host adapters handle unusual widgets; generic form detection remains the default.
const SITE_ADAPTERS = new Map();

function registerSiteAdapter(hostname, adapter) {
  SITE_ADAPTERS.set(String(hostname).toLowerCase(), adapter);
}

function adapterForPage() {
  const host = location.hostname.toLowerCase();
  return [...SITE_ADAPTERS.entries()]
    .sort(([a], [b]) => b.length - a.length)
    .find(([domain]) => host === domain || host.endsWith(`.${domain}`))?.[1] || null;
}

let rootCache = null;
function allRoots() {
  if (rootCache) return rootCache;
  const roots = [document];
  for (let index = 0; index < roots.length; index++) {
    for (const el of roots[index].querySelectorAll("*")) {
      if (el.shadowRoot) roots.push(el.shadowRoot);
    }
  }
  rootCache = roots;
  return rootCache;
}

function allElements(selector) {
  return allRoots().flatMap((root) => [...root.querySelectorAll(selector)]);
}

function byId(id) {
  for (const root of allRoots()) {
    const found = root.getElementById?.(id) || root.querySelector?.(`#${CSS.escape(id)}`);
    if (found) return found;
  }
  return null;
}

// Collect every text hint we can find about a field, including common component wrappers.
function hints(el) {
  const parts = [el.name, el.id, el.placeholder, el.getAttribute("aria-label"),
    el.getAttribute("aria-description"), el.getAttribute("data-field"),
    el.getAttribute("data-testid"), el.getAttribute("data-automation-id"), el.autocomplete];
  if (el.labels) parts.push(...[...el.labels].map((l) => l.innerText));
  for (const attr of ["aria-labelledby", "aria-describedby"]) {
    const ids = el.getAttribute(attr);
    if (ids) parts.push(...ids.split(/\s+/).map((id) => byId(id)?.innerText));
  }
  const fieldset = el.closest("fieldset");
  if (fieldset) parts.push(fieldset.querySelector("legend")?.innerText);
  const group = el.closest("[role='group'], [role='radiogroup']");
  if (group) {
    parts.push(group.getAttribute("aria-label"));
    const labelled = group.getAttribute("aria-labelledby");
    if (labelled) parts.push(...labelled.split(/\s+/).map((id) => byId(id)?.innerText));
  }
  const wrapper = el.closest(".field, .form-field, .form-group, [data-field], [data-testid*='field']");
  const label = wrapper?.querySelector("label, [class*='label'], [data-testid*='label']");
  if (label && label !== el) parts.push(label.innerText);
  if (!hasLinkedLabel(el)) parts.push(nearbyLabel(el)?.innerText);
  return parts.filter(Boolean).map((s) => String(s).trim()).filter(Boolean);
}

// True when the markup itself says what the field is. An aria-labelledby that points at a missing element does not count.
function hasLinkedLabel(el) {
  if (el.getAttribute("aria-label") || [...(el.labels || [])].some((label) => label.innerText.trim())) return true;
  return (el.getAttribute("aria-labelledby") || "").split(/\s+/).some((id) => id && byId(id)?.innerText.trim());
}

// Some component libraries (Vuetify, as on Quickin) draw the label as a plain sibling with no "for". Climb while the
// container still holds only this field, and take the loose <label> written before it.
function nearbyLabel(el) {
  if (["radio", "checkbox", "file", "hidden", "submit", "button"].includes(el.type)) return null;
  const otherControls = "input:not([type='hidden']), textarea:not([readonly]), select";
  let node = el.parentElement;
  for (let depth = 0; depth < 6 && node && !node.matches("form, body"); depth++, node = node.parentElement) {
    if ([...node.querySelectorAll(otherControls)].some((control) => control !== el)) return null;
    const found = [...node.querySelectorAll("label")].reverse().find((label) => !label.control
      && !label.htmlFor && label.innerText.trim()
      && label.compareDocumentPosition(el) & Node.DOCUMENT_POSITION_FOLLOWING);
    if (found) return found;
  }
  return null;
}

function normalizeHint(value) {
  return String(value || "").toLocaleLowerCase().replace(/[^\p{L}\p{N}]+/gu, " ").trim();
}

function optionMatches(option, wanted) {
  const candidate = normalizeHint(option);
  const target = normalizeHint(wanted);
  return Boolean(candidate && target && (candidate === target
    || candidate.startsWith(`${target} `)
    || candidate.endsWith(` ${target}`)
    || candidate.includes(` ${target} `)));
}

// Data about the applicant. A long sentence that merely mentions one of these ("what is the full name of the employee who
// referred you?") is a question about something else, so only short hints may match them partially.
const IDENTITY_KEYS = new Set(["firstName", "lastName", "fullName", "email", "phone", "cpf"]);
const MAX_IDENTITY_HINT_WORDS = 8;
const fitsKey = (key, hint) => !IDENTITY_KEYS.has(key) || normalizeHint(hint).split(" ").length <= MAX_IDENTITY_HINT_WORDS;

function matchKey(el, fields = []) {
  const parts = hints(el).map(normalizeHint).filter(Boolean);
  const aliases = fields.flatMap((field) => [
    { key: field.key, alias: field.label },
    { key: field.key, alias: field.key },
    ...(field.aliases || []).map((alias) => ({ key: field.key, alias })),
  ]).map((item) => ({ ...item, normalized: normalizeHint(item.alias) }))
    .filter((item) => item.normalized)
    .sort((a, b) => b.normalized.length - a.normalized.length);

  // Prefer exact name/id/label matches before checking for aliases within longer hints.
  for (const item of aliases) {
    if (parts.includes(item.normalized)) return item.key;
  }
  for (const item of aliases) {
    if (parts.some((part) => fitsKey(item.key, part) && ` ${part} `.includes(` ${item.normalized} `))) return item.key;
  }

  for (const rule of RULES) {
    if (hints(el).some((part) => fitsKey(rule.key, part) && rule.re.test(part))) return rule.key;
  }
  return null;
}

// Move focus in or out so forms that record or validate on focus/blur notice the field. Chrome can withhold the native
// events while the page itself is not focused (the click happens in the side panel), so send them by hand when they do not fire.
function moveFocus(el, type) {
  let fired = false;
  const mark = () => { fired = true; };
  el.addEventListener(type, mark);
  if (type === "focus") el.focus?.({ preventScroll: true });
  else el.blur?.();
  el.removeEventListener(type, mark);
  if (fired) return;
  el.dispatchEvent(new FocusEvent(type, { composed: true }));
  el.dispatchEvent(new FocusEvent(type === "focus" ? "focusin" : "focusout", { bubbles: true, composed: true }));
}

const TYPEABLE_TYPES = ["text", "email", "tel", "url", "search", "number", "textarea"];
const compactText = (value) => String(value || "").replace(/[^\p{L}\p{N}]+/gu, "").toLocaleLowerCase();

// Replace the content the way a keyboard paste would, so the browser itself emits the input events.
function typeText(el, value, editable) {
  const active = el.getRootNode().activeElement;
  if (active !== el) return false; // never type into whatever else holds the focus
  if (editable) {
    const range = document.createRange();
    range.selectNodeContents(el);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  } else {
    el.select();
  }
  return document.execCommand("insertText", false, value);
}

// Set value in a way React/Vue/Angular forms notice. keepFocus leaves the field open (autocomplete lists close on blur).
function setValue(el, value, { keepFocus = false } = {}) {
  const editable = el.isContentEditable || el.getAttribute("contenteditable") === "true";
  const native = el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement || el instanceof HTMLSelectElement;
  if (!editable && !native && el.getAttribute("role") === "combobox") {
    el.click();
    return;
  }
  const wanted = String(value);
  moveFocus(el, "focus");
  const typed = (editable || TYPEABLE_TYPES.includes(el.type)) && typeText(el, wanted, editable);
  // Fall back to a direct write when typing did not happen or the field did not keep the text (a mask may reformat it).
  if (!typed || compactText(editable ? el.textContent : el.value) !== compactText(wanted)) {
    if (editable) {
      el.textContent = wanted;
    } else if (native) {
      const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype
        : el instanceof HTMLSelectElement ? HTMLSelectElement.prototype : HTMLInputElement.prototype;
      Object.getOwnPropertyDescriptor(proto, "value").set.call(el, wanted);
    }
    el.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertReplacementText", data: wanted }));
  }
  el.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
  if (!keepFocus) moveFocus(el, "blur");
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function waitFor(fn, timeout = 4000, step = 100) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    const result = fn();
    if (result) return result;
    await sleep(step);
  }
  return fn() || null;
}

const foldText = (value) => normalizeHint(value).normalize("NFD").replace(/[̀-ͯ]/g, "");

const isVisible = (el) => Boolean(el?.getClientRects?.().length) && getComputedStyle(el).visibility !== "hidden";

// ---- Phone normalization ----------------------------------------------------------------
const CALLING_CODES = new Set(["1", "7", "20", "27", "30", "31", "32", "33", "34", "36", "39", "40", "41", "43", "44",
  "45", "46", "47", "48", "49", "51", "52", "53", "54", "55", "56", "57", "58", "60", "61", "62", "63", "64", "65",
  "66", "81", "82", "84", "86", "90", "91", "92", "93", "94", "95", "98", "212", "213", "216", "218", "351", "352",
  "353", "354", "355", "356", "357", "358", "359", "370", "371", "372", "380", "381", "385", "386", "420", "421",
  "591", "593", "595", "598", "971", "972"]);

// Split any common phone notation into calling code + national digits (Brazil is the default).
// hintCc: the calling code saved in the profile; it decides numbers written without "+" (other than the Brazilian shapes below).
function phoneParts(raw, hintCc = "") {
  const text = String(raw || "").trim();
  let digits = text.replace(/\D/g, "");
  if (!digits) return null;
  const hint = String(hintCc || "").replace(/\D/g, "");
  if (hint && hint !== "55" && !text.startsWith("+") && !text.startsWith("00")) return { cc: hint, national: digits };
  if (text.startsWith("+") || text.startsWith("00")) {
    if (text.startsWith("00")) digits = digits.slice(2);
    for (const length of [3, 2, 1]) {
      const cc = digits.slice(0, length);
      if (CALLING_CODES.has(cc) && digits.length > length) return { cc, national: digits.slice(length) };
    }
    return null;
  }
  if ((digits.length === 12 || digits.length === 13) && digits.startsWith("55")) return { cc: "55", national: digits.slice(2) };
  if (digits.length === 10 || digits.length === 11) return { cc: "55", national: digits };
  return { cc: "", national: digits };
}

function formatBrazilNational(national) {
  if (national.length === 11) return `(${national.slice(0, 2)}) ${national.slice(2, 7)}-${national.slice(7)}`;
  if (national.length === 10) return `(${national.slice(0, 2)}) ${national.slice(2, 6)}-${national.slice(6)}`;
  return national;
}

// Wording near a phone field that says the number must already carry the country code ("+55", "DDI", "international format"...).
const INTERNATIONAL_PHONE_CUE_RE = /\+\s*\d|\bddi\b|country\s*code|c[oó]digo\s*(do\s*)?pa[ií]s|internacional|international|e\.?\s?164|com\s+(o\s+)?(ddi|c[oó]digo)|with\s+(the\s+)?(country|calling)/i;

// Text around the field (not its name or id, which are too noisy) used to spot that cue.
function phoneCueText(el) {
  const parts = [el.placeholder, el.getAttribute("aria-label"), el.getAttribute("aria-description"), el.title, el.getAttribute("pattern")];
  if (el.labels) parts.push(...[...el.labels].map((l) => l.innerText));
  const described = el.getAttribute("aria-describedby");
  if (described) parts.push(...described.split(/\s+/).map((id) => byId(id)?.innerText));
  const wrapper = el.closest(".field, .form-field, .form-group, [data-field], [data-testid*='field']");
  const note = wrapper?.querySelector("label, [class*='label'], [class*='hint'], [class*='help'], small");
  if (note && note !== el) parts.push(note.innerText);
  return parts.filter(Boolean).join(" ");
}

// Does this single phone input expect the country code inside the number? A "+" already shown, a placeholder long enough to hold
// one (12+ digits, e.g. 5511999990000), or wording such as "with DDI" says so.
function wantsInternationalPhone(el, cueText = "") {
  const placeholder = String(el.placeholder || "");
  if (/^\s*\+/.test(el.value || "") || /^\s*\+/.test(placeholder)) return true;
  if (/^[\d\s().-]+$/.test(placeholder) && placeholder.replace(/\D/g, "").length >= 12) return true;
  return INTERNATIONAL_PHONE_CUE_RE.test(cueText);
}

// Choose the notation this particular input expects: E.164, national digits, or masked Brazilian.
// format is the per-site choice from Site settings: "international" (always +CC…), "national" (never the code), or "auto" (read the page).
function phoneValueFor(el, raw, hintCc = "", format = "auto", cueText = "") {
  const parts = phoneParts(raw, hintCc);
  if (!parts) return String(raw);
  const e164 = parts.cc ? `+${parts.cc}${parts.national}` : parts.national;
  const national = parts.cc === "55" || !parts.cc ? formatBrazilNational(parts.national) : parts.national;
  // Widgets that own the calling code decide for themselves; the site setting does not apply to them.
  if (el.closest(".PhoneInput, .react-tel-input")) return e164;
  if (el.closest(".iti")) return parts.national;
  if (format === "international") return e164;
  if (format === "national") return national;
  if (wantsInternationalPhone(el, cueText)) return e164;
  if (parts.cc === "55" || !parts.cc) return national;
  return e164;
}

// What to type into a text input that holds only the calling code: "+55" when the field hints at a plus sign, otherwise "55".
function codeValueFor(el, raw) {
  const digits = String(raw || "").replace(/\D/g, "");
  const plus = /^\s*\+/.test(el.placeholder || "") || /^\s*\+/.test(el.value || "");
  const text = plus ? `+${digits}` : digits;
  return el.maxLength > 0 && text.length > el.maxLength ? digits : text;
}

// A value that is only a calling-code prefix or an empty input mask is not user data.
function isPlaceholderValue(text, key) {
  const compact = String(text || "").replace(/[\s_()\-.]/g, "");
  if (!compact.replace(/\+/g, "")) return true;
  return key === "phone" && /^\+?\d{1,3}$/.test(compact);
}

// ---- Custom dropdown helper (react-dropdown-select and similar) -------------------------
const optionLabel = (el) => el.getAttribute("aria-label") || el.innerText || el.textContent || "";

async function selectDropdownOption(root, { search = "", match, timeout = 6000 } = {}) {
  if (!root) return null;
  root.scrollIntoView?.({ block: "center" });
  root.focus?.();
  root.click();
  const listFor = () => {
    rootCache = null;
    const lists = allElements("[data-component-name='DropdownOptions'], [role='listbox']").filter(isVisible);
    return lists.find((list) => root.contains(list)) || lists[lists.length - 1] || null;
  };
  const list = await waitFor(listFor, 3000);
  if (!list) return null;
  const searchInput = list.querySelector("[data-component-name='DropdownOptionsSearch'] input, input[type='text']");
  if (searchInput && search) {
    setValue(searchInput, search, { keepFocus: true });
    await sleep(450);
  }
  const optionSelector = "[data-component-name='DropdownOption'], [role='option']";
  const found = await waitFor(() => {
    const options = [...(listFor() || list).querySelectorAll(optionSelector)].filter(isVisible);
    return options.find((option) => match(optionLabel(option))) || null;
  }, timeout, 150);
  if (!found) {
    document.activeElement?.dispatchEvent(new KeyboardEvent("keydown", { key: "Escape", bubbles: true }));
    return null;
  }
  const label = optionLabel(found).trim();
  found.click();
  await sleep(150);
  return label;
}

// ---- Resume upload -----------------------------------------------------------------------
function findResumeInputs() {
  rootCache = null;
  return allElements("input[type='file']").filter((el) => {
    if (el.disabled || el.files?.length) return false;
    const text = [...hints(el), el.name, el.id].filter(Boolean).join(" ");
    if (/cover|carta|medical|laudo|diversity|diversidade|portf[oó]lio|portfolio/i.test(text)) return false;
    return /resume|\bcv\b|curr[ií]cul/i.test(text) || /pdf/i.test(el.getAttribute("accept") || "");
  });
}

function attachResume({ base64, mimeType = "application/pdf", fileName = "resume.pdf" } = {}) {
  const input = findResumeInputs()[0];
  if (!input) return { attached: false, reason: "no empty resume upload field found on this page" };
  if (!base64) return { attached: false, reason: "no resume data received" };
  const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
  const transfer = new DataTransfer();
  transfer.items.add(new File([bytes], fileName, { type: mimeType }));
  input.files = transfer.files;
  input.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
  input.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
  return { attached: true, fileName };
}

async function fill(profile, fields = [], options = {}) {
  rootCache = null;
  const phoneFormat = ["international", "national"].includes(options.phoneFormat) ? options.phoneFormat : "auto";
  const filled = [];
  const skipped = [];
  const written = []; // text fields to re-check once the page has reacted
  const controls = allElements("input, textarea, select, [contenteditable='true'], [role='combobox'], [role='radio'], [role='checkbox']");
  const eligible = (el) => {
    if (el.disabled || el.readOnly || el.getAttribute("aria-disabled") === "true" || !el.getClientRects().length
      || ["hidden", "password", "file", "submit", "button", "image"].includes((el.type || "").toLowerCase())) return false;
    // Custom dropdown widgets (search boxes, hidden inputs) are handled by host adapters, not typed into.
    if (el.closest(".react-dropdown-select, [data-component-name='Dropdown'], [data-component-name='DropdownOptions']")) return false;
    const style = getComputedStyle(el);
    return style.display !== "none" && style.visibility !== "hidden";
  };
  const matchProfile = (el) => {
    const key = matchKey(el, fields);
    const value = key == null ? "" : String(profile[key] ?? "").trim();
    return key ? { key, value } : null;
  };
  const phoneWidget = ".PhoneInput, .react-tel-input, .iti"; // calling code + number in one widget: the phone field handles both
  const wantedCode = String(profile.phoneCountryCode || "").replace(/\D/g, "");
  // A calling-code field is "done" only when it already shows our code; a different one is usually the site's default (often +1), so it is replaced.
  const codeShown = (el) => {
    if (el instanceof HTMLSelectElement) {
      const option = el.selectedOptions[0];
      return Boolean(option) && (optionMatches(option.label || option.textContent || "", wantedCode) || optionMatches(option.value, wantedCode));
    }
    return String(el.value || "").replace(/\D/g, "") === wantedCode;
  };
  // When the page has its own calling-code field, the number field must hold the national number only.
  const hasSeparateCode = Boolean(wantedCode) && controls.some((c) => eligible(c) && !c.closest(phoneWidget) && matchKey(c, fields) === "phoneCountryCode");
  const hasValue = (el, key) => {
    if (key === "phoneCountryCode") return codeShown(el);
    if (el instanceof HTMLSelectElement) return Boolean(el.value);
    if (el.matches("input, textarea") || el.isContentEditable) {
      const text = String(el.value || el.textContent || "").trim();
      return Boolean(text) && !isPlaceholderValue(text, key);
    }
    return Boolean(el.getAttribute("aria-valuetext") || el.getAttribute("data-value"));
  };
  const describe = (el, key) => hints(el).find(Boolean) || el.name || key;

  // Fill text controls and native selects; never overwrite anything the user entered.
  for (const el of controls) {
    const type = (el.type || "").toLowerCase();
    const role = el.getAttribute("role");
    if (!eligible(el) || SKIP_TYPES.includes(type) || ["radio", "checkbox"].includes(type)
      || ["radio", "checkbox"].includes(role)) continue;
    const match = matchProfile(el);
    if (!match?.value) continue;
    if (match.key === "phoneCountryCode" && el.closest(phoneWidget)) continue;
    if (hasValue(el, match.key)) {
      skipped.push({ label: describe(el, match.key), reason: "already filled" });
      continue;
    }
    if (el instanceof HTMLSelectElement) {
      const wanted = normalizeHint(match.value);
      const option = [...el.options].find((item) => {
        const label = normalizeHint(item.label || item.textContent || "");
        const value = normalizeHint(item.value);
        return optionMatches(label, wanted) || optionMatches(value, wanted);
      });
      if (!option || option.disabled || !option.value) {
        skipped.push({ label: describe(el, match.key), reason: "no matching option" });
        continue;
      }
      setValue(el, option.value);
    } else if (match.key === "phoneCountryCode" && el.getAttribute("role") !== "combobox" && !el.getAttribute("aria-autocomplete")) {
      setValue(el, codeValueFor(el, match.value));
    } else if (match.key === "phone") {
      const parts = phoneParts(match.value, profile.phoneCountryCode);
      const shown = String(el.value || "").replace(/\D/g, "");
      // Phone widgets with a fixed calling code need a different country picked first (adapter step).
      if (el.closest(".PhoneInput") && parts?.cc && shown && !shown.startsWith(parts.cc)) {
        skipped.push({ label: describe(el, match.key), reason: `calling code +${parts.cc} differs from the page` });
        continue;
      }
      const phone = hasSeparateCode && parts && !el.closest(phoneWidget)
        ? (parts.cc === "55" || !parts.cc ? formatBrazilNational(parts.national) : parts.national)
        : phoneValueFor(el, match.value, profile.phoneCountryCode, phoneFormat, phoneCueText(el));
      setValue(el, phone);
      written.push({ el, value: phone, label: describe(el, match.key) });
    } else {
      const autocomplete = el.getAttribute("role") === "combobox" || el.getAttribute("aria-autocomplete");
      // Some lists (Vuetify, as on Quickin) only open on a pointer press, and ignore text typed while closed.
      if (autocomplete && el.matches("input")) {
        moveFocus(el, "focus");
        el.dispatchEvent(new MouseEvent("mousedown", { bubbles: true, composed: true }));
        el.click();
        await sleep(150);
      }
      setValue(el, match.value, { keepFocus: Boolean(autocomplete) });
      // Custom autocomplete widgets often publish a matching option after typing.
      if (autocomplete) {
        const target = normalizeHint(match.value);
        const optionSelector = "[role='option'], [role='radio'], [role='menuitem'], li[aria-selected]";
        const findOption = () => {
          rootCache = null;
          const ids = `${el.getAttribute("aria-controls") || ""} ${el.getAttribute("aria-owns") || ""}`.trim().split(/s+/).filter(Boolean);
          const local = el.closest("[role='combobox'], [role='listbox'], .select, [class*='select']");
          const scope = ids.map(byId).find(Boolean) || local || el.closest("form") || el.parentElement;
          const options = [...(scope.matches?.(optionSelector) ? [scope] : []), ...scope.querySelectorAll(optionSelector)];
          return options.find((item) => item.getClientRects().length && optionMatches(
            [item.innerText, item.textContent, item.getAttribute("aria-label")].filter(Boolean).join(" "), target));
        };
        await sleep(180);
        const option = await waitFor(findOption, 1500);
        option?.click();
        if (option) await sleep(150);
        moveFocus(el, "blur");
        // With no option picked, a list that also drops the typed text was not filled.
        if (!option && el.matches("input") && !el.value.trim()) {
          skipped.push({ label: describe(el, match.key), reason: "choose it from the list" });
          continue;
        }
      } else if (el.matches("input, textarea")) {
        written.push({ el, value: match.value, label: describe(el, match.key) });
      }
    }
    filled.push(describe(el, match.key));
  }

  // Radio/checkbox controls need an explicit profile value and a matching option.
  const groups = new Map();
  for (const el of controls) {
    const type = (el.type || el.getAttribute("role") || "").toLowerCase();
    if (!eligible(el) || !["radio", "checkbox"].includes(type)) continue;
    const parentGroup = el.closest("fieldset, [role='group'], [role='radiogroup']");
    const identity = el.name ? `${type}:${el.name}`
      : parentGroup ? `${type}:group-${controls.indexOf(parentGroup)}` : `${type}:single-${controls.indexOf(el)}`;
    if (!groups.has(identity)) groups.set(identity, []);
    groups.get(identity).push(el);
  }
  for (const group of groups.values()) {
    const first = group[0];
    const question = hints(first).join(" ");
    if (NEVER_AUTOFILL_RE.test(question)) {
      skipped.push({ label: question || first.name || "consent", reason: "left for your review" });
      continue;
    }
    const match = matchProfile(first);
    if (!match?.value) continue;
    const wanted = normalizeHint(match.value);
    const choice = group.find((el) => {
      const label = (el.id ? byId(el.id)?.labels?.[0]?.innerText : "")
        || el.closest("label")?.innerText || el.getAttribute("aria-label") || el.innerText || "";
      const option = normalizeHint([label, el.value].filter(Boolean).join(" "));
      return optionMatches(option, wanted);
    });
    if (!choice) {
      skipped.push({ label: question || first.name || match.key, reason: "no matching choice" });
      continue;
    }
    if (choice.checked || choice.getAttribute("aria-checked") === "true") {
      skipped.push({ label: question || first.name || match.key, reason: "already selected" });
      continue;
    }
    choice.click();
    filled.push(question || first.name || match.key);
  }

  const adapter = adapterForPage();
  if (adapter?.afterFill) {
    const extra = await adapter.afterFill({ profile, fields, filled, skipped, helpers: {
      hints, matchKey, setValue, optionMatches, waitFor, sleep, foldText, isVisible, allElements,
      phoneParts, selectDropdownOption, normalizeHint,
    } });
    if (Array.isArray(extra?.filled)) filled.push(...extra.filled);
    if (Array.isArray(extra?.skipped)) skipped.push(...extra.skipped);
  }

  // A late re-render can wipe a value right after it is written: write it once more and report it if it will not stay.
  const wiped = () => written.filter(({ el }) => el.isConnected && !el.value.trim());
  if (written.length) {
    await sleep(300);
    const retry = wiped();
    if (retry.length) {
      for (const { el, value } of retry) setValue(el, value);
      await sleep(300);
      for (const { label } of wiped()) {
        const index = filled.indexOf(label);
        if (index >= 0) filled.splice(index, 1);
        skipped.push({ label, reason: "the page cleared the value" });
      }
    }
  }

  console.log(`[Odradek Autofill] filled ${filled.length} control(s); ${skipped.length} left unchanged`);
  return {
    filled,
    skipped,
    resumeInputs: findResumeInputs().length,
    pageLang: document.documentElement.lang || "",
  };
}

// ---- InHire (*.inhire.app, one subdomain per company) -------------------------------------------------------
// Country and city are react-dropdown-select widgets that load their options on focus, and the
// phone uses react-phone-number-input with a fixed calling code, so the generic engine cannot drive them.
const BR_STATE_CODES = {
  acre: "AC", alagoas: "AL", amapa: "AP", amazonas: "AM", bahia: "BA", ceara: "CE", "distrito federal": "DF",
  "espirito santo": "ES", goias: "GO", maranhao: "MA", "mato grosso": "MT", "mato grosso do sul": "MS",
  "minas gerais": "MG", para: "PA", paraiba: "PB", parana: "PR", pernambuco: "PE", piaui: "PI",
  "rio de janeiro": "RJ", "rio grande do norte": "RN", "rio grande do sul": "RS", rondonia: "RO", roraima: "RR",
  "santa catarina": "SC", "sao paulo": "SP", sergipe: "SE", tocantins: "TO",
};

const COUNTRY_ALIASES = {
  "united states": "estados unidos", usa: "estados unidos", us: "estados unidos",
  "united kingdom": "reino unido", uk: "reino unido", germany: "alemanha", spain: "espanha",
  france: "franca", italy: "italia", netherlands: "paises baixos", switzerland: "suica",
};

registerSiteAdapter("inhire.app", {
  async afterFill({ profile, helpers }) {
    const { waitFor, sleep, foldText, allElements, isVisible, selectDropdownOption, setValue, phoneParts } = helpers;
    const filled = [];
    const skipped = [];

    const dropdowns = () => allElements("[data-component-name='Dropdown']")
      .filter((item) => isVisible(item) && !item.closest(".PhoneInput"));
    const findDropdown = (name, labelRe) => {
      const all = dropdowns();
      const byName = all.find((item) => item.getAttribute("name") === name || item.querySelector(`[name="${name}"]`));
      if (byName) return byName;
      return all.find((item) => {
        let node = item.parentElement;
        for (let depth = 0; depth < 4 && node; depth++, node = node.parentElement) {
          if (node.querySelectorAll("[data-component-name='Dropdown']").length !== 1) continue;
          const label = node.querySelector("label, [data-component-name='Label']");
          if (label && !item.contains(label)) return labelRe.test(label.innerText || "");
        }
        return false;
      }) || null;
    };
    const rootOf = (dropdown) => dropdown.querySelector(".react-dropdown-select") || dropdown;
    const hasSelection = (dropdown) => {
      const root = rootOf(dropdown);
      const input = root.querySelector("input");
      const content = root.querySelector(".react-dropdown-select-content");
      return Boolean(input && !input.placeholder && (content?.textContent || "").trim());
    };

    // Split "City - UF" / "City, UF" and map a state name to its two-letter code.
    let city = String(profile.city || "").trim();
    let uf = String(profile.state || "").trim();
    const combined = city.match(/^(.+?)\s*[-,/]\s*([A-Za-z]{2})$/);
    if (combined) {
      city = combined[1].trim();
      uf = uf || combined[2];
    }
    uf = /^[A-Za-z]{2}$/.test(uf) ? uf.toUpperCase() : (BR_STATE_CODES[foldText(uf)] || "");

    const countryRaw = String(profile.country || "").trim();
    const country = foldText(countryRaw);
    const isBrazil = !country || ["br", "bra", "brasil", "brazil"].includes(country);

    // 1) Country — only shown after ticking "not Brazilian".
    if (!isBrazil) {
      const toggle = allElements("input[name='isNotBrazilian']")[0];
      if (toggle && !toggle.checked) {
        toggle.click();
        await sleep(300);
      }
      const dropdown = await waitFor(() => findDropdown("country", /pa[ií]s|country/i), 3000);
      if (!dropdown) {
        skipped.push({ label: "Country", reason: "country field not found" });
      } else if (hasSelection(dropdown)) {
        skipped.push({ label: "Country", reason: "already selected" });
      } else {
        const targets = [country, COUNTRY_ALIASES[country]].filter(Boolean);
        const chosen = await selectDropdownOption(rootOf(dropdown), {
          search: countryRaw,
          match: (label) => targets.includes(foldText(label)),
        });
        if (chosen) filled.push(`Country: ${chosen}`);
        else skipped.push({ label: "Country", reason: `no option matching "${countryRaw}"` });
      }
    }

    // 2) City — a searchable "City - UF" list in Brazil, a plain text input elsewhere.
    if (city) {
      if (isBrazil) {
        const dropdown = await waitFor(() => findDropdown("districtBr", /cidade|city|munic[ií]pio/i), 3000);
        if (!dropdown) {
          skipped.push({ label: "City", reason: "city field not found" });
        } else if (hasSelection(dropdown)) {
          skipped.push({ label: "City", reason: "already selected" });
        } else if (!uf) {
          skipped.push({ label: "City", reason: "add your state (e.g. SP) in Fields so the right city is chosen" });
        } else {
          const wanted = foldText(`${city} - ${uf}`);
          const chosen = await selectDropdownOption(rootOf(dropdown), {
            search: city,
            match: (label) => foldText(label) === wanted,
            timeout: 8000,
          });
          if (chosen) filled.push(`City: ${chosen}`);
          else skipped.push({ label: "City", reason: `no option matching "${city} - ${uf}"` });
        }
      } else {
        const input = await waitFor(() => allElements("input[name='district']").find(isVisible), 3000);
        if (!input) skipped.push({ label: "City", reason: "city field not found" });
        else if (input.value.trim()) skipped.push({ label: "City", reason: "already filled" });
        else {
          setValue(input, city);
          filled.push("City");
        }
      }
    }

    // 3) Phone calling code — the number input cannot change its own +CC prefix.
    const parts = phoneParts(profile.phone);
    const phoneInput = allElements(".PhoneInput input").find(isVisible);
    if (phoneInput && parts?.cc) {
      const typed = phoneInput.value.replace(/\D/g, "");
      if (typed.length <= 3 && typed !== parts.cc) {
        const wrapper = phoneInput.closest(".PhoneInput");
        const dropdown = wrapper?.querySelector("[data-component-name='Dropdown']");
        if (!dropdown) {
          skipped.push({ label: "Phone", reason: `calling code +${parts.cc} could not be selected` });
        } else {
          const ending = new RegExp(`\\+${parts.cc}$`);
          const chosen = await selectDropdownOption(rootOf(dropdown), {
            search: `+${parts.cc}`,
            match: (label) => ending.test(label.trim()),
          });
          if (chosen) {
            setValue(phoneInput, `+${parts.cc}${parts.national}`);
            filled.push(`Phone: ${chosen}`);
          } else {
            skipped.push({ label: "Phone", reason: `no calling code option for +${parts.cc}` });
          }
        }
      }
    }
    return { filled, skipped };
  },
});

// ---- Entry points for chrome.scripting (side panel, keyboard shortcut and context menu) ---
globalThis.__odradekFill = async (message = {}) => {
  // Tiny embedded frames are trackers or widgets, not application forms: never hand them personal data.
  if (window !== window.top && (window.innerWidth < 200 || window.innerHeight < 100)) return null;
  try {
    const result = await fill(message.profile || {}, message.fields || [], { phoneFormat: message.phoneFormat });
    return { ...result, host: location.hostname };
  } catch (error) {
    return { filled: [], skipped: [{ label: "page", reason: error.message || "fill failed" }], resumeInputs: 0, host: location.hostname };
  }
};

globalThis.__odradekAttachResume = (message) => {
  try {
    return attachResume(message);
  } catch (error) {
    return { attached: false, reason: error.message || "could not attach the resume" };
  }
};

const TOAST_MS = 7000;
let toastHost = null;
let toastTimer = null;

// Feedback on the page itself, for fills started while the side panel is closed.
globalThis.__odradekToast = (text, tone = "ok") => {
  if (window !== window.top) return;
  clearTimeout(toastTimer);
  toastHost?.remove();
  const host = document.createElement("div");
  toastHost = host;
  Object.assign(host.style, {
    position: "fixed", zIndex: "2147483647", right: "16px", bottom: "16px", width: "min(340px, calc(100vw - 32px))",
  });
  const shadow = host.attachShadow({ mode: "closed" });
  shadow.innerHTML = `
    <style>
      :host { all: initial; display: block; }
      .card { padding: 10px 12px; border: 1px solid #39393f; border-left: 3px solid #30a46c; border-radius: 9px; background: #111114; color: #ededf0; box-shadow: 0 8px 28px #000b; font: 12px/1.45 ui-sans-serif, sans-serif; cursor: pointer; }
      .card.error { border-left-color: #e5484d; }
      .title { margin-bottom: 3px; font-weight: 700; }
    </style>
    <div class="card" role="status" title="Click to dismiss"><div class="title">Odradek Autofill</div><div class="text"></div></div>`;
  const card = shadow.querySelector(".card");
  card.classList.toggle("error", tone === "error");
  shadow.querySelector(".text").textContent = text;
  card.addEventListener("click", () => host.remove());
  document.documentElement.appendChild(host);
  toastTimer = setTimeout(() => host.remove(), TOAST_MS);
};

let pickerCleanup = null;

function startElementPicker() {
  if (window !== window.top) return;
  pickerCleanup?.();
  const overlay = document.createElement("div");
  Object.assign(overlay.style, {
    position: "fixed", zIndex: "2147483647", pointerEvents: "none", display: "none",
    border: "2px solid #e5484d", background: "rgba(229,72,77,.12)",
    boxShadow: "0 0 0 1px rgba(255,255,255,.8), 0 0 0 99999px rgba(0,0,0,.12)",
  });
  document.documentElement.appendChild(overlay);
  const preview = document.createElement("div");
  Object.assign(preview.style, {
    position: "fixed", zIndex: "2147483647", display: "none", pointerEvents: "none",
    width: "min(360px, calc(100vw - 20px))",
  });
  const previewShadow = preview.attachShadow({ mode: "closed" });
  previewShadow.innerHTML = `
    <style>
      :host { all: initial; display: block; }
      .card { width: 100%; max-height: 180px; overflow: hidden; border: 1px solid #e5484d; border-radius: 9px; background: #111114; color: #ededf0; box-shadow: 0 8px 28px #000b; font: 12px/1.4 ui-sans-serif, sans-serif; }
      .title { padding: 6px 9px; border-bottom: 1px solid #39393f; color: #ff858a; font-weight: 700; }
      pre { max-height: 136px; overflow: auto; margin: 0; padding: 7px 9px; color: #e2e2e7; white-space: pre-wrap; overflow-wrap: anywhere; font: 10px/1.4 ui-monospace, Consolas, monospace; }
      pre::-webkit-scrollbar { width: 7px; }
      pre::-webkit-scrollbar-thumb { border-radius: 8px; background: #55555d; }
    </style>
    <div class="card"><div class="title"></div><pre></pre></div>`;
  const previewTitle = previewShadow.querySelector(".title");
  const previewHtml = previewShadow.querySelector("pre");
  document.documentElement.appendChild(preview);
  let hovered = null;
  const previousCursor = document.documentElement.style.cursor;
  document.documentElement.style.cursor = "crosshair";

  const clean = () => {
    document.removeEventListener("mousemove", onMove, true);
    document.removeEventListener("click", onClick, true);
    document.removeEventListener("contextmenu", onContext, true);
    document.removeEventListener("keydown", onKey, true);
    document.documentElement.style.cursor = previousCursor;
    overlay.remove();
    preview.remove();
    pickerCleanup = null;
  };
  const cancel = () => {
    clean();
    chrome.runtime.sendMessage({ type: "ELEMENT_PICKER_CANCELLED" }).catch(() => {});
  };
  const onMove = (event) => {
    const target = event.target;
    if (!(target instanceof Element) || target === overlay) return;
    if (target !== hovered) {
      hovered = target;
      const rect = target.getBoundingClientRect();
      Object.assign(overlay.style, {
        display: "block", left: `${rect.left}px`, top: `${rect.top}px`,
        width: `${rect.width}px`, height: `${rect.height}px`,
      });
      previewTitle.textContent = `<${target.tagName.toLowerCase()}> innerHTML`;
      const html = target.innerHTML;
      previewHtml.textContent = html.slice(0, 6000) || "(empty innerHTML)";
      if (html.length > 6000) previewHtml.textContent += "\n… (preview shortened)";
      chrome.runtime.sendMessage({
        type: "ELEMENT_PICKER_HOVER", tagName: target.tagName.toLowerCase(),
      }).catch(() => {});
    }
    preview.style.display = "block";
    const previewRect = preview.getBoundingClientRect();
    const left = Math.max(8, Math.min(event.clientX + 14, window.innerWidth - previewRect.width - 8));
    let top = event.clientY - previewRect.height - 14;
    if (top < 8) top = event.clientY + 18;
    top = Math.max(8, Math.min(top, window.innerHeight - previewRect.height - 8));
    preview.style.left = `${left}px`;
    preview.style.top = `${top}px`;
  };
  const onClick = (event) => {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    const target = event.target instanceof Element ? event.target : hovered;
    if (!target) return;
    const html = target.innerHTML;
    clean();
    chrome.runtime.sendMessage({ type: "ELEMENT_SELECTED", html, tagName: target.tagName.toLowerCase() }).catch(() => {});
  };
  const onContext = (event) => {
    event.preventDefault();
    event.stopImmediatePropagation();
    cancel();
  };
  const onKey = (event) => {
    if (event.key === "Escape") {
      event.preventDefault();
      cancel();
    }
  };
  pickerCleanup = clean;
  document.addEventListener("mousemove", onMove, true);
  document.addEventListener("click", onClick, true);
  document.addEventListener("contextmenu", onContext, true);
  document.addEventListener("keydown", onKey, true);
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg.type === "GET_PAGE_URL") {
    sendResponse({ url: location.href });
    return;
  }
  if (msg.type === "START_ELEMENT_PICKER") startElementPicker();
});
}
