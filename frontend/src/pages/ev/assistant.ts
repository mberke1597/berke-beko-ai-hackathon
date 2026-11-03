import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import { tr } from "../../i18n/tr.ts";
import { openVoiceOverlay } from "./voice.ts";

/** Asistan tab: empty states for suggestions, energy and chat (no backend in slice 1). */
export function renderAssistant(main: HTMLElement): () => void {
  const a = tr.home.assistant;
  const messages = el("div", { class: "ev-chat__messages", "aria-live": "polite" }, [
    el("div", { class: "ev-empty ev-empty--chat" }, [
      el("span", { class: "ev-empty__icon ev-empty__icon--brand" }, [icon("sparkle", "icon icon--xl")]),
      el("h3", { class: "ev-empty__title" }, [a.chatEmptyTitle]),
      el("p", { class: "ev-empty__body" }, [a.chatEmptyBody]),
      el(
        "ul",
        { class: "ev-examples" },
        a.examples.map((example) => el("li", { class: "ev-examples__item" }, [`“${example}”`])),
      ),
    ]),
  ]);

  const input = el("input", { id: "assistant-input", class: "input", type: "text", autocomplete: "off", placeholder: a.inputPlaceholder });
  const voice = el("button", { type: "button", class: "ev-icon-button ev-chat__mic", "aria-label": tr.home.voice.button }, [icon("mic")]);
  const form = el("form", { class: "ev-chat__form" }, [
    el("label", { class: "visually-hidden", for: "assistant-input" }, [a.inputLabel]),
    input,
    el("button", { type: "submit", class: "button button--primary", "aria-label": a.send }, [icon("send")]),
    voice,
  ]);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const text = input.value.trim();
    if (text === "") return;
    messages.append(
      el("p", { class: "voice__bubble voice__bubble--user" }, [text]),
      el("p", { class: "voice__bubble voice__bubble--assistant" }, [a.soon]),
    );
    input.value = "";
  });
  voice.addEventListener("click", () => {
    const host = main.closest<HTMLElement>(".ev-app") ?? main;
    openVoiceOverlay(host, voice);
  });

  main.replaceChildren(
    el("div", { class: "ev-screen" }, [
      el("div", { class: "ev-screen__head" }, [
        el("h1", { class: "ev-screen__title" }, [icon("sparkle", "icon ev-brand-icon"), a.title]),
      ]),
      el("section", { class: "ev-panel", "aria-labelledby": "assistant-suggestions" }, [
        el("h2", { id: "assistant-suggestions", class: "ev-panel__title" }, [a.suggestionsTitle]),
        el("p", { class: "ev-muted" }, [a.suggestionsEmpty]),
      ]),
      el("section", { class: "ev-panel ev-energy", "aria-labelledby": "assistant-energy" }, [
        el("h2", { id: "assistant-energy", class: "ev-panel__title" }, [icon("leaf", "icon icon--sm"), a.energyTitle]),
        el("p", { class: "ev-muted" }, [a.energyEmpty]),
      ]),
      el("section", { class: "ev-panel ev-chat", "aria-labelledby": "assistant-chat" }, [
        el("h2", { id: "assistant-chat", class: "ev-panel__title" }, [a.chatTitle]),
        messages,
        form,
      ]),
      el("div", { class: "ev-link-row", "aria-disabled": "true" }, [
        icon("sparkle", "icon icon--sm"),
        el("span", {}, [a.learned]),
        el("span", { class: "badge badge--neutral" }, [tr.home.tasks.soon]),
      ]),
    ]),
  );
  return () => undefined;
}
