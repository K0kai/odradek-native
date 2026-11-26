# Odradek Autofill

## Install
The Odradek desktop app ships this extension and guides the installation: **Configurações → Contas e chaves → Extensão do navegador**, pick the browser and press **Instalar extensão**. Browsers do not let a program install an extension that is not in their store, so there are a few clicks (enable **Developer mode**, **Load unpacked**, paste the folder path the app copies). The app keeps that folder (`%LOCALAPPDATA%\Odradek\extension`) up to date, and the extension reloads itself when you open the panel after an update.

Then open the side panel and press **Connect to Odradek**; allow the connection in the app.

For development, load this `extension/` folder with **Load unpacked**. The `key` in `manifest.json` gives it the same ID everywhere (the app only pairs with that ID), so do not change it.

## Layout
- `manifest.json` - MV3 config and side panel registration (Chrome 141+)
- `connect.js` - one-click connection to the desktop app and self-reload after an app update
- `sidepanel.html/js` - profile editor and application assistance (storage: chrome.storage.local)
- `background.js` - opens the side panel from the extension icon; handles the keyboard shortcut and the context menu
- `fill-runner.js` - fill flow shared by the panel and the service worker: all frames, then the resume
- `content.js`    - shared form engine for visible native and common ARIA controls; small host adapters can extend it only for unusual site widgets

## Filling a page
**Assist application** works on any `http://` or `https://` website; nothing has to be registered first. It only runs when you ask for it, in one of three ways:
- the **Assist application** button in the side panel;
- the keyboard shortcut **Alt+Shift+F** (change it at `chrome://extensions/shortcuts`);
- right-click the page → **Fill application with Odradek**.

With the shortcut or the menu the panel can stay closed: a small notice on the page reports the result. On pages Chrome protects (`chrome://`, the Web Store) the toolbar icon shows `!` instead.

**Embedded forms.** Forms inside iframes (such as an applicant-tracking system embedded in a company page) are filled too, including the resume upload. Because every embedded frame with matching fields receives profile values, the result names the embedded sites that were filled; review them. Frames smaller than 200×100 px are ignored.

## Site settings
Optional. Open the **Site settings** pill on Home to change how a particular site is filled; today that is the phone notation. The extension stores only the origin (`scheme://host`), so every route and query string on it is covered.

For platforms where every company has its own subdomain (InHire, for example), use **+ Add all subdomains**. It suggests a rule from the current tab (`https://acme.inhire.app` becomes `*.inhire.app`) and you can edit it before adding. A rule like `https://*.inhire.app` covers the bare domain and every subdomain and replaces any exact entries it now covers. Rules that are too broad (`*.com`, `*.com.br`) are rejected.

The manifest asks for access to all `http` and `https` sites at install time, so Chrome does not prompt for each site. If you limit the extension's site access in Chrome, the panel asks for the site when you fill it.

## Backend settings
Open **Settings** on Home and enter the origin of your Odradek Scraper server (for example, `https://odradek.example.com`). It starts pointed at the desktop app on `http://127.0.0.1:8765`; the address is saved on the current device. HTTPS is required for remote servers; HTTP is accepted for `localhost` and `127.0.0.1`.

The Odradek desktop app for Windows serves the same API on `http://127.0.0.1:8765`, so it works with plain HTTP and needs no extra setup. **Connect to Odradek** gets the access token for you.

## Application assistance
Type your details in **Fields**; they are stored in the extension profile on this device. The shared engine matches labels, accessibility names, autocomplete metadata and saved aliases. It supports native text fields and selects, radio/checkbox groups, editable text areas and many ARIA autocomplete widgets; it also searches open Shadow DOM. When a field has no linked label (Vuetify forms such as Quickin), the loose label just above it is used. Names, email, phone and CPF are not matched from sentences longer than eight words, so a question about someone else is left blank. If a site uses an unusual custom widget, a narrow hostname adapter can extend the same engine without changing the common flow. Legal consent and final submission remain for you to review and handle.

## Connecting to the panel
With the desktop app, press **Connect to Odradek** and allow it in the app: nothing to copy. To connect manually instead (or to a remote panel), open **Settings → Accounts and keys → Browser extension** there and press **Generate token**. Copy the panel URL and the token (shown only once), then paste them into the extension's **Settings** (Backend URL and Access token). The extension sends the token as `Authorization: Bearer …` to the assistant, resume, LinkedIn and Indeed endpoints only; it does not depend on a panel login or cookies. Generating a new token disconnects the previous one, and **Revoke** removes access. The token is stored on this device in `chrome.storage.local`.

## Assistant
Open the **Assistant** pill to chat with the assistant configured in your Odradek Scraper backend. Questions you send are processed by that backend and its configured AI provider. The panel and database health check runs in the background while you select page content, and completes before a question is sent. To use a question from the current website, choose **Select a question from this page**, allow site access if Chrome asks, hover over the element, and left-click it. Its inner HTML is added to the chat box for review; it is sent only when you press **Send to assistant**. Right-click or press Escape to cancel selection.

