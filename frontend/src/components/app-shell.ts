import { tr } from "../i18n/tr.ts";
import { paths } from "../paths.ts";
import { hrefFor } from "../router.ts";
import { el, LOGO_HEIGHT, LOGO_WIDTH, logoSrc } from "./dom.ts";

const MAIN_ID = "main-content";
const SIDEBAR_ID = "app-sidebar";

/** Placeholder navigation for Phase 1; pages arrive in later phases. */
const navItems: readonly string[] = [
  tr.nav.chat,
  tr.nav.agents,
  tr.nav.workflow,
  tr.nav.dashboard,
  tr.nav.users,
];

function createHeader(onToggleMenu: () => void): { header: HTMLElement; toggle: HTMLButtonElement } {
  const toggle = el(
    "button",
    {
      type: "button",
      class: "app-header__menu",
      "aria-controls": SIDEBAR_ID,
      "aria-expanded": "false",
    },
    [el("span", { class: "visually-hidden" }, [tr.app.menuToggle]), el("span", { class: "app-header__menu-icon", "aria-hidden": "true" })],
  );
  toggle.addEventListener("click", onToggleMenu);

  const brand = el("a", { class: "app-header__brand", href: hrefFor(paths.home) }, [
    el("img", {
      class: "app-header__logo",
      src: logoSrc,
      alt: tr.app.logoAlt,
      width: String(LOGO_WIDTH),
      height: String(LOGO_HEIGHT),
    }),
    el("span", { class: "app-header__title" }, [tr.app.name]),
  ]);

  const logout = el("a", { class: "button button--ghost button--sm", href: hrefFor(paths.login) }, [tr.header.logout]);

  const header = el("header", { class: "app-header" }, [toggle, brand, el("div", { class: "app-header__actions" }, [logout])]);
  return { header, toggle };
}

function createSidebar(): HTMLElement {
  const list = el(
    "ul",
    { class: "sidebar__list" },
    navItems.map((label) =>
      el("li", {}, [
        el("span", { class: "sidebar__item sidebar__item--disabled", "aria-disabled": "true" }, [
          el("span", { class: "sidebar__label" }, [label]),
          el("span", { class: "badge badge--neutral" }, [tr.nav.comingSoon]),
        ]),
      ]),
    ),
  );
  return el("nav", { id: SIDEBAR_ID, class: "sidebar", "aria-label": tr.nav.ariaLabel }, [list]);
}

/**
 * Renders the authenticated app layout (header + sidebar + main) into `root`
 * and returns the `<main>` element where the page content goes.
 */
export function renderAppShell(root: HTMLElement): HTMLElement {
  const sidebar = createSidebar();
  const { header, toggle } = createHeader(() => {
    const open = sidebar.classList.toggle("sidebar--open");
    toggle.setAttribute("aria-expanded", String(open));
  });
  const main = el("main", { id: MAIN_ID, class: "app-main", tabindex: "-1" });

  const skip = el("a", { class: "skip-link", href: `#${MAIN_ID}` }, [tr.app.skipToContent]);
  // The skip link must not change the route hash.
  skip.addEventListener("click", (event) => {
    event.preventDefault();
    main.focus();
  });

  root.replaceChildren(skip, header, el("div", { class: "app-body" }, [sidebar, main]));
  return main;
}
