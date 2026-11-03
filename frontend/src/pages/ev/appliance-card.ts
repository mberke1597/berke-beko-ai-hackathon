import { el } from "../../components/dom.ts";
import { supportsCommand } from "../../home/commands.ts";
import { applianceIconName, icon, toneIconName } from "../../home/icons.ts";
import { cardSummary, stateTone, toneLabel } from "../../home/labels.ts";
import type { Appliance } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { devicePath } from "../../paths.ts";
import { hrefFor } from "../../router.ts";

export interface CardHandlers {
  onPower(appliance: Appliance, button: HTMLButtonElement): void;
}

/** Appliance card: icon, name, key state, status (text + icon + colour) and a quick power toggle. */
export function createApplianceCard(appliance: Appliance, handlers: CardHandlers, aiChanged = false): HTMLElement {
  const tone = stateTone(appliance);
  const summary = cardSummary(appliance);
  const status = toneLabel(appliance);

  const progress =
    summary.progressPct === undefined
      ? null
      : el(
          "div",
          {
            class: "appliance-card__progress",
            role: "progressbar",
            "aria-valuemin": "0",
            "aria-valuemax": "100",
            "aria-valuenow": String(summary.progressPct),
            "aria-label": tr.home.detail.progress,
          },
          [el("span", { class: "appliance-card__progress-bar", style: `width: ${String(summary.progressPct)}%` })],
        );

  const link = el(
    "a",
    {
      class: "appliance-card__link",
      href: hrefFor(devicePath(appliance.id)),
      "aria-label": tr.home.card.open(appliance.displayName, status === summary.primary ? status : `${status}, ${summary.primary}`),
    },
    [
      el("span", { class: "appliance-card__icon" }, [icon(applianceIconName(appliance.type), "icon icon--lg")]),
      aiChanged && el("span", { class: "appliance-card__ai" }, [icon("sparkle", "icon icon--xs"), tr.home.chat.aiTag]),
      el("span", { class: "appliance-card__name" }, [appliance.displayName]),
      el("span", { class: "appliance-card__primary" }, [summary.primary]),
      summary.secondary !== undefined && el("span", { class: "appliance-card__secondary" }, [summary.secondary]),
      progress,
      el("span", { class: "appliance-card__status" }, [icon(toneIconName(tone), "icon icon--xs"), status]),
    ],
  );

  const power = supportsCommand(appliance, "power")
    ? el(
        "button",
        {
          type: "button",
          class: "appliance-card__power",
          "aria-pressed": String(appliance.state.power),
          "aria-label": appliance.state.power ? tr.home.card.powerOff(appliance.displayName) : tr.home.card.powerOn(appliance.displayName),
        },
        [icon("power")],
      )
    : null;
  power?.addEventListener("click", () => {
    handlers.onPower(appliance, power);
  });

  return el("article", { class: `appliance-card appliance-card--${tone}`, "data-id": appliance.id }, [link, power]);
}
