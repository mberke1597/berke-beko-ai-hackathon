import { el } from "../../components/dom.ts";
import type { ChatController } from "../../home/chat/controller.ts";
import { icon } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import { tr } from "../../i18n/tr.ts";
import { createEntryList } from "./chat-view.ts";
import { renderLoading } from "./common.ts";
import { openVoiceOverlay } from "./voice.ts";

/** Asistan tab: Çeliknaz chat with streaming answers, action chips and the decision timeline. */
export function renderAssistant(main: HTMLElement, session: HomeSession): () => void {
  const a = tr.home.assistant;
  let unsubscribe: (() => void) | undefined;
  let disposed = false;

  const emptyState = el("div", { class: "ev-empty ev-empty--chat" }, [
    el("span", { class: "ev-empty__icon ev-empty__icon--brand" }, [icon("sparkle", "icon icon--xl")]),
    el("h3", { class: "ev-empty__title" }, [a.chatEmptyTitle]),
    el("p", { class: "ev-empty__body" }, [a.chatEmptyBody]),
  ]);
  const examples = el("ul", { class: "ev-examples" });
  emptyState.append(examples);

  const list = el("div", { class: "chat-list", role: "log", "aria-live": "polite", "aria-label": a.chatTitle });
  const status = el("div", { class: "chat-status" });

  const input = el("input", {
    id: "assistant-input",
    class: "input",
    type: "text",
    autocomplete: "off",
    placeholder: a.inputPlaceholder,
    enterkeyhint: "send",
  });
  const sendButton = el("button", { type: "submit", class: "button button--primary", "aria-label": a.send }, [icon("send")]);
  const voice = el("button", { type: "button", class: "ev-icon-button ev-chat__mic", "aria-label": tr.home.voice.button }, [icon("mic")]);
  const form = el("form", { class: "ev-chat__form" }, [
    el("label", { class: "visually-hidden", for: "assistant-input" }, [a.inputLabel]),
    input,
    sendButton,
    voice,
  ]);

  main.replaceChildren(
    el("div", { class: "ev-screen ev-screen--chat" }, [
      el("div", { class: "ev-screen__head" }, [el("h1", { class: "ev-screen__title" }, [icon("sparkle", "icon ev-brand-icon"), a.title])]),
      el("section", { class: "ev-panel ev-panel--compact", "aria-labelledby": "assistant-suggestions" }, [
        el("h2", { id: "assistant-suggestions", class: "ev-panel__title" }, [a.suggestionsTitle]),
        el("p", { class: "ev-muted" }, [a.suggestionsEmpty]),
      ]),
      el("section", { class: "ev-chat", "aria-labelledby": "assistant-chat" }, [
        el("h2", { id: "assistant-chat", class: "visually-hidden" }, [a.chatTitle]),
        emptyState,
        list,
        status,
      ]),
      form,
    ]),
  );

  const scrollToEnd = (): void => {
    list.lastElementChild?.scrollIntoView({ block: "end", behavior: "smooth" });
  };

  const send = (controller: ChatController, text: string): void => {
    const value = text.trim();
    if (value === "") return;
    void controller.send(value, "text");
    input.value = "";
    scrollToEnd();
  };

  const wire = (controller: ChatController): void => {
    const draw = createEntryList(list, {
      store: session.store,
      loadRun: (runId) => controller.loadRun(runId),
    });
    let count = 0;
    const update = (): void => {
      const { entries } = controller.snapshot;
      emptyState.hidden = entries.length > 0;
      draw(entries);
      const running = entries.some((e) => e.role === "assistant" && e.status === "running");
      sendButton.disabled = running;
      if (entries.length !== count) {
        count = entries.length;
        scrollToEnd();
      }
    };
    unsubscribe = controller.subscribe(update);
    update();

    examples.replaceChildren(
      ...a.examples.map((example) => {
        const button = el("button", { type: "button", class: "ev-examples__item" }, [`“${example}”`]);
        button.addEventListener("click", () => {
          send(controller, example);
        });
        return el("li", {}, [button]);
      }),
    );

    form.addEventListener("submit", (event) => {
      event.preventDefault();
      if (!sendButton.disabled) send(controller, input.value);
    });
  };

  const start = (): void => {
    renderLoading(status, tr.home.chat.connecting);
    // Appliance names for chips and the default member come from the home.
    void session.ensureLoaded().catch(() => undefined);
    void session
      .chat()
      .then(async (controller) => {
        await controller.start();
        return controller;
      })
      .then(
        (controller) => {
          if (disposed) return;
          status.replaceChildren();
          wire(controller);
        },
        () => {
          if (disposed) return;
          const retry = el("button", { type: "button", class: "button button--secondary button--sm" }, [tr.home.chat.retry]);
          retry.addEventListener("click", start);
          status.replaceChildren(el("div", { class: "alert alert--danger", role: "alert" }, [el("p", {}, [tr.home.chat.startError]), retry]));
        },
      );
  };

  form.addEventListener("submit", (event) => {
    event.preventDefault();
  });
  voice.addEventListener("click", () => {
    const host = main.closest<HTMLElement>(".ev-app") ?? main;
    openVoiceOverlay(host, voice, session);
  });

  start();
  return () => {
    disposed = true;
    unsubscribe?.();
  };
}
