import { commandErrorMessage } from "../../home/api.ts";
import { el } from "../../components/dom.ts";
import type { HomeSession } from "../../home/session.ts";
import { CLOTHING_LEVELS, type Clothing, type ComfortCheck } from "../../home/slice2/types.ts";
import { tr } from "../../i18n/tr.ts";
import { openSheet } from "./sheet.ts";
import { showToast } from "./toast.ts";

/**
 * "Nasıl, rahat mı?" question after a Çeliknaz command (SignalR `ComfortCheckRequested`).
 * Does nothing when another sheet or the voice overlay is already open, so it never interrupts the user's input.
 */
export function openComfortSheet(host: HTMLElement, session: HomeSession, check: ComfortCheck): void {
  if (host.querySelector("dialog[open]")) return;
  const t = tr.home.comfortSheet;
  const device = session.store.getAppliance(check.applianceId)?.displayName ?? tr.home.card.unknownDevice;
  const sheet = openSheet(host, t.title);
  let clothing: Clothing | null = null;

  const answer = (sensation: -1 | 0 | 1, button: HTMLButtonElement): void => {
    button.disabled = true;
    void session.backend().then(
      async (b) => {
        try {
          await b.slice2.postComfortFeedback(session.s2.memberId, { applianceId: check.applianceId, sensation, clothing, commandId: check.commandId });
          showToast(t.thanks, "success");
          sheet.close();
        } catch (error) {
          button.disabled = false;
          showToast(error instanceof Error ? `${t.error} ${commandErrorMessage(error)}` : t.error, "error");
        }
      },
      () => {
        button.disabled = false;
      },
    );
  };

  const choices: [-1 | 0 | 1, string][] = [
    [-1, t.cold],
    [0, t.ok],
    [1, t.hot],
  ];
  const buttons = choices.map(([sensation, label]) => {
    const button = el("button", { type: "button", class: "ev-feel", "data-key": `feel-${String(sensation)}` }, [label]);
    button.addEventListener("click", () => {
      answer(sensation, button);
    });
    return button;
  });

  const chips = CLOTHING_LEVELS.map((level) => {
    const chip = el("button", { type: "button", class: "ev-chip", "aria-pressed": "false" }, [tr.home.settings.clothing[level]]);
    chip.addEventListener("click", () => {
      clothing = clothing === level ? null : level;
      for (const other of chips) other.setAttribute("aria-pressed", String(other === chip && clothing !== null));
    });
    return chip;
  });

  const later = el("button", { type: "button", class: "button button--ghost button--sm" }, [t.later]);
  later.addEventListener("click", sheet.close);

  sheet.body.replaceChildren(
    el("p", { class: "ev-muted" }, [t.subtitle(device)]),
    el("div", { class: "ev-feelrow" }, buttons),
    el("p", { class: "field__label" }, [t.clothingTitle]),
    el("div", { class: "ev-chips ev-chips--wrap", role: "group", "aria-label": t.clothingTitle }, chips),
    later,
  );
  buttons[1]?.focus();
}
