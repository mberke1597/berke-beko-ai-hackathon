import { el } from "../components/dom.ts";
import { tr } from "../i18n/tr.ts";
import { paths } from "../paths.ts";
import { hrefFor } from "../router.ts";

export function renderNotFoundPage(main: HTMLElement): void {
  main.replaceChildren(
    el("div", { class: "page" }, [
      el("div", { class: "page__header" }, [
        el("h1", { class: "page__title" }, [tr.notFound.title]),
        el("p", { class: "page__subtitle" }, [tr.notFound.body]),
      ]),
      el("a", { class: "button button--primary", href: hrefFor(paths.home) }, [tr.notFound.backHome]),
    ]),
  );
}
