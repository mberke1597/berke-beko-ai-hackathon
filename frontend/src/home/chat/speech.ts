// Spoken answers (speechSynthesis, tr-TR) and the per-tab mute setting.

import { STORAGE_KEYS, type KeyValueStore } from "./storage.ts";

export function isMuted(store: KeyValueStore): boolean {
  return store.get(STORAGE_KEYS.voiceMuted) === "1";
}

export function setMuted(store: KeyValueStore, muted: boolean): void {
  store.set(STORAGE_KEYS.voiceMuted, muted ? "1" : "0");
}

function synth(): SpeechSynthesis | undefined {
  try {
    return typeof window !== "undefined" && "speechSynthesis" in window ? window.speechSynthesis : undefined;
  } catch {
    return undefined;
  }
}

export function canSpeak(): boolean {
  return synth() !== undefined && typeof SpeechSynthesisUtterance !== "undefined";
}

/** Reads `text` aloud in Turkish where the browser supports it. Returns false when it cannot. */
export function speak(text: string, onEnd?: () => void): boolean {
  const s = synth();
  if (!s || text.trim() === "" || typeof SpeechSynthesisUtterance === "undefined") return false;
  try {
    s.cancel();
    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = "tr-TR";
    const voice = s.getVoices().find((v) => v.lang.toLowerCase().startsWith("tr"));
    if (voice) utterance.voice = voice;
    if (onEnd) {
      utterance.onend = onEnd;
      utterance.onerror = onEnd;
    }
    s.speak(utterance);
    return true;
  } catch {
    return false;
  }
}

export function stopSpeaking(): void {
  try {
    synth()?.cancel();
  } catch {
    // ignore
  }
}
