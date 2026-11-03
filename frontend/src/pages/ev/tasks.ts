import { commandErrorMessage } from "../../home/api.ts";
import { el, setChildren } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import {
  avatarSlot,
  fairnessBars,
  filterTasks,
  formatClock,
  memberInitial,
  memberName,
  originLabel,
  type TaskFilter,
} from "../../home/slice2/logic.ts";
import type { HouseholdTask, TaskStat } from "../../home/slice2/types.ts";
import type { Member } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { hrefFor } from "../../router.ts";
import { loadHome, renderLoadError, renderLoading } from "./common.ts";
import { openSheet, sheetHost } from "./sheet.ts";
import { showToast } from "./toast.ts";

let selectedFilter: TaskFilter = "mine";

function avatar(members: readonly Member[], id: string): HTMLElement {
  const name = memberName(members, id);
  return el("span", { class: `ev-avatar ev-avatar--${String(avatarSlot(members, id))}`, "aria-hidden": "true", title: name }, [memberInitial(name)]);
}

function taskCard(
  task: HouseholdTask,
  members: readonly Member[],
  applianceName: string | undefined,
  actions: { done(): void; reassign(button: HTMLElement): void; skip(): void },
): HTMLElement {
  const open = task.status === "open";
  const assignee = memberName(members, task.assignedMemberId);
  const created = formatClock(task.createdAtUtc);
  const buttons = open
    ? el("div", { class: "ev-task__actions" }, [
        (() => {
          const b = el("button", { type: "button", class: "button button--primary button--sm", "data-key": `done-${task.id}` }, [icon("check", "icon icon--sm"), tr.home.tasks.actions.done]);
          b.addEventListener("click", () => {
            b.disabled = true;
            actions.done();
          });
          return b;
        })(),
        (() => {
          const b = el("button", { type: "button", class: "button button--secondary button--sm" }, [tr.home.tasks.actions.reassign]);
          b.addEventListener("click", () => {
            actions.reassign(b);
          });
          return b;
        })(),
        (() => {
          const b = el("button", { type: "button", class: "button button--ghost button--sm" }, [tr.home.tasks.actions.skip]);
          b.addEventListener("click", () => {
            b.disabled = true;
            actions.skip();
          });
          return b;
        })(),
      ])
    : el("p", { class: `ev-task__status ev-task__status--${task.status}` }, [
        icon(task.status === "done" ? "check" : "off", "icon icon--xs"),
        task.status === "done" ? tr.home.tasks.status.done : tr.home.tasks.status.skipped,
      ]);

  return el("article", { class: `ev-task ${open ? "" : "ev-task--closed"}`, "data-id": task.id }, [
    el("div", { class: "ev-task__head" }, [
      avatar(members, task.assignedMemberId),
      el("div", { class: "ev-task__main" }, [
        el("h3", { class: "ev-task__title" }, [task.title]),
        el("p", { class: "ev-task__assignee" }, [tr.home.tasks.assignedTo(assignee)]),
      ]),
      el("span", { class: "badge badge--neutral" }, [tr.home.tasks.points(String(task.effortPoints))]),
    ]),
    el("p", { class: "ev-task__origin" }, [originLabel(task, applianceName), created !== "" && ` · ${tr.home.tasks.createdAt(created)}`]),
    task.reason !== "" && el("p", { class: "ev-task__reason" }, [icon("sparkle", "icon icon--xs"), task.reason]),
    buttons,
  ]);
}

