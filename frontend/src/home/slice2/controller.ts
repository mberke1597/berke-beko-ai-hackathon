// App-wide slice 2 state: current member, settings, home context, notifications and tasks.
// Screens subscribe to it; live hub events (TaskAssigned, NotificationCreated, ComfortCheckRequested) are ingested here.

import type { KeyValueStore } from "../chat/storage.ts";
import { shouldAskComfort, upsertNotification, upsertTask, unreadCount } from "./logic.ts";
import type { Slice2Api } from "./api.ts";
import type { AppNotification, ComfortCheck, HomeContext, HouseholdTask, Settings, SettingsPatch } from "./types.ts";

export const MEMBER_STORAGE_KEY = "celiknaz.memberId";
export const DEFAULT_MEMBER = "ayse";

export type S2Change =
  | { kind: "member" }
  | { kind: "settings" }
  | { kind: "context" }
  | { kind: "notifications"; created?: AppNotification }
  | { kind: "tasks"; assigned?: HouseholdTask }
  | { kind: "comfortCheck"; check: ComfortCheck };

export type S2Listener = (change: S2Change) => void;

export interface Slice2Deps {
  api: () => Promise<Slice2Api>;
  storage: KeyValueStore;
  /** Called after the demo member changed (the chat starts a new conversation for the new member). */
  onMemberChange?: (memberId: string) => void;
}

export class Slice2Controller {
  private member: string;
  private currentSettings: Settings | undefined;
  private currentContext: HomeContext | undefined;
  private list: AppNotification[] = [];
  private taskList: HouseholdTask[] = [];
  private tasksReady = false;
  private readonly listeners = new Set<S2Listener>();

  constructor(private readonly deps: Slice2Deps) {
    this.member = deps.storage.get(MEMBER_STORAGE_KEY) ?? DEFAULT_MEMBER;
  }

  get memberId(): string {
    return this.member;
  }

  get settings(): Settings | undefined {
    return this.currentSettings;
  }

  get context(): HomeContext | undefined {
    return this.currentContext;
  }

  get notifications(): readonly AppNotification[] {
    return this.list;
  }

  get unread(): number {
    return unreadCount(this.list);
  }

  get tasks(): readonly HouseholdTask[] {
    return this.taskList;
  }

  get tasksLoaded(): boolean {
    return this.tasksReady;
  }

  subscribe(listener: S2Listener): () => void {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  }

  private emit(change: S2Change): void {
    for (const listener of [...this.listeners]) listener(change);
  }

  private async api(): Promise<Slice2Api> {
    return this.deps.api();
  }

  /** Drops a stored member id that is not part of the home (e.g. after the demo data changed). */
  validateMember(knownIds: readonly string[]): void {
    if (knownIds.length > 0 && !knownIds.includes(this.member)) this.applyMember(knownIds.includes(DEFAULT_MEMBER) ? DEFAULT_MEMBER : (knownIds[0] ?? DEFAULT_MEMBER));
  }

  /** Demo member switcher: all calls and the chat use the new member from now on. */
  setMember(memberId: string): void {
    if (memberId === this.member) return;
    this.applyMember(memberId);
  }

  private applyMember(memberId: string): void {
    this.member = memberId;
    this.deps.storage.set(MEMBER_STORAGE_KEY, memberId);
    this.currentSettings = undefined;
    this.list = [];
    this.emit({ kind: "member" });
    this.deps.onMemberChange?.(memberId);
    void this.loadNotifications().catch(() => undefined);
    void this.loadSettings().catch(() => undefined);
  }

  // ---------- settings and context ----------

  async loadSettings(): Promise<Settings> {
    const memberId = this.member;
    const settings = await (await this.api()).getSettings(memberId);
    if (memberId === this.member) {
      this.currentSettings = settings;
      this.emit({ kind: "settings" });
    }
    return settings;
  }

  /** Applies the change locally first, then saves it; reloads the server state if saving fails. */
  async updateSettings(patch: SettingsPatch): Promise<void> {
    const before = this.currentSettings;
    if (before) {
      this.currentSettings = { ...before, ...patch };
      this.emit({ kind: "settings" });
    }
    try {
      await (await this.api()).putSettings(this.member, patch);
      if (patch.priorityMode !== undefined) await this.refreshContext().catch(() => undefined);
    } catch (error) {
      await this.loadSettings().catch(() => undefined);
      throw error;
    }
  }

  async refreshContext(): Promise<HomeContext> {
    const context = await (await this.api()).getContext();
    this.currentContext = context;
    this.emit({ kind: "context" });
    return context;
  }

  // ---------- notifications ----------

  async loadNotifications(): Promise<void> {
    const memberId = this.member;
    const items = await (await this.api()).listNotifications(memberId);
    if (memberId !== this.member) return;
    this.list = items;
    this.emit({ kind: "notifications" });
  }

  async markRead(id: string): Promise<void> {
    const item = this.list.find((n) => n.id === id);
    if (!item || item.read) return;
    this.list = upsertNotification(this.list, { ...item, read: true });
    this.emit({ kind: "notifications" });
    try {
      await (await this.api()).markNotificationRead(id);
    } catch (error) {
      this.list = upsertNotification(this.list, item);
      this.emit({ kind: "notifications" });
      throw error;
    }
  }

  // ---------- tasks ----------

  /** Loads open, done and skipped tasks. */
  async loadTasks(): Promise<void> {
    const api = await this.api();
    const [open, done, skipped] = await Promise.all([api.listTasks("open"), api.listTasks("done"), api.listTasks("skipped")]);
    this.taskList = [...open, ...done, ...skipped];
    this.tasksReady = true;
    this.emit({ kind: "tasks" });
  }

  /** Stores a changed task (action result or live event). */
  applyTask(task: HouseholdTask): void {
    this.taskList = upsertTask(this.taskList, task);
    this.emit({ kind: "tasks" });
  }

  // ---------- live events ----------

  ingestTaskAssigned(task: HouseholdTask): void {
    this.taskList = upsertTask(this.taskList, task);
    this.emit({ kind: "tasks", assigned: task });
  }

  /** Only the current member's notifications are shown. */
  ingestNotification(notification: AppNotification): void {
    if (notification.memberId !== "" && notification.memberId !== this.member) return;
    this.list = upsertNotification(this.list, notification);
    this.emit({ kind: "notifications", created: notification });
  }

  ingestComfortCheck(check: ComfortCheck): void {
    if (shouldAskComfort(check, this.member)) this.emit({ kind: "comfortCheck", check });
  }
}
