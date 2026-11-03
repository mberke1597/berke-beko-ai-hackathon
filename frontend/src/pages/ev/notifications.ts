import { commandErrorMessage } from "../../home/api.ts";
import { el } from "../../components/dom.ts";
import { icon, type IconName } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import { formatClock, sortNotifications } from "../../home/slice2/logic.ts";
import type { AppNotification, NotificationKind } from "../../home/slice2/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { loadHome, renderLoadError, renderLoading } from "./common.ts";
import { renderSubPage } from "./subpage.ts";
import { showToast } from "./toast.ts";

export const notificationIcon: Record<NotificationKind, IconName> = {
  info: "bell",
  suggestion: "sparkle",
  comfort: "thermo",
  task: "tasks",
  alert: "alert",
};

function card(n: AppNotification, session: HomeSession): HTMLElement {
  const t = tr.home.notif;
  const time = formatClock(n.createdAtUtc);

  const markRead = n.read
    ? null
    : (() => {
        const button = el("button", { type: "button", class: "button button--ghost button--sm" }, [t.markRead]);
        button.addEventListener("click", () => {
          button.disabled = true;
          session.s2.markRead(n.id).catch((error: unknown) => {
            showToast(commandErrorMessage(error), "error");
          });
        });
        return button;
      })();

  const action = n.action
    ? (() => {
        const routineId = n.action.routineId;
        const button = el("button", { type: "button", class: "button button--primary button--sm", "data-key": `apply-${n.id}` }, [icon("check", "icon icon--sm"), t.apply]);
        button.addEventListener("click", () => {
          button.disabled = true;
          void session.backend().then(
            async (b) => {
              try {
                await b.slice2.acceptRoutine(routineId);
                showToast(t.applied, "success");
                await session.s2.markRead(n.id);
              } catch (error) {
                button.disabled = false;
                showToast(commandErrorMessage(error), "error");
              }
            },
            () => {
              button.disabled = false;
            },
          );
        });
        return button;
      })()
    : null;

  return el("article", { class: `ev-notif ev-notif--${n.kind} ${n.read ? "" : "ev-notif--unread"}`, "data-id": n.id }, [
    el("span", { class: "ev-notif__icon" }, [icon(notificationIcon[n.kind], "icon")]),
    el("div", { class: "ev-notif__main" }, [
      el("div", { class: "ev-notif__head" }, [
        el("h2", { class: "ev-notif__title" }, [n.title]),
        !n.read && el("span", { class: "ev-notif__dot", role: "img", "aria-label": t.unread }),
      ]),
      el("p", { class: "ev-notif__text" }, [n.text]),
      el("p", { class: "ev-notif__meta" }, [
        Object.hasOwn(t.kinds, n.kind) ? (t.kinds[n.kind] ?? n.kind) : n.kind,
        n.level === "critical" && ` · ${t.levels.critical ?? ""}`,
        time !== "" && ` · ${t.time(time)}`,
      ]),
      (action ?? markRead) !== null && el("div", { class: "ev-notif__actions" }, [action, markRead]),
    ]),
  ]);
}

/** Bildirimler: list of the current member's notifications with mark-read and suggestion actions. */
export function renderNotifications(main: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.notif;
  const body = renderSubPage(main, t.title, paths.ev);
  const list = el("div", { class: "ev-stack", "aria-live": "polite" });
  let unsubscribe: (() => void) | undefined;
  let disposed = false;

  function draw(): void {
    const items = sortNotifications(session.s2.notifications);
    const focus = (document.activeElement as HTMLElement | null)?.dataset.key;
    list.replaceChildren(
      ...(items.length === 0
        ? [
            el("div", { class: "ev-empty" }, [
              el("span", { class: "ev-empty__icon" }, [icon("bell", "icon icon--xl")]),
              el("h2", { class: "ev-empty__title" }, [t.emptyTitle]),
              el("p", { class: "ev-empty__body" }, [t.emptyBody]),
            ]),
          ]
        : items.map((n) => card(n, session))),
    );
    if (focus !== undefined) list.querySelector<HTMLElement>(`[data-key="${CSS.escape(focus)}"]`)?.focus();
  }

  const start = (): void => {
    renderLoading(body, t.loading);
    session.s2.loadNotifications().then(
      () => {
        if (disposed) return;
        body.replaceChildren(list);
        draw();
        unsubscribe = session.s2.subscribe((change) => {
          if (change.kind === "notifications" || change.kind === "member") draw();
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
  };
}
