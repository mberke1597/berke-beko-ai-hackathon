// Small hand-made SVG line chart for 24 h AC telemetry (room temperature + humidity).

import { el } from "../../components/dom.ts";
import { formatPercent, formatTemp } from "../../home/labels.ts";
import type { TelemetryPoint } from "../../home/types.ts";
import { tr } from "../../i18n/tr.ts";

const W = 320;
const H = 140;
const PAD = { top: 12, right: 34, bottom: 22, left: 34 };
const SVG_NS = "http://www.w3.org/2000/svg";

/** Builds an SVG path "M x y L x y ..." for the values, scaled into the plot area. Exported for tests. */
export function linePath(values: readonly (number | null)[], min: number, max: number, width = W, height = H): string {
  const plotW = width - PAD.left - PAD.right;
  const plotH = height - PAD.top - PAD.bottom;
  const span = max - min || 1;
  const n = Math.max(1, values.length - 1);
  let d = "";
  let pen = false;
  values.forEach((v, i) => {
    if (v === null) {
      pen = false;
      return;
    }
    const x = PAD.left + (i / n) * plotW;
    const y = PAD.top + (1 - (v - min) / span) * plotH;
    d += `${pen ? "L" : "M"}${x.toFixed(1)} ${y.toFixed(1)} `;
    pen = true;
  });
  return d.trim();
}

function svg<K extends keyof SVGElementTagNameMap>(tag: K, attrs: Record<string, string>, text?: string): SVGElementTagNameMap[K] {
  const node = document.createElementNS(SVG_NS, tag);
  for (const [k, v] of Object.entries(attrs)) node.setAttribute(k, v);
  if (text !== undefined) node.textContent = text;
  return node;
}

function range(values: number[], pad: number): [number, number] {
  return [Math.floor(Math.min(...values) - pad), Math.ceil(Math.max(...values) + pad)];
}

export function createTelemetryChart(points: readonly TelemetryPoint[]): HTMLElement {
  const temps = points.map((p) => p.roomTempC);
  const hums = points.map((p) => p.humidityPct);
  const tempValues = temps.filter((v): v is number => v !== null);
  const humValues = hums.filter((v): v is number => v !== null);
  if (tempValues.length < 2) return el("p", { class: "ev-chart__empty" }, [tr.home.detail.chartEmpty]);

  const [tMin, tMax] = range(tempValues, 0.5);
  const [hMin, hMax] = humValues.length > 1 ? range(humValues, 2) : [0, 100];

  const root = svg("svg", {
    viewBox: `0 0 ${String(W)} ${String(H)}`,
    class: "ev-chart__svg",
    role: "img",
    "aria-label": tr.home.detail.chartLabel(formatTemp(Math.min(...tempValues)), formatTemp(Math.max(...tempValues))),
  });

  const bottom = H - PAD.bottom;
  root.append(
    svg("line", { x1: String(PAD.left), x2: String(W - PAD.right), y1: String(bottom), y2: String(bottom), class: "ev-chart__axis" }),
    svg("line", { x1: String(PAD.left), x2: String(W - PAD.right), y1: String(PAD.top), y2: String(PAD.top), class: "ev-chart__grid" }),
    svg("text", { x: String(PAD.left - 4), y: String(PAD.top + 4), "text-anchor": "end", class: "ev-chart__label ev-chart__label--temp" }, formatTemp(tMax)),
    svg("text", { x: String(PAD.left - 4), y: String(bottom), "text-anchor": "end", class: "ev-chart__label ev-chart__label--temp" }, formatTemp(tMin)),
    svg("text", { x: String(W - PAD.right + 4), y: String(PAD.top + 4), class: "ev-chart__label ev-chart__label--hum" }, formatPercent(hMax)),
    svg("text", { x: String(W - PAD.right + 4), y: String(bottom), class: "ev-chart__label ev-chart__label--hum" }, formatPercent(hMin)),
  );
  root.append(
    svg("text", { x: String(PAD.left), y: String(H - 6), class: "ev-chart__label" }, tr.home.detail.chartStart),
    svg("text", { x: String(W - PAD.right), y: String(H - 6), "text-anchor": "end", class: "ev-chart__label" }, tr.home.detail.chartEnd),
  );
  if (humValues.length > 1) root.append(svg("path", { d: linePath(hums, hMin, hMax), class: "ev-chart__line ev-chart__line--hum" }));
  root.append(svg("path", { d: linePath(temps, tMin, tMax), class: "ev-chart__line ev-chart__line--temp" }));

  const legend = el("div", { class: "ev-chart__legend", "aria-hidden": "true" }, [
    el("span", { class: "ev-chart__key ev-chart__key--temp" }, [tr.home.detail.chartTemp]),
    humValues.length > 1 && el("span", { class: "ev-chart__key ev-chart__key--hum" }, [tr.home.detail.chartHumidity]),
  ]);

  const figure = el("div", { class: "ev-chart" }, [legend]);
  figure.append(root);
  return figure;
}
