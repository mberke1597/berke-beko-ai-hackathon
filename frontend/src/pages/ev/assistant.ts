import { commandErrorMessage } from "../../home/api.ts";
import { el } from "../../components/dom.ts";
import { formatKwh, formatTl } from "../../home/slice2/logic.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { showToast } from "./toast.ts";
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

  const suggestions = el("div", { class: "ev-stack" });
  const energyLine = el("p", { class: "ev-energyline", hidden: true });

  /** Unread suggestion notifications as insight cards with Evet / Hayır. */
  const drawSuggestions = (): void => {
    const items = session.s2.notifications.filter((n) => n.kind === "suggestion" && n.action !== null && !n.read).slice(0, 2);
    if (items.length === 0) {
      suggestions.replaceChildren(el("p", { class: "ev-muted" }, [a.suggestionsEmpty]));
      return;
    }
    suggestions.replaceChildren(
      ...items.map((n) => {
        const routineId = n.action?.routineId ?? "";
        const decide = (accept: boolean, buttons: HTMLButtonElement[]): void => {
          for (const b of buttons) b.disabled = true;
          void session.backend().then(async (b) => {
            try {
              if (accept) await b.slice2.acceptRoutine(routineId);
              else await b.slice2.rejectRoutine(routineId);
              showToast(a.suggestionDone, "success");
              await session.s2.markRead(n.id);
            } catch (error) {
              for (const button of buttons) button.disabled = false;
              showToast(commandErrorMessage(error), "error");
            }
          });
        };
        const yes = el("button", { type: "button", class: "button button--primary button--sm" }, [a.yes]);
        const no = el("button", { type: "button", class: "button button--secondary button--sm" }, [a.no]);
        yes.addEventListener("click", () => {
          decide(true, [yes, no]);
        });
        no.addEventListener("click", () => {
          decide(false, [yes, no]);
        });
        return el("article", { class: "ev-insight" }, [el("p", { class: "ev-insight__text" }, [n.text]), el("div", { class: "ev-actions" }, [yes, no])]);
      }),
    );
  };

  main.replaceChildren(
    el("div", { class: "ev-screen ev-screen--chat" }, [
      el("div", { class: "ev-screen__head" }, [el("h1", { class: "ev-screen__title" }, [icon("sparkle", "icon ev-brand-icon"), a.title])]),
      el("section", { class: "ev-panel ev-panel--compact", "aria-labelledby": "assistant-suggestions" }, [
        el("h2", { id: "assistant-suggestions", class: "ev-panel__title" }, [a.suggestionsTitle]),
        suggestions,
        energyLine,
      ]),
      el("a", { class: "ev-link-row ev-link-row--link", href: hrefFor(paths.evLearning) }, [
        icon("sparkle", "icon icon--sm"),
        el("span", {}, [a.learned]),
        icon("chevron", "icon icon--sm ev-link-row__chevron"),
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

  drawSuggestions();
  const unsubscribeS2 = session.s2.subscribe((change) => {
    if (change.kind === "notifications" || change.kind === "member") drawSuggestions();
  });
  void session.backend().then(
    async (b) => {
      try {
        const energy = await b.slice2.getEnergy();
        if (disposed) return;
        energyLine.hidden = false;
        energyLine.replaceChildren(icon("leaf", "icon icon--sm"), a.energyWeek(formatKwh(energy.savedWeekKwh), formatTl(energy.savedWeekTl)));
      } catch {
        // The energy line is optional.
      }
    },
    () => undefined,
  );

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
    unsubscribeS2();
  };
}
