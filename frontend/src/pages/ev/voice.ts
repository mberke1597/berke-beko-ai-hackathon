// Floating voice button overlay: Web Speech API (tr-TR) with a typed fallback. The final transcript is
// sent to Çeliknaz (channel "voice"); the short answer streams into the overlay and is read aloud.

import { el } from "../../components/dom.ts";
import { findByLocalId } from "../../home/chat/reducer.ts";
import { canSpeak, isMuted, setMuted, speak, stopSpeaking } from "../../home/chat/speech.ts";
import { safeStore } from "../../home/chat/storage.ts";
import type { Channel } from "../../home/chat/types.ts";
import { icon } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { renderEntry } from "./chat-view.ts";

// Minimal typing for the Web Speech API (not part of the standard DOM lib everywhere).
interface SpeechAlternative {
  transcript: string;
}
interface SpeechResult {
  readonly isFinal: boolean;
  readonly length: number;
  [index: number]: SpeechAlternative | undefined;
}
interface SpeechResultEvent {
  readonly resultIndex: number;
  readonly results: { readonly length: number; [index: number]: SpeechResult | undefined };
}
interface SpeechErrorEvent {
  readonly error: string;
}
interface Recognition {
  lang: string;
  interimResults: boolean;
  continuous: boolean;
  onresult: ((event: SpeechResultEvent) => void) | null;
  onerror: ((event: SpeechErrorEvent) => void) | null;
  onend: (() => void) | null;
  start(): void;
  stop(): void;
  abort(): void;
}
type RecognitionCtor = new () => Recognition;

function recognitionCtor(): RecognitionCtor | undefined {
  const w = window as unknown as { SpeechRecognition?: RecognitionCtor; webkitSpeechRecognition?: RecognitionCtor };
  return w.SpeechRecognition ?? w.webkitSpeechRecognition;
}

/** Joins final and interim speech results into one transcript string. */
export function joinTranscript(event: SpeechResultEvent): { text: string; final: boolean } {
  let text = "";
  let final = true;
  for (const result of Array.from(event.results)) {
    if (!result) continue;
    text += result[0]?.transcript ?? "";
    if (!result.isFinal) final = false;
  }
  return { text: text.trim(), final };
}

let openDialog: HTMLDialogElement | undefined;