/** Görevler tab: task list with filters and actions, weekly fairness bars and the rules link. */
export function renderTasks(main: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.tasks;
  const region = el("div", { class: "ev-screen" });
  main.replaceChildren(region);
  let unsubscribe: (() => void) | undefined;
  let disposed = false;

  const start = (): void => {
    renderLoading(region, t.loading);
    void Promise.all([session.ensureLoaded(), session.s2.loadTasks()]).then(
      () => {
        if (!disposed) draw();
      },
      (error: unknown) => {
        if (!disposed) renderLoadError(region, error, start);
      },
    );
  };

  function draw(): void {
    const home = session.store.home;
    if (!home) return;
    const members = home.members;

    const add = el("button", { type: "button", class: "button button--secondary button--sm" }, [icon("plus", "icon icon--sm"), t.add]);
    add.addEventListener("click", () => {
      openAddSheet(add, session, members);
    });

    const list = el("div", { class: "ev-tasklist", "aria-live": "polite" });
    const chips = el("div", { class: "ev-chips", role: "group", "aria-label": t.filtersLabel });
    const filters: [TaskFilter, string][] = [
      ["mine", t.filters.mine],
      ["everyone", t.filters.everyone],
      ["done", t.filters.done],
    ];
    const buttons = filters.map(([id, label]) => {
      const b = el("button", { type: "button", class: "ev-chip", "aria-pressed": String(selectedFilter === id), "data-filter": id }, [label]);
      b.addEventListener("click", () => {
        selectedFilter = id;
        for (const other of buttons) other.setAttribute("aria-pressed", String(other === b));
        drawList();
      });
      return b;
    });
    chips.append(...buttons);

    const fairness = el("section", { class: "ev-panel", "aria-labelledby": "tasks-fairness" }, [el("h2", { id: "tasks-fairness", class: "ev-panel__title" }, [t.fairnessTitle])]);
    const fairnessBody = el("div", { class: "ev-fairness-body" });
    fairness.append(fairnessBody);

    const rules = el("a", { class: "ev-link-row ev-link-row--link", href: hrefFor(paths.evRules) }, [
      icon("tasks", "icon icon--sm"),
      el("span", {}, [t.rules]),
      icon("chevron", "icon icon--sm ev-link-row__chevron"),
    ]);

    region.replaceChildren(el("div", { class: "ev-screen__head" }, [el("h1", { class: "ev-screen__title" }, [t.title]), add]), chips, list, fairness, rules);

    const act = (call: () => Promise<HouseholdTask>): void => {
      call().then(
        (updated) => {
          session.s2.applyTask(updated);
        },
        (error: unknown) => {
          showToast(error instanceof Error ? commandErrorMessage(error) : t.actionError, "error");
          drawList();
        },
      );
    };

    function drawList(): void {
      const memberId = session.s2.memberId;
      const items = filterTasks(session.s2.tasks, selectedFilter, memberId);
      if (items.length === 0) {
        const text = t.empty[selectedFilter];
        list.replaceChildren(
          el("div", { class: "ev-empty" }, [
            el("span", { class: "ev-empty__icon" }, [icon("tasks", "icon icon--xl")]),
            el("h2", { class: "ev-empty__title" }, [text.title]),
            el("p", { class: "ev-empty__body" }, [text.body]),
          ]),
        );
        return;
      }
      list.replaceChildren(
        ...items.map((task) =>
          taskCard(task, members, task.applianceId === null ? undefined : session.store.getAppliance(task.applianceId)?.displayName, {
            done: () => {
              act(async () => (await session.backend()).slice2.completeTask(task.id));
            },
            skip: () => {
              act(async () => (await session.backend()).slice2.skipTask(task.id));
            },
            reassign: (button) => {
              openReassignSheet(button, members, task, (memberId) => {
                act(async () => (await session.backend()).slice2.reassignTask(task.id, memberId));
              });
            },
          }),
        ),
      );
    }

    function drawFairness(stats: readonly TaskStat[]): void {
      const bars = fairnessBars(stats, members);
      const any = bars.some((b) => b.points > 0);
      setChildren(fairnessBody, [
        el(
          "ul",
          { class: "ev-fairness" },
          bars.map((b) =>
            el("li", { class: "ev-fairness__row" }, [
              avatar(members, b.memberId),
              el("span", { class: "ev-fairness__name" }, [b.name]),
              el("span", { class: "ev-fairness__bar", role: "img", "aria-label": t.fairnessRow(String(b.points), String(b.done)) }, [
                el("span", { class: "ev-fairness__fill", style: `width: ${String(b.pct)}%` }),
              ]),
              el("span", { class: "ev-fairness__count" }, [String(b.points)]),
            ]),
          ),
        ),
        !any && el("p", { class: "ev-muted" }, [t.fairnessEmpty]),
      ]);
    }

    const loadStats = (): void => {
      void session.backend().then(
        async (b) => {
          const stats = await b.slice2.getTaskStats();
          if (!disposed && region.isConnected) drawFairness(stats);
        },
        () => undefined,
      );
    };

    drawList();
    loadStats();
    unsubscribe?.();
    unsubscribe = session.s2.subscribe((change) => {
      if (change.kind === "tasks") {
        drawList();
        loadStats();
      } else if (change.kind === "member") {
        drawList();
      }
    });
  }

  loadHome(session, region, start);
  return () => {
    disposed = true;
    unsubscribe?.();
  };
}