## Sending LinkedIn jobs to Odradek
On a LinkedIn jobs page (Jobs → Recommended, or a search), the home view shows **Send LinkedIn jobs**. It scrolls the list in your own tab, reads the job IDs from the cards and sends them to the Odradek app (`POST /api/linkedin-import` with `{"enqueue": [...]}`, access token required); it finishes in a few seconds. Odradek keeps the IDs in a persistent background queue, skips jobs it already has, reads each job's public page itself (three at a time, a few seconds apart; when LinkedIn answers 429 it waits 1, 2, 4, 8 and then 15 minutes and retries, without losing anything) and saves and triages the jobs as they arrive, with the source "LinkedIn:Recomendadas". You can close the panel, and the queue survives closing Odradek. Nothing is clicked or navigated for you, and no LinkedIn credentials are used. A page of results holds roughly 25 jobs; open the next page and send again for more. **LinkedIn diagnostics** copies a structure-only report (no job text) that helps when LinkedIn changes its page.

## Sending Indeed jobs to Odradek
On an Indeed search, or the home page with recommended jobs (`indeed.com` and its country subdomains, such as `br.indeed.com`), the home view shows **Send Indeed jobs**. It reads the cards on the page in your own tab and asks Odradek which ones it does not have yet (`POST /api/indeed-import` with `{"ids": [...]}`, access token required). Indeed has no public job page that Odradek could read by itself, so for each new job **your tab** reads the job page, one at a time with a few seconds between them, and the extension sends the complete jobs in batches of five (`{"jobs": [...]}`). Odradek never contacts Indeed. Jobs are saved with the source "Indeed:Recomendadas" and triaged as they arrive.

**Max new jobs**, under the button, limits how many are read on each press (25 by default, remembered on this device; blank reads all, up to 500). Only jobs Odradek does not have yet count, so pressing again reads the next ones.

The app shows the progress in **Filas → Vagas do Indeed**: the extension also tells it which jobs it is going to read, which it could not read and why it stopped. **Esvaziar fila** there makes the extension stop after the batch in progress.

Keep the panel and the Indeed tab open until it finishes. Each batch is saved as soon as it is sent, so you can run it again and only the missing jobs are read. If Indeed asks for a verification, the extension stops and does not try to get around it: solve it in the tab and press the button again. As elsewhere in Odradek, two jobs with the same company, title and location count as one. A search shows one page of results; open the next page and send again for more. **Indeed diagnostics** copies a structure-only report (no job text) that helps when Indeed changes its page.

## Resume upload
When the page has an empty resume upload field, **Assist application** downloads your resume PDF from the panel (`GET /api/autofill-resume?language=pt|en`) and attaches it automatically. Choose Português, English or Automatic (page language) in **Settings**; if the chosen language has no resume, the other one is used. The request is authenticated with the extension's access token. Cover letters, medical or diversity uploads are never touched, and a field that already has a file is left alone. The uploaded file gets a short name with the first name and the language of the PDF actually sent: `CV_Ana_PT.pdf` or `CV_Ana_EN.pdf`.

## How fields are written
Each field is focused, typed into like a paste, then blurred, so forms that only register a value on focus or blur see it. About 300 ms later, text fields that the page emptied are written once more; if they still do not keep the value, they are counted as left unchanged.

## InHire
`inhire.app` and its company subdomains have a host adapter for custom widgets: phone (calling code + number), country (after ticking the not-Brazilian box), and the searchable `City - UF` list. Save your state (e.g. `SP`) in Fields so the right city is picked.

## Phone and calling code
Many sites split the phone into a calling-code field and a number field. The **Phone country code** field (e.g. `55`; the `+` is optional, `55` and `+55` are the same, and the extension adds the `+` only where the site's field expects it) fills the code field — a native select such as `Brazil (+55)`, or a text input (`+55`, or `55` when the field hints no plus sign or its `maxlength` is too short) — and, when a page has such a field, the number field gets the national number only (`(11) 99999-0000` for Brazil, bare digits otherwise). Pages with a single phone field keep the old behaviour (full `+CC…` number, masked for Brazil). Combined widgets (`.PhoneInput`, `.iti`, `.react-tel-input`) are left to the phone field.

**A single phone field that needs the code inside the number.** Some sites ask for `+5511999990000` in one field instead of a separate code field. In **Auto** the extension writes the number with the code when the field says so (a `+` already shown, a placeholder with 12 or more digits, or wording such as "+55", "DDI", "country code" or "international"); otherwise a Brazilian number goes in as `(11) 99999-0000`. When a site needs the other notation, open **Site settings**, add the site and set **Phone** on its row: **with country code** always writes `+<code><number>`, **without country code** always writes only the national number, and **auto** goes back to reading the page. A rule such as `*.inhire.app` applies to all its subdomains. Calling-code widgets (`.PhoneInput`, `.react-tel-input`, `.iti`) and pages with a separate code field keep deciding for themselves.

A code field that already shows the same code is skipped; a different one (usually the site's default, often `+1`) is replaced. Labels such as "Country code", "Calling code", "Dial code", "Código do país" and "DDI" are recognised; add more aliases under **Fields**.

## Add or edit a field
Use **+ Add a field** in the side panel to create one, or select **Edit** beside an existing field to change its label and website aliases.

## Ideas for later
- Multiple profiles
- Site-specific adapters for multi-step forms (Greenhouse, Lever, Gupy)
- Auto-fill on page load
- Cover-letter snippets / saved answers to common questions
