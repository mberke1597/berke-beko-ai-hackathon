type Attrs = Record<string, string | boolean | undefined>;
type Child = Node | string | null | undefined | false;

/** Minimal element factory. Boolean `true` sets an empty attribute; `false`/`undefined` skips it. */
export function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  attrs: Attrs = {},
  children: readonly Child[] = [],
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) {
    if (value === undefined || value === false) continue;
    node.setAttribute(name, value === true ? "" : value);
  }
  for (const child of children) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child);
  }
  return node;
}

/** Path of the trimmed blue Beko logo (117x70 px, transparent background). */
export const logoSrc = `${import.meta.env.BASE_URL}brand/beko-logo-trimmed.png`;
export const LOGO_WIDTH = 117;
export const LOGO_HEIGHT = 70;
