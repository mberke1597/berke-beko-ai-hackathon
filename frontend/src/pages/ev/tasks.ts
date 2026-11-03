import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import type { HomeSession } from "../../home/session.ts";
import type { Member } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { loadHome } from "./common.ts";
import { showToast } from "./toast.ts";

/** Görevler tab: empty state that previews the task UI (no backend in slice 1). */
export function renderTasks(main: HTMLElement, session: HomeSession): () => void {
  const region = el("div", { class: "ev-screen" });
  main.replaceChildren(region);
  loadHome(session, region, () => {
    draw(region, session.store.home?.members ?? []);
  });
  return () => undefined;
}

function draw(region: HTMLElement, members: readonly Member[]): void {
  const t = tr.home.tasks;

  const add = el("button", { type: "button", class: "button button--secondary button--sm" }, [icon("plus", "icon icon--sm"), t.add]);
  add.addEventListener("click", () => {
    showToast(t.addSoon);
  });

  const filters = el(
    "div",
    { class: "ev-chips", role: "group", "aria-label": t.filtersLabel },
    [t.filters.mine, t.filters.everyone, t.filters.done].map((label, index) =>
      el("button", { type: "button", class: "ev-chip", "aria-pressed": String(index === 0), disabled: true }, [label]),
    ),
  );

  const example = el("div", { class: "ev-task ev-task--preview", "aria-label": t.exampleTitle }, [
    el("div", { class: "ev-task__head" }, [
      icon("washer", "icon ev-task__icon"),
      el("span", { class: "ev-task__title" }, [t.exampleTask]),
      el("span", { class: "ev-task__assignee" }, [members[1]?.displayName ?? ""]),
    ]),
    el("p", { class: "ev-task__origin" }, [t.exampleOrigin]),
    el(
      "div",
      { class: "ev-task__actions" },
      [t.exampleActions.done, t.exampleActions.reassign, t.exampleActions.skip].map((label) =>
        el("span", { class: "ev-task__action" }, [label]),
      ),
    ),
  ]);

  region.replaceChildren(
      el("div", { class: "ev-screen__head" }, [el("h1", { class: "ev-screen__title" }, [t.title]), add]),
      filters,
      el("div", { class: "ev-empty" }, [
        el("span", { class: "ev-empty__icon" }, [icon("tasks", "icon icon--xl")]),
        el("h2", { class: "ev-empty__title" }, [t.emptyTitle]),
        el("p", { class: "ev-empty__body" }, [t.emptyBody]),
      ]),
      el("section", { class: "ev-panel", "aria-labelledby": "tasks-example" }, [
        el("h2", { id: "tasks-example", class: "ev-panel__title" }, [t.exampleTitle, " ", el("span", { class: "badge badge--neutral" }, [t.soon])]),
        example,
      ]),
      el("section", { class: "ev-panel", "aria-labelledby": "tasks-fairness" }, [
        el("h2", { id: "tasks-fairness", class: "ev-panel__title" }, [t.fairnessTitle]),
        el(
          "ul",
          { class: "ev-fairness" },
          members.map((m, index) =>
            el("li", { class: "ev-fairness__row" }, [
              el("span", { class: `ev-avatar ev-avatar--${String((index % 3) + 1)}`, "aria-hidden": "true" }, [m.displayName.charAt(0)]),
              el("span", { class: "ev-fairness__name" }, [m.displayName]),
              el("span", { class: "ev-fairness__bar", "aria-hidden": "true" }),
              el("span", { class: "ev-fairness__count" }, ["0"]),
            ]),
          ),
        ),
        el("p", { class: "ev-muted" }, [t.fairnessEmpty]),
      ]),
      el("div", { class: "ev-link-row", "aria-disabled": "true" }, [
        icon("tasks", "icon icon--sm"),
        el("span", {}, [t.rules]),
        el("span", { class: "badge badge--neutral" }, [t.soon]),
      ]),
  );
}
