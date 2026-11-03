// Hand-made SVG line chart for weekly rates (override rate, good-feedback rate). No chart library.

import { el } from "../../components/dom.ts";
import { formatRate, formatWeek, pathFor, ratePoints } from "../../home/slice2/logic.ts";
import type { RatePoint } from "../../home/slice2/types.ts";

const W = 320;
const H = 160;
const PAD = { top: 12, right: 14, bottom: 26, left: 38 };
const SVG_NS = "http://www.w3.org/2000/svg";

export interface RateSeries {
  tone: "override" | "good";
  label: string;
  points: readonly RatePoint[];
}

function svg<K extends keyof SVGElementTagNameMap>(tag: K, attrs: Record<string, string>, text?: string): SVGElementTagNameMap[K] {
  const node = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  if (text !== undefined) node.textContent = text;
  return node;
}

/** Line chart of rates 0..1 over weeks. Returns a text fallback when no series has two points. */
export function createRateChart(series: readonly RateSeries[], ariaLabel: string, emptyText: string, width = W, height = H): HTMLElement {
  const usable = series.filter((s) => s.points.length >= 2);
  if (usable.length === 0) return el("p", { class: "ev-muted" }, [emptyText]);

  const root = svg("svg", { viewBox: `0 0 ${String(width)} ${String(height)}`, class: "ev-chart__svg", role: "img", "aria-label": ariaLabel });
  const bottom = height - PAD.bottom;
  for (const level of [0, 0.5, 1]) {
    const y = PAD.top + (1 - level) * (bottom - PAD.top);
    root.append(
      svg("line", { x1: String(PAD.left), x2: String(width - PAD.right), y1: String(y), y2: String(y), class: level === 0 ? "ev-chart__axis" : "ev-chart__grid" }),
      svg("text", { x: String(PAD.left - 5), y: String(y + 3), "text-anchor": "end", class: "ev-chart__label" }, formatRate(level)),
    );
  }
  const first = usable[0]?.points[0];
  const last = usable[0]?.points[usable[0].points.length - 1];
  if (first && last) {
    root.append(
      svg("text", { x: String(PAD.left), y: String(height - 8), class: "ev-chart__label" }, formatWeek(first.weekStartUtc)),
      svg("text", { x: String(width - PAD.right), y: String(height - 8), "text-anchor": "end", class: "ev-chart__label" }, formatWeek(last.weekStartUtc)),
    );
  }
  for (const s of usable) {
    const points = ratePoints(s.points, width, height, PAD);
    root.append(svg("path", { d: pathFor(points), class: `ev-chart__line ev-chart__line--${s.tone}` }));
    for (const p of points) root.append(svg("circle", { cx: String(p.x), cy: String(p.y), r: "3", class: `ev-chart__dot ev-chart__dot--${s.tone}` }));
  }

  const figure = el("div", { class: "ev-chart" }, [
    el(
      "div",
      { class: "ev-chart__legend", "aria-hidden": "true" },
      usable.map((s) => el("span", { class: `ev-chart__key ev-chart__key--${s.tone}` }, [s.label])),
    ),
  ]);
  figure.append(root);
  return figure;
}
