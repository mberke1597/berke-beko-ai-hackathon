import { api, type ApiClient, type RequestOptions } from "../../api/client.ts";
import { ApiError } from "../../api/errors.ts";
import type { KeyValueStore } from "./storage.ts";
import { STORAGE_KEYS } from "./storage.ts";
import {
  CELIKNAZ_AGENT_ID,
  decodeConversationCreated,
  decodeMessageAccepted,
  decodeMessages,
  decodeRunDetail,
  type Channel,
  type ChatMessage,
  type ConversationCreated,
  type MessageAccepted,
  type RunDetail,
} from "./types.ts";

/** Chat endpoints of Phase 2. Implemented by the real API and by the mock agent. */
export interface ChatApi {
  createConversation(agentId: string, memberId: string, options?: RequestOptions): Promise<ConversationCreated>;
  getMessages(conversationId: string, options?: RequestOptions): Promise<ChatMessage[]>;
  sendMessage(conversationId: string, text: string, channel: Channel, options?: RequestOptions): Promise<MessageAccepted>;
  getRun(runId: string, options?: RequestOptions): Promise<RunDetail>;
}

const conv = (id: string): string => `/api/conversations/${encodeURIComponent(id)}`;

export function createHttpChatApi(client: ApiClient = api): ChatApi {
  return {
    createConversation: (agentId, memberId, options) =>
      client.post("/api/conversations", { agentId, memberId }, decodeConversationCreated, options),
    getMessages: (id, options) => client.get(`${conv(id)}/messages`, decodeMessages, options),
    sendMessage: (id, text, channel, options) => client.post(`${conv(id)}/messages`, { text, channel }, decodeMessageAccepted, options),
    getRun: (runId, options) => client.get(`/api/runs/${encodeURIComponent(runId)}`, decodeRunDetail, options),
  };
}

export interface Bootstrap {
  conversationId: string;
  messages: ChatMessage[];
}

/**
 * Reuses the conversation id from sessionStorage, or creates one (agent "celiknaz").
 * A stored id the server no longer knows (404, e.g. after a backend restart) is replaced.
 */
export async function bootstrapConversation(chat: ChatApi, store: KeyValueStore, memberId: string): Promise<Bootstrap> {
  const stored = store.get(STORAGE_KEYS.conversationId);
  if (stored !== undefined && stored !== "") {
    try {
      return { conversationId: stored, messages: await chat.getMessages(stored) };
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 404)) throw error;
      store.remove(STORAGE_KEYS.conversationId);
    }
  }
  const created = await chat.createConversation(CELIKNAZ_AGENT_ID, memberId);
  store.set(STORAGE_KEYS.conversationId, created.conversationId);
  return { conversationId: created.conversationId, messages: [] };
}
