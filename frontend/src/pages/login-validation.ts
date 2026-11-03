import { tr } from "../i18n/tr.ts";

export interface LoginInput {
  email: string;
  password: string;
}

export type LoginErrors = Partial<Record<keyof LoginInput, string>>;

// Pragmatic check: something@something.tld, no spaces.
const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function validateLogin(input: LoginInput): LoginErrors {
  const errors: LoginErrors = {};
  const email = input.email.trim();
  if (email === "") errors.email = tr.login.errors.emailRequired;
  else if (!EMAIL_PATTERN.test(email)) errors.email = tr.login.errors.emailInvalid;
  if (input.password === "") errors.password = tr.login.errors.passwordRequired;
  return errors;
}