/** Opens the listening overlay inside `host`; focus returns to `opener` when it closes. */
export function openVoiceOverlay(host: HTMLElement, opener: HTMLElement, session: HomeSession): void {
  if (openDialog?.open) return;
  const store = safeStore();
  let muted = isMuted(store);
  let disposed = false;
  let unsubscribe: (() => void) | undefined;

  const status = el("p", { class: "voice__status", role: "status" }, [tr.home.voice.hint]);
  const transcript = el("p", { class: "voice__transcript", "aria-live": "polite" });
  const reply = el("div", { class: "voice__reply", "aria-live": "polite", hidden: true });
  const pulse = el("div", { class: "voice__pulse", "aria-hidden": "true" }, [icon("mic", "icon voice__mic")]);

  const toggle = el("button", { type: "button", class: "button button--secondary button--sm" });
  const muteButton = el("button", { type: "button", class: "ev-icon-button voice__mute" });
  const renderMute = (): void => {
    muteButton.setAttribute("aria-pressed", String(muted));
    muteButton.setAttribute("aria-label", muted ? tr.home.voice.unmute : tr.home.voice.mute);
    muteButton.replaceChildren(icon(muted ? "speakerOff" : "speaker"));
  };
  renderMute();
  muteButton.hidden = !canSpeak();
  muteButton.addEventListener("click", () => {
    muted = !muted;
    setMuted(store, muted);
    if (muted) stopSpeaking();
    renderMute();
  });

  const input = el("input", {
    class: "input",
    id: "voice-typed",
    type: "text",
    autocomplete: "off",
    placeholder: tr.home.voice.typedPlaceholder,
  });
  const sendButton = el("button", { type: "submit", class: "button button--primary", "aria-label": tr.home.voice.send }, [icon("send")]);
  const form = el("form", { class: "voice__form" }, [
    el("label", { class: "field__label", for: "voice-typed" }, [tr.home.voice.typedLabel]),
    el("div", { class: "voice__form-row" }, [input, sendButton]),
  ]);
  const close = el("button", { type: "button", class: "ev-icon-button voice__close", "aria-label": tr.home.voice.close }, [icon("close")]);
  const toChat = el("a", { class: "voice__link", href: hrefFor(paths.evAssistant), hidden: true }, [tr.home.voice.openChat]);

  const dialog = el("dialog", { class: "voice", "aria-labelledby": "voice-title" }, [
    el("div", { class: "voice__sheet" }, [
      el("div", { class: "voice__top" }, [
        el("h2", { id: "voice-title", class: "voice__title" }, [icon("sparkle", "icon ev-brand-icon"), tr.home.voice.title]),
        el("div", { class: "voice__top-actions" }, [muteButton, close]),
      ]),
      pulse,
      status,
      transcript,
      toggle,
      reply,
      toChat,
      form,
    ]),
  ]);

  const Ctor = recognitionCtor();
  let recognition: Recognition | undefined;
  let listening = false;
  let busy = false;

  const setListening = (value: boolean): void => {
    listening = value;
    dialog.classList.toggle("voice--listening", value);
    toggle.replaceChildren(icon(value ? "stop" : "mic", "icon icon--sm"), value ? tr.home.voice.stop : tr.home.voice.restart);
    if (value) status.textContent = tr.home.voice.listening;
  };

  /** Sends to Çeliknaz and shows the streaming answer here, without leaving the current tab. */
  const ask = (text: string, channel: Channel): void => {
    if (text === "" || busy) return;
    busy = true;
    sendButton.disabled = true;
    toggle.disabled = true;
    status.textContent = tr.home.voice.thinking;
    stopSpeaking();
    reply.hidden = false;
    void session.chat().then((controller) => {
      if (disposed) return;
      const { localId, done } = controller.sendWithId(text, channel);
      const draw = (): void => {
        const entry = findByLocalId(controller.snapshot, localId);
        const userEntry = controller.snapshot.entries.find((e) => e.id === `local:${localId}`);
        reply.replaceChildren(
          ...(userEntry ? [renderEntry(userEntry, { store: session.store }, new Set())] : []),
          ...(entry ? [renderEntry(entry, { store: session.store }, new Set())] : []),
        );
      };
      unsubscribe?.();
      unsubscribe = controller.subscribe(draw);
      draw();
      void done.then((entry) => {
        busy = false;
        sendButton.disabled = false;
        toggle.disabled = false;
        if (disposed) return;
        status.textContent = "";
        toChat.hidden = false;
        if (!muted && entry.text !== "") {
          const speaking = speak(entry.text, () => {
            dialog.classList.remove("voice--speaking");
          });
          if (speaking) {
            dialog.classList.add("voice--speaking");
            status.textContent = tr.home.voice.speaking;
          }
        }
      });
    });
  };

  const start = (): void => {
    if (!Ctor || busy) return;
    stopSpeaking();
    recognition?.abort();
    const r = new Ctor();
    r.lang = "tr-TR";
    r.interimResults = true;
    r.continuous = false;
    let finalText = "";
    r.onresult = (event) => {
      const { text, final } = joinTranscript(event);
      transcript.textContent = text;
      if (final) finalText = text;
    };
    r.onerror = (event) => {
      status.textContent = event.error === "not-allowed" || event.error === "service-not-allowed" ? tr.home.voice.denied : tr.home.voice.error;
    };
    r.onend = () => {
      setListening(false);
      if (finalText !== "") ask(finalText, "voice");
    };
    recognition = r;
    try {
      r.start();
      setListening(true);
    } catch {
      status.textContent = tr.home.voice.error;
      setListening(false);
    }
  };

  toggle.addEventListener("click", () => {
    if (listening) recognition?.stop();
    else start();
  });

  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const text = input.value.trim();
    if (text === "") return;
    transcript.textContent = text;
    input.value = "";
    ask(text, "text");
  });

  const finish = (): void => {
    disposed = true;
    recognition?.abort();
    recognition = undefined;
    unsubscribe?.();
    stopSpeaking();
    dialog.remove();
    openDialog = undefined;
    if (opener.isConnected) opener.focus();
  };
  close.addEventListener("click", () => {
    dialog.close();
  });
  toChat.addEventListener("click", () => {
    dialog.close();
  });
  dialog.addEventListener("close", finish);
  dialog.addEventListener("click", (event) => {
    if (event.target === dialog) dialog.close();
  });

  host.append(dialog);
  openDialog = dialog;
  dialog.showModal();
  // Start the conversation early so the first answer is fast.
  void session
    .chat()
    .then((c) => c.start())
    .catch(() => undefined);

  if (Ctor) {
    start();
  } else {
    status.textContent = tr.home.voice.unsupported;
    toggle.hidden = true;
    input.focus();
  }
}
