import { el } from "../../components/dom.ts";
import { icon, type IconName } from "../../home/icons.ts";
import { eventMessage } from "../../home/labels.ts";
import type { HomeSession } from "../../home/session.ts";
import { badgeText, memberName } from "../../home/slice2/logic.ts";
import type { ContextMember } from "../../home/slice2/types.ts";
import type { Member } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { openComfortSheet } from "./comfort-sheet.ts";
import { setToastRegion, showToast } from "./toast.ts";
import { openVoiceOverlay } from "./voice.ts";

export type EvTab = "evim" | "tasks" | "assistant";

interface TabDef {
  id: EvTab;
  path: string;
  label: string;
  icon: IconName;
}

const tabs: readonly TabDef[] = [
  { id: "evim", path: paths.ev, label: tr.home.tabs.evim, icon: "home" },
  { id: "tasks", path: paths.evTasks, label: tr.home.tabs.tasks, icon: "tasks" },
  { id: "assistant", path: paths.evAssistant, label: tr.home.tabs.assistant, icon: "sparkle" },
];

interface ShellParts {
  root: HTMLElement;
  app: HTMLElement;
  title: HTMLElement;
  members: HTMLElement;
  main: HTMLElement;
  tabLinks: Map<EvTab, HTMLAnchorElement>;
}

let current: ShellParts | undefined;

function iconLink(name: IconName, label: string, href: string, extra?: Node): HTMLAnchorElement {
  return el("a", { class: "ev-icon-button", href, "aria-label": label }, [icon(name), extra]);
}

function initials(name: string): string {
  return name.trim().charAt(0).toLocaleUpperCase("tr-TR");
}

/** Avatars of the household: members who are away are dimmed (and announced), the current member has a ring. */
function renderMembers(container: HTMLElement, members: readonly Member[], bands: readonly ContextMember[], currentId: string): void {
  container.replaceChildren(
    ...members.map((member, index) => {
      const band = bands.find((b) => b.memberId === member.id)?.band ?? "home";
      const label = band === "home" ? tr.home.memberAtHome(member.displayName) : tr.home.memberAway(member.displayName);
      return el(
        "li",
        {
          class: `ev-avatar ev-avatar--${String((index % 3) + 1)} ${band === "home" ? "" : "ev-avatar--away"} ${member.id === currentId ? "ev-avatar--current" : ""}`,
          title: label,
        },
        [el("span", { "aria-hidden": "true" }, [initials(member.displayName)]), el("span", { class: "visually-hidden" }, [label])],
      );
    }),
  );
}

function build(root: HTMLElement, session: HomeSession): ShellParts {
  const title = el("span", { class: "ev-header__title" }, [tr.home.tabs.evim]);
  const members = el("ul", { class: "ev-header__members", "aria-label": tr.home.membersLabel });
  const badge = el("span", { class: "ev-badge", "aria-hidden": "true", hidden: true });
  const bell = iconLink("bell", tr.home.notifications, hrefFor(paths.evNotifications), badge);
  const header = el("header", { class: "ev-header" }, [
    el("div", { class: "ev-header__home" }, [title, icon("chevron", "icon icon--sm")]),
    members,
    el("div", { class: "ev-header__actions" }, [
      bell,
      iconLink("user", tr.home.profile, hrefFor(paths.evSettings)),
    ]),
  ]);

  const main = el("main", { id: "ev-main", class: "ev-main", tabindex: "-1" });

  const tabLinks = new Map<EvTab, HTMLAnchorElement>();
  const tabBar = el(
    "nav",
    { class: "ev-tabbar", "aria-label": tr.home.tabsLabel },
    [
      el(
        "ul",
        { class: "ev-tabbar__list" },
        tabs.map((tab) => {
          const link = el("a", { class: "ev-tabbar__tab", href: hrefFor(tab.path) }, [
            icon(tab.icon, "icon ev-tabbar__icon"),
            el("span", { class: "ev-tabbar__label" }, [tab.label]),
          ]);
          tabLinks.set(tab.id, link);
          return el("li", {}, [link]);
        }),
      ),
    ],
  );

  const fab = el("button", { type: "button", class: "ev-fab", "aria-label": tr.home.voice.button }, [icon("mic", "icon ev-fab__icon")]);
  fab.addEventListener("click", () => {
    openVoiceOverlay(app, fab, session);
  });

  const toasts = el("div", { class: "ev-toasts", "aria-live": "polite" });
  setToastRegion(toasts);

  const app = el("div", { class: "ev-app" }, [header, main, fab, tabBar, toasts]);
  root.replaceChildren(el("div", { class: "ev-page" }, [app]));

  const drawHeader = (): void => {
    const home = session.store.home;
    if (!home) return;
    title.textContent = home.home.name;
    renderMembers(members, home.members, session.s2.context?.members ?? [], session.s2.memberId);
  };
  const drawBadge = (): void => {
    const text = badgeText(session.s2.unread);
    badge.hidden = text === undefined;
    badge.textContent = text ?? "";
    bell.setAttribute("aria-label", text === undefined ? tr.home.notifications : `${tr.home.notifications}, ${tr.home.notif.badge(String(session.s2.unread))}`);
  };

  const unsubscribe = session.store.subscribe((change) => {
    if (!app.isConnected) {
      unsubscribe();
      return;
    }
    if (change.kind === "home") {
      drawHeader();
    } else if (change.kind === "event") {
      const appliance = session.store.getAppliance(change.event.applianceId);
      showToast(eventMessage(change.event, appliance), change.event.event === "error" ? "error" : "success");
    }
  });
  const unsubscribeS2 = session.s2.subscribe((change) => {
    if (!app.isConnected) {
      unsubscribeS2();
      return;
    }
    switch (change.kind) {
      case "member":
      case "context":
        drawHeader();
        drawBadge();
        break;
      case "notifications":
        drawBadge();
        // Task notifications are announced by the TaskAssigned toast.
        if (change.created && change.created.kind !== "task") {
          showToast(`${change.created.title}: ${change.created.text}`, change.created.kind === "alert" ? "error" : "info");
        }
        break;
      case "tasks":
        if (change.assigned) {
          const name = memberName(session.store.home?.members ?? [], change.assigned.assignedMemberId);
          showToast(tr.home.tasks.assigned(change.assigned.title, name), "info");
        }
        break;
      case "comfortCheck":
        openComfortSheet(app, session, change.check);
        break;
      case "settings":
        break;
    }
  });
  drawHeader();
  drawBadge();

  return { root, app, title, members, main, tabLinks };
}

/**
 * Renders (or reuses) the Çeliknaz app shell and returns the `<main>` element for the screen.
 * `tab` = null means a full-screen sub page (appliance detail): no header and no tab bar.
 */
export function renderEvShell(root: HTMLElement, session: HomeSession, tab: EvTab | null): HTMLElement {
  if (current?.root !== root || !current.app.isConnected) current = build(root, session);
  const { app, main, tabLinks } = current;
  app.dataset.mode = tab === null ? "detail" : "tab";
  app.dataset.tab = tab ?? "";
  for (const [id, link] of tabLinks) {
    if (id === tab) link.setAttribute("aria-current", "page");
    else link.removeAttribute("aria-current");
  }
  main.replaceChildren();
  main.scrollTop = 0;
  window.scrollTo({ top: 0 });
  return main;
}
