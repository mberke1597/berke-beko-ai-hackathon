import { el } from "../../components/dom.ts";
import { icon, type IconName } from "../../home/icons.ts";
import { tr } from "../../i18n/tr.ts";

export type ToastTone = "info" | "success" | "error";

const TOAST_MS = 4500;

let region: HTMLElement | undefined;

/** Registers the live region that hosts toasts (created by the shell). */
export function setToastRegion(element: HTMLElement): void {
  region = element;
}

const toneIcon: Record<ToastTone, IconName> = { info: "bell", success: "check", error: "alert" };

/** Shows a short, dismissible message. Errors use role="alert", others are polite. */
export function showToast(message: string, tone: ToastTone = "info"): void {
  if (!region?.isConnected) return;
  const close = el("button", { type: "button", class: "toast__close", "aria-label": tr.home.toastClose });
  close.append(icon("close", "icon icon--sm"));
  const toast = el("div", { class: `toast toast--${tone}`, role: tone === "error" ? "alert" : "status" }, [
    icon(toneIcon[tone], "icon toast__icon"),
    el("span", { class: "toast__text" }, [message]),
    close,
  ]);
  const remove = (): void => {
    toast.remove();
  };
  close.addEventListener("click", remove);
  region.append(toast);
  // Keep at most three toasts on screen.
  while (region.childElementCount > 3) region.firstElementChild?.remove();
  window.setTimeout(remove, TOAST_MS);
}
