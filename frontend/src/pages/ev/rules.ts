import { commandErrorMessage } from "../../home/api.ts";
import { el } from "../../components/dom.ts";
import type { HomeSession } from "../../home/session.ts";
import { rulesByTrigger, strategyLabel, triggerLabel, upsertRule } from "../../home/slice2/logic.ts";
import { TASK_STRATEGIES, type RulePatch, type TaskRule, type TaskStrategy } from "../../home/slice2/types.ts";
import type { Member } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";
import { paths } from "../../paths.ts";
import { loadHome, renderLoadError, renderLoading } from "./common.ts";
import { renderSubPage } from "./subpage.ts";
import { showToast } from "./toast.ts";

/** Görev kuralları: trigger → template → strategy, with on/off switch and strategy picker. */
export function renderRules(main: HTMLElement, session: HomeSession): () => void {
  const t = tr.home.rules;
  const body = renderSubPage(main, t.title, paths.evTasks);
  let disposed = false;
  let rules: TaskRule[] = [];

  const save = async (rule: TaskRule, patch: RulePatch): Promise<void> => {
    const before = rules;
    rules = upsertRule(rules, { ...rule, ...patch });
    draw();
    try {
      const updated = await (await session.backend()).slice2.putRule(rule.id, patch);
      rules = upsertRule(rules, updated);
      showToast(t.saved, "success");
    } catch (error) {
      rules = before;
      showToast(commandErrorMessage(error), "error");
    }
    if (!disposed) draw();
  };

  function ruleCard(rule: TaskRule, members: readonly Member[]): HTMLElement {
    const switchId = `rule-switch-${rule.id}`;
    const toggle = el(
      "button",
      { type: "button", role: "switch", class: "ev-switch", id: switchId, "aria-checked": String(rule.enabled), "data-key": `rule-${rule.id}` },
      [el("span", { class: "ev-switch__track", "aria-hidden": "true" }, [el("span", { class: "ev-switch__thumb" })]), t.enabled],
    );
    toggle.addEventListener("click", () => {
      void save(rule, { enabled: !rule.enabled });
    });

    const strategy = el(
      "select",
      { id: `rule-strategy-${rule.id}`, class: "input ev-select", disabled: !rule.enabled },
      TASK_STRATEGIES.map((s) => el("option", { value: s, selected: s === rule.strategy }, [strategyLabel(s)])),
    );
    strategy.addEventListener("change", () => {
      const next = strategy.value as TaskStrategy;
      void save(rule, next === "fixed" ? { strategy: next, fixedMemberId: rule.fixedMemberId ?? members[0]?.id ?? null } : { strategy: next });
    });

    const fixed =
      rule.strategy === "fixed"
        ? el("div", { class: "field" }, [
            el("label", { class: "field__label", for: `rule-fixed-${rule.id}` }, [t.fixedMember]),
            (() => {
              const select = el(
                "select",
                { id: `rule-fixed-${rule.id}`, class: "input ev-select", disabled: !rule.enabled },
                members.map((m) => el("option", { value: m.id, selected: m.id === rule.fixedMemberId }, [m.displayName])),
              );
              select.addEventListener("change", () => {
                void save(rule, { fixedMemberId: select.value });
              });
              return select;
            })(),
          ])
        : null;

    return el("article", { class: `ev-panel ev-rule ${rule.enabled ? "" : "ev-rule--off"}` }, [
      el("div", { class: "ev-rule__head" }, [
        el("div", { class: "ev-rule__text" }, [el("h2", { class: "ev-rule__trigger" }, [triggerLabel(rule.trigger)]), el("p", { class: "ev-rule__template" }, [`→ ${rule.template}`])]),
        el("span", { class: "badge badge--neutral" }, [t.points(String(rule.effortPoints))]),
      ]),
      toggle,
      el("div", { class: "field" }, [el("label", { class: "field__label", for: `rule-strategy-${rule.id}` }, [t.strategy]), strategy]),
      fixed,
    ]);
  }

  function draw(): void {
    const members = session.store.home?.members ?? [];
    body.replaceChildren(
      el("p", { class: "ev-muted" }, [t.subtitle]),
      ...(rules.length === 0 ? [el("p", { class: "ev-muted" }, [t.empty])] : rulesByTrigger(rules).map((r) => ruleCard(r, members))),
    );
  }

  const start = (): void => {
    renderLoading(body, t.loading);
    void session.backend().then(
      async (b) => {
        rules = await b.slice2.listRules();
        if (!disposed) draw();
      },
      (error: unknown) => {
        if (!disposed) renderLoadError(body, error, start);
      },
    );
  };
  loadHome(session, body, start);
  return () => {
    disposed = true;
  };
}