function memberOption(members: readonly Member[], member: Member | null, label: string, hint: string | undefined, onPick: () => void): HTMLElement {
  const button = el("button", { type: "button", class: "ev-pick" }, [
    member === null ? el("span", { class: "ev-empty__icon ev-pick__icon" }, [icon("sparkle", "icon")]) : avatar(members, member.id),
    el("span", { class: "ev-pick__text" }, [el("span", { class: "ev-pick__label" }, [label]), hint !== undefined && el("span", { class: "ev-muted" }, [hint])]),
  ]);
  button.addEventListener("click", onPick);
  return button;
}

function openReassignSheet(opener: HTMLElement, members: readonly Member[], task: HouseholdTask, onPick: (memberId: string | null) => void): void {
  const t = tr.home.tasks;
  const sheet = openSheet(sheetHost(opener), t.reassignTitle, { opener });
  sheet.body.replaceChildren(
    el("div", { class: "ev-picklist" }, [
      memberOption(members, null, t.auto, t.autoHint, () => {
        sheet.close();
        onPick(null);
      }),
      ...members
        .filter((m) => m.id !== task.assignedMemberId)
        .map((m) =>
          memberOption(members, m, t.reassignTo(m.displayName), undefined, () => {
            sheet.close();
            onPick(m.id);
          }),
        ),
    ]),
  );
}

function openAddSheet(opener: HTMLElement, session: HomeSession, members: readonly Member[]): void {
  const t = tr.home.tasks;
  const sheet = openSheet(sheetHost(opener), t.addTitle, { opener });
  const title = el("input", { id: "task-title", class: "input", type: "text", maxlength: "80", placeholder: t.titlePlaceholder, autocomplete: "off", required: true });
  const error = el("p", { class: "field__error", role: "alert", hidden: true }, [t.titleRequired]);
  const assignee = el(
    "select",
    { id: "task-assignee", class: "input ev-select" },
    [el("option", { value: "" }, [t.auto]), ...members.map((m) => el("option", { value: m.id }, [m.displayName]))],
  );
  const effort = el(
    "select",
    { id: "task-effort", class: "input ev-select" },
    [1, 2, 3].map((n) => el("option", { value: String(n), selected: n === 1 }, [`${t.efforts[n] ?? String(n)} (${t.points(String(n))})`])),
  );
  const save = el("button", { type: "submit", class: "button button--primary button--block" }, [t.save]);
  const form = el("form", { class: "ev-form", novalidate: true }, [
    el("div", { class: "field" }, [el("label", { class: "field__label", for: "task-title" }, [t.titleLabel]), title, error]),
    el("div", { class: "field" }, [el("label", { class: "field__label", for: "task-assignee" }, [t.assignee]), assignee]),
    el("div", { class: "field" }, [el("label", { class: "field__label", for: "task-effort" }, [t.effort]), effort]),
    save,
  ]);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const value = title.value.trim();
    error.hidden = value !== "";
    if (value === "") {
      title.setAttribute("aria-invalid", "true");
      title.focus();
      return;
    }
    save.disabled = true;
    void session.backend().then(
      async (b) => {
        try {
          const created = await b.slice2.createTask({ title: value, assignedMemberId: assignee.value === "" ? null : assignee.value, effortPoints: Number(effort.value) });
          session.s2.applyTask(created);
          showToast(t.added, "success");
          sheet.close();
        } catch (e) {
          save.disabled = false;
          showToast(commandErrorMessage(e), "error");
        }
      },
      () => {
        save.disabled = false;
      },
    );
  });
  sheet.body.replaceChildren(form);
  title.focus();
}
