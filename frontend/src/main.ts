import "./styles/tokens.css";
import "./styles/base.css";
import "./styles/components.css";

import { renderAppShell } from "./components/app-shell.ts";
import { tr } from "./i18n/tr.ts";
import { renderLoginPage } from "./pages/login.ts";
import { renderNotFoundPage } from "./pages/not-found.ts";
import { renderStatusPage } from "./pages/status.ts";
import { paths } from "./paths.ts";
import { Router } from "./router.ts";

const root = document.querySelector<HTMLElement>("#app");
if (!root) throw new Error("Missing #app root element");

document.title = tr.app.name;

const router: Router = new Router({
  routes: {
    [paths.login]: () => {
      renderLoginPage(root, () => {
        router.navigate(paths.home);
      });
    },
    [paths.home]: () => renderStatusPage(renderAppShell(root)),
  },
  notFound: () => {
    renderNotFoundPage(renderAppShell(root));
  },
});

router.start();
