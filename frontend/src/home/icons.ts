// Inline SVG icons (24x24, stroke = currentColor). Static, trusted markup only.

import type { ApplianceType } from "./types.ts";
import type { StateTone } from "./labels.ts";

const paths = {
  ac: '<rect x="2.5" y="5" width="19" height="8" rx="2"/><path d="M6 10h12"/><path d="M7 16.5c0 1.2-1 1.5-1 2.5M12 16.5c0 1.2-1 1.5-1 2.5M17 16.5c0 1.2-1 1.5-1 2.5"/>',
  washer:
    '<rect x="4" y="2.5" width="16" height="19" rx="2"/><path d="M4 7h16"/><circle cx="7.5" cy="4.8" r=".6" fill="currentColor"/><circle cx="12" cy="14" r="4.5"/><path d="M9.5 14.5c1-.8 2-.8 2.5 0s1.5.8 2.5 0"/>',
  dishwasher:
    '<rect x="4" y="2.5" width="16" height="19" rx="2"/><path d="M4 7h16"/><path d="M8 4.8h3"/><path d="M8 11v6M12 11v6M16 11v6"/>',
  oven: '<rect x="3" y="3" width="18" height="18" rx="2"/><path d="M3 8h18"/><circle cx="7" cy="5.5" r=".6" fill="currentColor"/><circle cx="10" cy="5.5" r=".6" fill="currentColor"/><rect x="6.5" y="11" width="11" height="7" rx="1"/>',
  fridge: '<rect x="5" y="2" width="14" height="20" rx="2"/><path d="M5 10h14"/><path d="M8 5.5v2M8 13v3"/>',
  home: '<path d="M3 10.5 12 3l9 7.5"/><path d="M5 9.5V21h14V9.5"/><path d="M10 21v-6h4v6"/>',
  tasks: '<rect x="4" y="3.5" width="16" height="17" rx="2"/><path d="m8 12 2.5 2.5L16 9"/>',
  sparkle:
    '<path d="M12 3c.6 3.9 2.1 5.4 6 6-3.9.6-5.4 2.1-6 6-.6-3.9-2.1-5.4-6-6 3.9-.6 5.4-2.1 6-6Z"/><path d="M18.5 15c.3 1.6.9 2.2 2.5 2.5-1.6.3-2.2.9-2.5 2.5-.3-1.6-.9-2.2-2.5-2.5 1.6-.3 2.2-.9 2.5-2.5Z"/>',
  mic: '<rect x="9" y="2.5" width="6" height="12" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0"/><path d="M12 17.5V21"/>',
  bell: '<path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15Z"/><path d="M10 20.5a2 2 0 0 0 4 0"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/>',
  back: '<path d="M15 5l-7 7 7 7"/>',
  power: '<path d="M12 3v8"/><path d="M6.6 6.6a7.5 7.5 0 1 0 10.8 0"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  minus: '<path d="M5 12h14"/>',
  check: '<circle cx="12" cy="12" r="9"/><path d="m8 12 3 3 5-6"/>',
  alert: '<path d="M12 3 2 20h20Z"/><path d="M12 10v4"/><circle cx="12" cy="17" r=".6" fill="currentColor"/>',
  play: '<path d="M7 4.5v15l12-7.5Z"/>',
  pause: '<path d="M8 5v14M16 5v14"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="1.5"/>',
  off: '<circle cx="12" cy="12" r="9"/><path d="M8 12h8"/>',
  idle: '<circle cx="12" cy="12" r="9"/>',
  running: '<circle cx="12" cy="12" r="9"/><circle cx="12" cy="12" r="4" fill="currentColor"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>',
  leaf: '<path d="M5 19c0-8 5-13 15-14-1 10-6 15-14 15"/><path d="M5 19 13 11"/>',
  chevron: '<path d="m6 9 6 6 6-6"/>',
  close: '<path d="M6 6l12 12M18 6 6 18"/>',
  send: '<path d="M4 12 20 4l-6 16-3-7Z"/>',
  clock: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
  drop: '<path d="M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11Z"/>',
  thermo: '<path d="M10 4a2 2 0 0 1 4 0v10a4 4 0 1 1-4 0Z"/><path d="M12 9v7"/>',
  remote: '<rect x="7" y="2.5" width="10" height="19" rx="3"/><circle cx="12" cy="7" r="1.5"/><path d="M10 12h4M10 15h4"/>',
  chart: '<path d="M4 20V4"/><path d="M4 20h16"/><path d="m7 15 4-5 3 3 5-6"/>',
} as const;

export type IconName = keyof typeof paths;

/** Creates an inline SVG icon. Decorative by default (aria-hidden). */
export function icon(name: IconName, className = "icon"): SVGSVGElement {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  svg.setAttribute("viewBox", "0 0 24 24");
  svg.setAttribute("fill", "none");
  svg.setAttribute("stroke", "currentColor");
  svg.setAttribute("stroke-width", "1.8");
  svg.setAttribute("stroke-linecap", "round");
  svg.setAttribute("stroke-linejoin", "round");
  svg.setAttribute("aria-hidden", "true");
  svg.setAttribute("focusable", "false");
  svg.setAttribute("class", className);
  svg.innerHTML = paths[name];
  return svg;
}

export function applianceIconName(type: ApplianceType): IconName {
  switch (type) {
    case "air_conditioner":
      return "ac";
    case "washing_machine":
      return "washer";
    case "dishwasher":
      return "dishwasher";
    case "oven":
      return "oven";
    case "refrigerator":
      return "fridge";
  }
}

export function toneIconName(tone: StateTone): IconName {
  switch (tone) {
    case "off":
      return "off";
    case "idle":
      return "idle";
    case "running":
      return "running";
    case "paused":
      return "pause";
    case "finished":
      return "check";
    case "error":
      return "alert";
  }
}
