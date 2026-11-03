import { commandErrorMessage } from "../../home/api.ts";
import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import { formatTl, avatarSlot, bandLabel, formatDistance, memberInitial, routeLabel } from "../../home/slice2/logic.ts";
import type { HomeSession } from "../../home/session.ts";
import {
  CLOTHING_LEVELS,
  NOTIFICATION_LEVELS,
  PRIORITY_MODES,
  ROUTES,
  type Clothing,
  type NotificationLevel,
  type Presence,
  type PriorityMode,
  type Settings,
  type SettingsPatch,
} from "../../home/slice2/types.ts";
import type { Member } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { loadHome, renderLoadError, renderLoading } from "./common.ts";
import { openSheet, sheetHost } from "./sheet.ts";
import { renderSubPage } from "./subpage.ts";
import { showToast } from "./toast.ts";

const PRESENCE_REFRESH_MS = 2500;

function avatar(members: readonly Member[], member: Member): HTMLElement {
  return el("span", { class: `ev-avatar ev-avatar--${String(avatarSlot(members, member.id))}`, "aria-hidden": "true" }, [memberInitial(member.displayName)]);
}

function section(title: string, hint: string | undefined, children: (Node | null | false)[], id: string): HTMLElement {
  return el("section", { class: "ev-panel", "aria-labelledby": id }, [
    el("h2", { id, class: "ev-panel__title" }, [title]),
    hint !== undefined && el("p", { class: "ev-muted" }, [hint]),
    ...children,
  ]);
}

function linkRow(href: string, label: string, hint?: string): HTMLElement {
  return el("a", { class: "ev-link-row ev-link-row--link", href }, [
    el("span", { class: "ev-link-row__text" }, [el("span", {}, [label]), hint !== undefined && el("span", { class: "ev-muted" }, [hint])]),
    icon("chevron", "icon icon--sm ev-link-row__chevron"),
  ]);
}

