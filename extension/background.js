importScripts("fill-runner.js");

const MENU_ID = "odradek-fill";
const BADGE_MS = 6000;
const busyTabs = new Set();

chrome.runtime.onInstalled.addListener(() => {
  chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
  chrome.contextMenus.removeAll(() => chrome.contextMenus.create({
    id: MENU_ID,
    title: "Fill application with Odradek",
    contexts: ["page", "editable", "frame"],
    documentUrlPatterns: ["http://*/*", "https://*/*"],
  }));
});

chrome.runtime.onStartup.addListener(() => {
  chrome.sidePanel.setPanelBehavior({ openPanelOnActionClick: true });
});

function showToast(tabId, text, tone = "ok") {
  return chrome.scripting.executeScript({
    target: { tabId, frameIds: [0] },
    func: (message, kind) => globalThis.__odradekToast?.(message, kind),
    args: [text, tone],
  });
}

// For pages where nothing can be injected (chrome://, the Web Store): explain on the toolbar icon instead.
async function showBadge(tabId, message) {
  await chrome.action.setBadgeBackgroundColor({ tabId, color: "#e5484d" });
  await chrome.action.setBadgeText({ tabId, text: "!" });
  await chrome.action.setTitle({ tabId, title: `Odradek: ${message}` });
  setTimeout(() => {
    chrome.action.setBadgeText({ tabId, text: "" }).catch(() => {});
    chrome.action.setTitle({ tabId, title: "" }).catch(() => {});
  }, BADGE_MS);
}

async function runFromBackground(tab) {
  if (!tab?.id || busyTabs.has(tab.id)) return;
  busyTabs.add(tab.id);
  try {
    const { filled, skipped, frameNote, resumeNote } = await OdradekFill.fillTab(tab.id, {
      ready: async (backendUrl) => {
        await showToast(tab.id, "Fields filled. Waiting for the panel to attach the resume…");
        await OdradekFill.waitForPanel(backendUrl);
      },
    });
    const summary = filled.length || skipped.length || resumeNote
      ? `${filled.length} field(s) filled; ${skipped.length} left unchanged.${frameNote}${resumeNote} Review the page and submit it yourself.`
      : "No matching fields were found on this page.";
    await showToast(tab.id, summary);
  } catch (error) {
    const message = error?.message || "could not fill this page";
    await showToast(tab.id, `Could not fill this page: ${message}`, "error").catch(() => showBadge(tab.id, message).catch(() => {}));
  } finally {
    busyTabs.delete(tab.id);
  }
}

chrome.commands.onCommand.addListener(async (command, tab) => {
  if (command !== "fill-page") return;
  const target = tab || (await chrome.tabs.query({ active: true, lastFocusedWindow: true }))[0];
  await runFromBackground(target);
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ID) runFromBackground(tab);
});
