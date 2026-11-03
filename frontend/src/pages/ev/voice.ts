// Floating voice button overlay: Web Speech API (tr-TR) with a typed fallback.
// Slice 1 does not send anything to the backend yet; it only echoes the request.

import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import { tr } from "../../i18n/tr.ts";

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
export function openVoiceOverlay(host: HTMLElement, opener: HTMLElement): void {
  if (openDialog?.open) return;

  const status = el("p", { class: "voice__status" }, [tr.home.voice.hint]);
  const transcript = el("p", { class: "voice__transcript", "aria-live": "polite" });
  const reply = el("div", { class: "voice__reply", hidden: true });
  const pulse = el("div", { class: "voice__pulse", "aria-hidden": "true" }, [icon("mic", "icon voice__mic")]);

  const toggle = el("button", { type: "button", class: "button button--secondary button--sm" });
  const input = el("input", {
    class: "input",
    id: "voice-typed",
    type: "text",
    autocomplete: "off",
    placeholder: tr.home.voice.typedPlaceholder,
  });
  const form = el("form", { class: "voice__form" }, [
    el("label", { class: "field__label", for: "voice-typed" }, [tr.home.voice.typedLabel]),
    el("div", { class: "voice__form-row" }, [
      input,
      el("button", { type: "submit", class: "button button--primary", "aria-label": tr.home.voice.send }, [icon("send")]),
    ]),
  ]);
  const close = el("button", { type: "button", class: "ev-icon-button voice__close", "aria-label": tr.home.voice.close }, [icon("close")]);

  const dialog = el("dialog", { class: "voice", "aria-labelledby": "voice-title" }, [
    el("div", { class: "voice__sheet" }, [
      el("div", { class: "voice__top" }, [el("h2", { id: "voice-title", class: "voice__title" }, [tr.home.voice.title]), close]),
      pulse,
      status,
      transcript,
      toggle,
      reply,
      form,
    ]),
  ]);

  const Ctor = recognitionCtor();
  let recognition: Recognition | undefined;
  let listening = false;

  const setListening = (value: boolean): void => {
    listening = value;
    dialog.classList.toggle("voice--listening", value);
    toggle.replaceChildren(icon(value ? "stop" : "mic", "icon icon--sm"), value ? tr.home.voice.stop : tr.home.voice.restart);
    if (value) status.textContent = tr.home.voice.listening;
  };

  const answer = (text: string): void => {
    if (text === "") return;
    reply.hidden = false;
    reply.replaceChildren(
      el("p", { class: "voice__bubble voice__bubble--user" }, [text]),
      el("p", { class: "voice__bubble voice__bubble--assistant" }, [`${tr.home.voice.soon} “${text}”`]),
    );
  };

  const start = (): void => {
    if (!Ctor) return;
    recognition?.abort();
    const r = new Ctor();
    r.lang = "tr-TR";
    r.interimResults = true;
    r.continuous = false;
    r.onresult = (event) => {
      const { text, final } = joinTranscript(event);
      transcript.textContent = text;
      if (final) answer(text);
    };
    r.onerror = (event) => {
      status.textContent = event.error === "not-allowed" || event.error === "service-not-allowed" ? tr.home.voice.denied : tr.home.voice.error;
    };
    r.onend = () => {
      setListening(false);
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
    answer(text);
    input.value = "";
  });

  const finish = (): void => {
    recognition?.abort();
    recognition = undefined;
    dialog.remove();
    openDialog = undefined;
    if (opener.isConnected) opener.focus();
  };
  close.addEventListener("click", () => {
    dialog.close();
  });
  dialog.addEventListener("close", finish);
  dialog.addEventListener("click", (event) => {
    // Click on the backdrop closes the sheet.
    if (event.target === dialog) dialog.close();
  });

  host.append(dialog);
  openDialog = dialog;
  dialog.showModal();

  if (Ctor) {
    start();
  } else {
    status.textContent = tr.home.voice.unsupported;
    toggle.hidden = true;
    input.focus();
  }
}
