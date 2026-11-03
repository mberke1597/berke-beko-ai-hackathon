import { el, LOGO_HEIGHT, LOGO_WIDTH, logoSrc } from "../components/dom.ts";
import { tr } from "../i18n/tr.ts";
import { validateLogin, type LoginInput } from "./login-validation.ts";

interface Field {
  wrapper: HTMLDivElement;
  input: HTMLInputElement;
  error: HTMLParagraphElement;
}

function createField(name: keyof LoginInput, label: string, type: string, autocomplete: string, placeholder?: string): Field {
  const id = `login-${name}`;
  const errorId = `${id}-error`;
  const input = el("input", {
    id,
    name,
    type,
    class: "input",
    autocomplete,
    required: true,
    placeholder,
    "aria-describedby": errorId,
  });
  const error = el("p", { id: errorId, class: "field__error", hidden: true });
  const wrapper = el("div", { class: "field" }, [el("label", { class: "field__label", for: id }, [label]), input, error]);
  return { wrapper, input, error };
}

function setFieldError(field: Field, message: string | undefined): void {
  field.error.textContent = message ?? "";
  field.error.hidden = message === undefined;
  if (message === undefined) field.input.removeAttribute("aria-invalid");
  else field.input.setAttribute("aria-invalid", "true");
}

/** Login page shell. Phase 1: not wired to the backend; a valid submit just calls `onSuccess`. */
export function renderLoginPage(root: HTMLElement, onSuccess: () => void): void {
  const email = createField("email", tr.login.email, "email", "username", tr.login.emailPlaceholder);
  const password = createField("password", tr.login.password, "password", "current-password");
  const submit = el("button", { type: "submit", class: "button button--primary button--block" }, [tr.login.submit]);

  const form = el("form", { class: "login-card__form", novalidate: true }, [email.wrapper, password.wrapper, submit]);
  form.addEventListener("submit", (event) => {
    event.preventDefault();
    const errors = validateLogin({ email: email.input.value, password: password.input.value });
    setFieldError(email, errors.email);
    setFieldError(password, errors.password);
    if (errors.email !== undefined) {
      email.input.focus();
      return;
    }
    if (errors.password !== undefined) {
      password.input.focus();
      return;
    }
    submit.disabled = true;
    submit.textContent = tr.login.submitting;
    onSuccess();
  });

  const card = el("section", { class: "login-card", "aria-labelledby": "login-title" }, [
    el("img", {
      class: "login-card__logo",
      src: logoSrc,
      alt: tr.app.logoAlt,
      width: String(LOGO_WIDTH),
      height: String(LOGO_HEIGHT),
    }),
    el("h1", { id: "login-title", class: "login-card__title" }, [tr.login.title]),
    el("p", { class: "login-card__subtitle" }, [tr.login.subtitle]),
    form,
  ]);

  root.replaceChildren(el("main", { class: "login-page" }, [card]));
}
