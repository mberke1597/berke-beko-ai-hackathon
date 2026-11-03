import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import { tr } from "../../i18n/tr.ts";
import { hrefFor } from "../../router.ts";

/** Back link: goes back in history when there is something to go back to, otherwise to `parentPath`. */
export function backLink(parentPath: string): HTMLAnchorElement {
  const link = el("a", { class: "ev-icon-button ev-detail__back", href: hrefFor(parentPath), "aria-label": tr.home.detail.back }, [icon("back")]);
  link.addEventListener("click", (event) => {
    if (window.history.length > 1) {
      event.preventDefault();
      window.history.back();
    }
  });
  return link;
}

/** Full-screen sub page frame (title bar with back arrow). Returns the content element. */
export function renderSubPage(main: HTMLElement, title: string, parentPath: string, actions: readonly Node[] = []): HTMLElement {
  const body = el("div", { class: "ev-detail__body ev-subpage" });
  main.replaceChildren(
    el("div", { class: "ev-detail" }, [
      el("div", { class: "ev-detail__bar" }, [backLink(parentPath), el("h1", { class: "ev-detail__title" }, [title]), ...actions]),
      body,
    ]),
  );
  return body;
}
