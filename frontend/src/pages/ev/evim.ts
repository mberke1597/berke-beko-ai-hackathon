import { el, setChildren } from "../../components/dom.ts";
import { sendDeviceCommand } from "../../home/actions.ts";
import { icon } from "../../home/icons.ts";
import { formatTemp } from "../../home/labels.ts";
import type { LiveStatus } from "../../home/live.ts";
import type { HomeSession } from "../../home/session.ts";
import type { HomeView } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { createApplianceCard, type CardHandlers } from "./appliance-card.ts";
import { loadHome } from "./common.ts";
import { showToast } from "./toast.ts";

/** How often the home context (outdoor temperature, priority mode, presence) is refreshed while Evim is open. */
const CONTEXT_REFRESH_MS = 20_000;

const ALL = "all";
let selectedRoom = ALL;

function liveLabel(status: LiveStatus): string {
  if (status === "connected") return tr.home.live.connected;
  if (status === "reconnecting" || status === "connecting") return tr.home.live.reconnecting;
  return tr.home.live.disconnected;
}

function renderLive(target: HTMLElement, status: LiveStatus): void {
  target.className = `ev-live ev-live--${status}`;
  target.replaceChildren(el("span", { class: "ev-live__dot", "aria-hidden": "true" }), liveLabel(status));
}

/** Evim tab: status strip, room chips and appliance cards grouped by room. */
export function renderEvim(main: HTMLElement, session: HomeSession): () => void {
  const region = el("div", { class: "ev-screen" });
  main.replaceChildren(region);
  let unsubscribe: (() => void) | undefined;
  let unsubscribeS2: (() => void) | undefined;
  let contextTimer: ReturnType<typeof setInterval> | undefined;
  let isMock = false;

  const handlers: CardHandlers = {
    onPower: (appliance, button) => {
      button.disabled = true;
      void sendDeviceCommand(session, appliance, { type: "power", value: !appliance.state.power }).then((outcome) => {
        button.disabled = false;
        if (!outcome.ok) showToast(outcome.message, "error");
      });
    },
  };

  const draw = (view: HomeView): void => {
    const live = el("span", { class: "ev-live" });
    renderLive(live, session.store.live);

    const strip = el("div", { class: "ev-strip" });
    const drawStrip = (): void => {
      const context = session.s2.context;
      setChildren(strip, [
        el("span", { class: "ev-strip__item" }, [
          icon("sun", "icon icon--sm"),
          context ? tr.home.statusStrip.outdoor(formatTemp(context.outdoorTempC)) : tr.home.statusStrip.loading,
        ]),
        el("span", { class: "ev-strip__item" }, [
          icon("leaf", "icon icon--sm"),
          context ? tr.home.statusStrip.priority(tr.home.statusStrip.priorityModes[context.priorityMode]) : tr.home.statusStrip.loading,
        ]),
        el("span", { class: "ev-strip__end" }, [isMock && el("span", { class: "badge badge--neutral" }, [tr.home.mockBadge]), live]),
      ]);
    };
    drawStrip();

    if (selectedRoom !== ALL && !view.rooms.some((r) => r.id === selectedRoom)) selectedRoom = ALL;

    const sections = el("div", { class: "ev-rooms" });
    const cards = new Map<string, HTMLElement>();

    const drawSections = (): void => {
      cards.clear();
      const rooms = selectedRoom === ALL ? view.rooms : view.rooms.filter((r) => r.id === selectedRoom);
      sections.replaceChildren(
        ...rooms.map((room) => {
          const appliances = view.appliances.filter((a) => a.roomId === room.id);
          const headingId = `room-${room.id}`;
          return el("section", { class: "ev-room", "aria-labelledby": headingId }, [
            el("h2", { id: headingId, class: "ev-room__title" }, [room.name]),
            appliances.length === 0
              ? el("p", { class: "ev-room__empty" }, [tr.home.rooms.empty])
              : el(
                  "div",
                  { class: "ev-grid" },
                  appliances.map((appliance) => {
                    const card = createApplianceCard(appliance, handlers, session.store.isAiChanged(appliance.id));
                    cards.set(appliance.id, card);
                    return card;
                  }),
                ),
          ]);
        }),
      );
    };

    const chipButtons: HTMLButtonElement[] = [];
    const chip = (id: string, label: string): HTMLButtonElement => {
      const button = el("button", { type: "button", class: "ev-chip", "aria-pressed": String(selectedRoom === id) }, [label]);
      button.addEventListener("click", () => {
        selectedRoom = id;
        for (const b of chipButtons) b.setAttribute("aria-pressed", String(b === button));
        drawSections();
      });
      chipButtons.push(button);
      return button;
    };
    const chips = el("div", { class: "ev-chips", role: "group", "aria-label": tr.home.rooms.label }, [
      chip(ALL, tr.home.rooms.all),
      ...view.rooms.map((room) => chip(room.id, room.name)),
    ]);

    drawSections();
    region.replaceChildren(strip, chips, sections);

    unsubscribe?.();
    unsubscribeS2?.();
    unsubscribeS2 = session.s2.subscribe((change) => {
      if (change.kind === "context") drawStrip();
    });
    unsubscribe = session.store.subscribe((change) => {
      if (change.kind === "appliance" || change.kind === "aiTag") {
        const id = change.kind === "appliance" ? change.appliance.id : change.applianceId;
        const appliance = change.kind === "appliance" ? change.appliance : session.store.getAppliance(id);
        const old = cards.get(id);
        if (old && appliance) {
          const next = createApplianceCard(appliance, handlers, session.store.isAiChanged(id));
          // Keep keyboard focus on the same control after a live re-render.
          const focusPower = old.contains(document.activeElement) && document.activeElement?.classList.contains("appliance-card__power");
          const focusLink = old.contains(document.activeElement) && !focusPower;
          old.replaceWith(next);
          cards.set(id, next);
          if (focusPower) next.querySelector<HTMLElement>(".appliance-card__power")?.focus();
          if (focusLink) next.querySelector<HTMLElement>(".appliance-card__link")?.focus();
        } else if (!old && change.kind === "appliance" && session.store.home) {
          draw(session.store.home);
        }
      } else if (change.kind === "live") {
        renderLive(live, change.status);
      } else if (change.kind === "home" && session.store.home) {
        draw(session.store.home);
      }
    });
  };

  loadHome(session, region, () => {
    const view = session.store.home;
    contextTimer ??= setInterval(() => {
      void session.s2.refreshContext().catch(() => undefined);
    }, CONTEXT_REFRESH_MS);
    if (view) {
      void session.backend().then((b) => {
        isMock = b.isMock;
        if (region.isConnected) draw(view);
      });
    }
  });

  return () => {
    unsubscribe?.();
    unsubscribeS2?.();
    if (contextTimer !== undefined) clearInterval(contextTimer);
  };
}
