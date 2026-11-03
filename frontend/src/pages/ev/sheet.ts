// Bottom sheet built on <dialog>: focus trap, Escape and backdrop click come from the platform.

import { el } from "../../components/dom.ts";
import { icon } from "../../home/icons.ts";
import { tr } from "../../i18n/tr.ts";

export interface SheetHandle {
  dialog: HTMLDialogElement;
  body: HTMLElement;
  close: () => void;
}

let activeSheet: HTMLDialogElement | undefined;

/**
 * Opens a sheet with a title inside `host`. Only one sheet is open at a time (a new one replaces the old).
 * `onClose` runs after the sheet closed by any means; focus returns to `opener` when given.
 */
export function openSheet(host: HTMLElement, title: string, options: { opener?: HTMLElement; onClose?: () => void } = {}): SheetHandle {
  if (activeSheet?.open) activeSheet.close();

  const titleId = `sheet-title-${String(Date.now())}`;
  const closeButton = el("button", { type: "button", class: "ev-icon-button", "aria-label": tr.home.sheet.close }, [icon("close")]);
  const body = el("div", { class: "sheet__body" });
  const dialog = el("dialog", { class: "voice sheet", "aria-labelledby": titleId }, [
    el("div", { class: "voice__sheet sheet__panel" }, [
      el("div", { class: "voice__top" }, [el("h2", { id: titleId, class: "voice__title" }, [title]), closeButton]),
      body,
    ]),
  ]);

  const handle: SheetHandle = {
    dialog,
    body,
    close: () => {
      if (dialog.open) dialog.close();
    },
  };
  closeButton.addEventListener("click", handle.close);
  dialog.addEventListener("click", (event) => {
    if (event.target === dialog) handle.close();
  });
  dialog.addEventListener("close", () => {
    dialog.remove();
    if (activeSheet === dialog) activeSheet = undefined;
    if (options.opener?.isConnected) options.opener.focus();
    options.onClose?.();
  });

  host.append(dialog);
  activeSheet = dialog;
  dialog.showModal();
  return handle;
}

/** The element hosting sheets and toasts for the current screen. */
export function sheetHost(from: HTMLElement): HTMLElement {
  return from.closest<HTMLElement>(".ev-app") ?? from.closest<HTMLElement>(".panel-page") ?? document.body;
}
