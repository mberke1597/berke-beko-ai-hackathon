import { el } from "../../components/dom.ts";
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

/** Placeholder until the weather mock exists (backend `Simulation__OutdoorTempC` default). */
export const PLACEHOLDER_OUTDOOR_TEMP_C = 31;

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

    const strip = el("div", { class: "ev-strip" }, [
      el("span", { class: "ev-strip__item" }, [icon("sun", "icon icon--sm"), tr.home.statusStrip.outdoor(formatTemp(PLACEHOLDER_OUTDOOR_TEMP_C))]),
      el("span", { class: "ev-strip__item" }, [icon("leaf", "icon icon--sm"), tr.home.statusStrip.priority(tr.home.statusStrip.priorityModes.saving)]),
      el("span", { class: "ev-strip__end" }, [isMock && el("span", { class: "badge badge--neutral" }, [tr.home.mockBadge]), live]),
    ]);

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
                    const card = createApplianceCard(appliance, handlers);
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
    unsubscribe = session.store.subscribe((change) => {
      if (change.kind === "appliance") {
        const old = cards.get(change.appliance.id);
        if (old) {
          const next = createApplianceCard(change.appliance, handlers);
          // Keep keyboard focus on the same control after a live re-render.
          const focusPower = old.contains(document.activeElement) && document.activeElement?.classList.contains("appliance-card__power");
          const focusLink = old.contains(document.activeElement) && !focusPower;
          old.replaceWith(next);
          cards.set(change.appliance.id, next);
          if (focusPower) next.querySelector<HTMLElement>(".appliance-card__power")?.focus();
          if (focusLink) next.querySelector<HTMLElement>(".appliance-card__link")?.focus();
        } else if (session.store.home) {
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
    if (view) {
      void session.backend().then((b) => {
        isMock = b.isMock;
        if (region.isConnected) draw(view);
      });
    }
  });

  return () => {
    unsubscribe?.();
  };
}
