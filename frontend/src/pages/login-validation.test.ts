import { describe, expect, it } from "vitest";
import { tr } from "../i18n/tr.ts";
import { validateLogin } from "./login-validation.ts";

describe("validateLogin", () => {
  it("requires both fields", () => {
    expect(validateLogin({ email: " ", password: "" })).toEqual({
      email: tr.login.errors.emailRequired,
      password: tr.login.errors.passwordRequired,
    });
  });
  it("rejects invalid e-mail", () => {
    expect(validateLogin({ email: "abc", password: "x" }).email).toBe(tr.login.errors.emailInvalid);
  });
  it("accepts valid input", () => {
    expect(validateLogin({ email: "a@beko.com", password: "x" })).toEqual({});
  });
});