/** Ayarlar: member switcher, priority mode, notifications, quiet hours, clothing, location, learning and demo controls. */
export function renderSettings(main: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.settings;
  const body = renderSubPage(main, t.title, paths.ev);
  const content = el("div", { class: "ev-stack" });
  const presenceBox = el("div", { class: "ev-presence" });
  let disposed = false;
  let unsubscribe: (() => void) | undefined;
  let presenceTimer: ReturnType<typeof setInterval> | undefined;

  const save = (patch: SettingsPatch): void => {
    session.s2.updateSettings(patch).then(
      () => {
        showToast(t.saved, "success");
      },
      (error: unknown) => {
        showToast(error instanceof Error ? commandErrorMessage(error) : t.saveError, "error");
      },
    );
  };

  function choiceGroup<T extends string>(
    label: string,
    key: string,
    options: readonly T[],
    selected: T,
    text: (option: T) => { name: string; desc?: string; extra?: string },
    onPick: (option: T) => void,
  ): HTMLElement {
    return el(
      "div",
      { class: "ev-choices", role: "group", "aria-label": label },
      options.map((option) => {
        const info = text(option);
        const button = el("button", { type: "button", class: "ev-choice", "aria-pressed": String(option === selected), "data-key": `${key}-${option}` }, [
          el("span", { class: "ev-choice__name" }, [info.name]),
          info.extra !== undefined && el("span", { class: "ev-choice__extra" }, [info.extra]),
          info.desc !== undefined && el("span", { class: "ev-choice__desc" }, [info.desc]),
        ]);
        button.addEventListener("click", () => {
          if (option !== selected) onPick(option);
        });
        return button;
      }),
    );
  }

  function draw(settings: Settings): void {
    const members = session.store.home?.members ?? [];
    const focusKey = (document.activeElement as HTMLElement | null)?.dataset.key;

    const memberChips = el(
      "div",
      { class: "ev-memberswitch", role: "group", "aria-label": t.member.title },
      members.map((m) => {
        const active = m.id === session.s2.memberId;
        const button = el("button", { type: "button", class: "ev-membercard", "aria-pressed": String(active), "data-key": `member-${m.id}` }, [avatar(members, m), el("span", {}, [m.displayName])]);
        button.addEventListener("click", () => {
          session.s2.setMember(m.id);
          showToast(t.member.switched(m.displayName), "success");
        });
        return button;
      }),
    );

    const quietFrom = el("input", { id: "quiet-from", class: "input", type: "time", value: settings.quietHours.from, "data-key": "quiet-from" });
    const quietTo = el("input", { id: "quiet-to", class: "input", type: "time", value: settings.quietHours.to, "data-key": "quiet-to" });
    const saveQuiet = (): void => {
      if (quietFrom.value !== "" && quietTo.value !== "") save({ quietHours: { from: quietFrom.value, to: quietTo.value } });
    };
    quietFrom.addEventListener("change", saveQuiet);
    quietTo.addEventListener("change", saveQuiet);

    const sharing = el(
      "button",
      { type: "button", role: "switch", class: "ev-switch", "aria-checked": String(settings.locationSharing), "data-key": "location", "aria-describedby": "location-hint" },
      [el("span", { class: "ev-switch__track", "aria-hidden": "true" }, [el("span", { class: "ev-switch__thumb" })]), t.location.title],
    );
    sharing.addEventListener("click", () => {
      save({ locationSharing: !settings.locationSharing });
    });

    const reset = el("button", { type: "button", class: "button button--secondary ev-danger" }, [t.reset.button]);
    reset.addEventListener("click", () => {
      openResetSheet(reset, session);
    });

    content.replaceChildren(
      section(t.member.title, t.member.hint, [memberChips], "set-member"),
      section(
        t.priority.title,
        t.priority.hint,
        [
          choiceGroup<PriorityMode>(
            t.priority.title,
            "priority",
            PRIORITY_MODES,
            settings.priorityMode,
            (m) => ({ name: t.priority.modes[m].name, desc: t.priority.modes[m].desc, extra: t.priority.perMonth(formatTl(settings.estimatedMonthlyTl[m])) }),
            (m) => {
              save({ priorityMode: m });
            },
          ),
        ],
        "set-priority",
      ),
      section(
        t.notifications.title,
        undefined,
        [
          choiceGroup<NotificationLevel>(
            t.notifications.title,
            "notif",
            NOTIFICATION_LEVELS,
            settings.notificationLevel,
            (l) => ({ name: t.notifications.levels[l].name, desc: t.notifications.levels[l].desc }),
            (l) => {
              save({ notificationLevel: l });
            },
          ),
        ],
        "set-notif",
      ),
      section(
        t.quietHours.title,
        t.quietHours.hint,
        [
          el("div", { class: "ev-timerow" }, [
            el("div", { class: "field" }, [el("label", { class: "field__label", for: "quiet-from" }, [t.quietHours.from]), quietFrom]),
            el("div", { class: "field" }, [el("label", { class: "field__label", for: "quiet-to" }, [t.quietHours.to]), quietTo]),
          ]),
        ],
        "set-quiet",
      ),
      section(
        t.clothing.title,
        t.clothing.hint,
        [
          el(
            "div",
            { class: "ev-chips ev-chips--wrap", role: "group", "aria-label": t.clothing.title },
            CLOTHING_LEVELS.map((c: Clothing) => {
              const chip = el("button", { type: "button", class: "ev-chip", "aria-pressed": String(settings.clothing === c), "data-key": `clothing-${c}` }, [t.clothing[c]]);
              chip.addEventListener("click", () => {
                if (settings.clothing !== c) save({ clothing: c });
              });
              return chip;
            }),
          ),
        ],
        "set-clothing",
      ),
      el("section", { class: "ev-panel", "aria-labelledby": "set-location" }, [
        el("h2", { id: "set-location", class: "visually-hidden" }, [t.location.title]),
        sharing,
        el("p", { id: "location-hint", class: "ev-muted" }, [t.location.hint]),
      ]),
      linkRow(hrefFor(paths.evLearning), t.learning),
      reset,
      section(tr.home.presence.title, tr.home.presence.hint, [presenceBox], "set-demo"),
      linkRow(hrefFor(paths.panel), t.panelLink, t.panelHint),
    );

    if (focusKey !== undefined) content.querySelector<HTMLElement>(`[data-key="${CSS.escape(focusKey)}"]`)?.focus();
  }

  // ---------- demo controls (presence) ----------

  function drawPresence(list: readonly Presence[]): void {
    const members = session.store.home?.members ?? [];
    presenceBox.replaceChildren(
      ...members.map((m) => {
        const p = list.find((x) => x.memberId === m.id);
        const status = p
          ? [bandLabel(p.band), p.distanceM > 0 ? formatDistance(p.distanceM) : undefined, p.etaMin !== null ? tr.home.presence.eta(String(p.etaMin)) : undefined].filter((x): x is string => x !== undefined).join(" · ")
          : "";
        return el("div", { class: "ev-presence__row" }, [
          el("div", { class: "ev-presence__who" }, [
            avatar(members, m),
            el("div", {}, [el("strong", {}, [m.displayName]), el("p", { class: `ev-presence__status ev-presence__status--${p?.band ?? "home"}` }, [status])]),
          ]),
          el(
            "div",
            { class: "ev-presence__routes", role: "group", "aria-label": m.displayName },
            ROUTES.map((route) => {
              const button = el("button", { type: "button", class: "button button--secondary button--sm", "data-key": `route-${m.id}-${route}` }, [routeLabel(route)]);
              button.addEventListener("click", () => {
                button.disabled = true;
                void session.backend().then(
                  async (b) => {
                    try {
                      await b.slice2.setRoute(m.id, route);
                      showToast(tr.home.presence.routeSet(m.displayName, routeLabel(route)), "success");
                      await refreshPresence();
                    } catch (error) {
                      showToast(commandErrorMessage(error), "error");
                    } finally {
                      button.disabled = false;
                    }
                  },
                  () => {
                    button.disabled = false;
                  },
                );
              });
              return button;
            }),
          ),
        ]);
      }),
    );
  }

  async function refreshPresence(): Promise<void> {
    try {
      const list = await (await session.backend()).slice2.getPresence();
      if (!disposed && presenceBox.isConnected) {
        const focus = (document.activeElement as HTMLElement | null)?.dataset.key;
        drawPresence(list);
        if (focus?.startsWith("route-") === true) presenceBox.querySelector<HTMLElement>(`[data-key="${CSS.escape(focus)}"]`)?.focus();
      }
    } catch {
      if (!disposed && presenceBox.childElementCount === 0) presenceBox.replaceChildren(el("p", { class: "ev-muted" }, [tr.home.presence.loadError]));
    }
  }

  const start = (): void => {
    renderLoading(body, t.loading);
    session.s2.loadSettings().then(
      (settings) => {
        if (disposed) return;
        body.replaceChildren(content);
        draw(settings);
        void refreshPresence();
        presenceTimer = setInterval(() => {
          void refreshPresence();
        }, PRESENCE_REFRESH_MS);
        unsubscribe = session.s2.subscribe((change) => {
          if ((change.kind === "settings" || change.kind === "member") && session.s2.settings) draw(session.s2.settings);
        });
      },
      (error: unknown) => {
        if (!disposed) renderLoadError(body, error, start);
      },
    );
  };

  loadHome(session, body, start);
  return () => {
    disposed = true;
    unsubscribe?.();
    if (presenceTimer !== undefined) clearInterval(presenceTimer);
  };
}

function openResetSheet(opener: HTMLElement, session: HomeSession): void {
  const t = tr.home.settings.reset;
  const sheet = openSheet(sheetHost(opener), t.title, { opener });
  const confirm = el("button", { type: "button", class: "button button--primary ev-danger-fill" }, [t.confirm]);
  const cancel = el("button", { type: "button", class: "button button--secondary" }, [t.cancel]);
  cancel.addEventListener("click", sheet.close);
  confirm.addEventListener("click", () => {
    confirm.disabled = true;
    void session.backend().then(
      async (b) => {
        try {
          await b.slice2.resetLearning(session.s2.memberId);
          showToast(t.done, "success");
          sheet.close();
        } catch (error) {
          confirm.disabled = false;
          showToast(commandErrorMessage(error), "error");
        }
      },
      () => {
        confirm.disabled = false;
      },
    );
  });
  sheet.body.replaceChildren(el("p", {}, [t.body]), el("div", { class: "ev-actions" }, [confirm, cancel]));
  cancel.focus();
}
