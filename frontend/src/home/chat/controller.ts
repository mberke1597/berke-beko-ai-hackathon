// App-wide Çeliknaz chat: conversation bootstrap, RunHub stream, sends from the Asistan tab and the voice overlay.

import type { LiveStatus } from "../live.ts";
import type { ChatApi } from "./api.ts";
import { bootstrapConversation } from "./api.ts";
import { emptyChat, findAssistant, findByLocalId, reduceChat, type AssistantEntry, type ChatEvent, type ChatState } from "./reducer.ts";
import type { ConnectRunLive, RunLiveConnection } from "./run-live.ts";
import type { KeyValueStore } from "./storage.ts";
import type { Channel, ChatAction, RunEvent } from "./types.ts";

export interface ChatControllerDeps {
  chat: ChatApi;
  connectRunLive: ConnectRunLive;
  store: KeyValueStore;
  memberId: () => string;
  /** Called once per finished run with its accepted actions (Evim "Çeliknaz ayarladı" tags). */
  onActions?: (actions: ChatAction[]) => void;
  /** A run with no event for this long is shown as failed. */
  idleTimeoutMs?: number;
}

export type ChatListener = (state: ChatState) => void;

interface Waiter {
  resolve: (entry: AssistantEntry) => void;
  timer: ReturnType<typeof setTimeout> | undefined;
  runId: string | undefined;
}

let localCounter = 0;

export class ChatController {
  private state: ChatState = emptyChat;
  private readonly listeners = new Set<ChatListener>();
  private starting: Promise<void> | undefined;
  private connection: RunLiveConnection | undefined;
  private conversationId: string | undefined;
  private readonly waiters = new Map<string, Waiter>();
  private readonly finishedRuns = new Set<string>();
  private liveStatus: LiveStatus = "disconnected";
  private readonly idleTimeoutMs: number;

  constructor(private readonly deps: ChatControllerDeps) {
    this.idleTimeoutMs = deps.idleTimeoutMs ?? 45_000;
  }

  get snapshot(): ChatState {
    return this.state;
  }

  get live(): LiveStatus {
    return this.liveStatus;
  }

  get ready(): boolean {
    return this.conversationId !== undefined;
  }

  subscribe(listener: ChatListener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  /** Creates or restores the conversation, loads history and joins RunHub. Safe to call many times. */
  start(): Promise<void> {
    this.starting ??= this.doStart().catch((error: unknown) => {
      this.starting = undefined;
      throw error;
    });
    return this.starting;
  }

  private async doStart(): Promise<void> {
    const { conversationId, messages } = await bootstrapConversation(this.deps.chat, this.deps.store, this.deps.memberId());
    this.conversationId = conversationId;
    this.dispatch({ kind: "history", messages });
    this.connection?.stop();
    this.connection = this.deps.connectRunLive(conversationId, {
      event: (event) => {
        this.onRunEvent(event);
      },
      status: (status) => {
        this.liveStatus = status;
        this.notify();
      },
    });
    // Without the hub the chat still works through the recovery path in `timeout`.
    await this.connection.ready.catch(() => undefined);
  }

  /** Sends a message; resolves with the finished (or failed) assistant entry. */
  send(text: string, channel: Channel): Promise<AssistantEntry> {
    return this.sendWithId(text, channel).done;
  }

  /** Like `send`, but also returns the local id so a view can follow the streaming entry. */
  sendWithId(text: string, channel: Channel): { localId: string; done: Promise<AssistantEntry> } {
    const localId = `${String(Date.now())}-${String(++localCounter)}`;
    this.dispatch({ kind: "sent", localId, text, channel });
    const done = new Promise<AssistantEntry>((resolve) => {
      this.waiters.set(localId, { resolve, timer: undefined, runId: undefined });
    });
    void this.post(localId, text, channel);
    return { localId, done };
  }

  private async post(localId: string, text: string, channel: Channel): Promise<void> {
    try {
      await this.start();
      const conversationId = this.conversationId;
      if (conversationId === undefined) throw new Error("No conversation");
      this.armTimeout(localId);
      const accepted = await this.deps.chat.sendMessage(conversationId, text, channel);
      const waiter = this.waiters.get(localId);
      if (waiter) waiter.runId = accepted.runId;
      this.dispatch({ kind: "accepted", localId, runId: accepted.runId });
      // RunCompleted may have arrived before the 202.
      if (this.finishedRuns.has(accepted.runId)) this.settle(localId);
    } catch {
      this.dispatch({ kind: "failed", localId });
      this.settle(localId);
    }
  }

  /** Loads steps and totals of a past run for the timeline. */
  async loadRun(runId: string): Promise<void> {
    const detail = await this.deps.chat.getRun(runId);
    this.dispatch({ kind: "runDetail", runId, steps: detail.steps, totals: detail.totals });
  }

  dispose(): void {
    this.connection?.stop();
    for (const w of this.waiters.values()) if (w.timer !== undefined) clearTimeout(w.timer);
    this.waiters.clear();
  }

  private onRunEvent(event: RunEvent): void {
    this.dispatch(event);
    const entry = findAssistant(this.state, event.runId);
    const localId = entry?.localId ?? undefined;
    if (localId !== undefined) {
      const waiter = this.waiters.get(localId);
      if (waiter) {
        waiter.runId = event.runId;
        this.armTimeout(localId);
      }
    }
    if (event.kind === "RunCompleted") {
      if (this.finishedRuns.has(event.runId)) return;
      this.finishedRuns.add(event.runId);
      const accepted = event.actions.filter((a) => a.accepted);
      if (accepted.length > 0) this.deps.onActions?.(accepted);
      if (localId !== undefined) this.settle(localId);
    }
  }

  private armTimeout(localId: string): void {
    const waiter = this.waiters.get(localId);
    if (!waiter) return;
    if (waiter.timer !== undefined) clearTimeout(waiter.timer);
    waiter.timer = setTimeout(() => {
      void this.timeout(localId);
    }, this.idleTimeoutMs);
  }

  /** No events for too long: try to recover the answer from history, otherwise show the fallback. */
  private async timeout(localId: string): Promise<void> {
    const runId = this.waiters.get(localId)?.runId;
    if (runId !== undefined && this.conversationId !== undefined) {
      try {
        const messages = await this.deps.chat.getMessages(this.conversationId);
        const answer = messages.find((m) => m.role === "assistant" && m.runId === runId);
        if (answer) {
          this.onRunEvent({ kind: "RunCompleted", runId, text: answer.text, actions: answer.actions, totals: null });
          return;
        }
      } catch {
        // fall through to the fallback answer
      }
    }
    this.dispatch(runId === undefined ? { kind: "failed", localId } : { kind: "failed", runId, localId });
    this.settle(localId);
  }

  private settle(localId: string): void {
    const waiter = this.waiters.get(localId);
    if (!waiter) return;
    if (waiter.timer !== undefined) clearTimeout(waiter.timer);
    this.waiters.delete(localId);
    const entry = findByLocalId(this.state, localId) ?? (waiter.runId !== undefined ? findAssistant(this.state, waiter.runId) : undefined);
    waiter.resolve(
      entry ?? {
        role: "assistant",
        id: `pending:${localId}`,
        localId,
        runId: waiter.runId ?? null,
        text: "",
        status: "failed",
        actions: [],
        steps: [],
        totals: null,
        fromHistory: false,
      },
    );
  }

  private dispatch(event: ChatEvent): void {
    this.state = reduceChat(this.state, event);
    this.notify();
  }

  private notify(): void {
    for (const listener of [...this.listeners]) listener(this.state);
  }
}
