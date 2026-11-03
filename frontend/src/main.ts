import "./styles/tokens.css";
import "./styles/base.css";
import "./styles/components.css";
import "./styles/home.css";

import { renderAppShell } from "./components/app-shell.ts";
import { homeSession } from "./home/session.ts";
import { tr } from "./i18n/tr.ts";
import { renderAssistant } from "./pages/ev/assistant.ts";
import { renderDetail } from "./pages/ev/detail.ts";
import { renderEvim } from "./pages/ev/evim.ts";
import { renderLearning } from "./pages/ev/learning.ts";
import { renderNotifications } from "./pages/ev/notifications.ts";
import { renderRules } from "./pages/ev/rules.ts";
import { renderSettings } from "./pages/ev/settings.ts";
import { renderEvShell } from "./pages/ev/shell.ts";
import { renderTasks } from "./pages/ev/tasks.ts";
import { renderLoginPage } from "./pages/login.ts";
import { renderNotFoundPage } from "./pages/not-found.ts";
import { renderPanel } from "./pages/panel.ts";
import { renderStatusPage } from "./pages/status.ts";
import { paths } from "./paths.ts";
import { Router } from "./router.ts";

const root = document.querySelector<HTMLElement>("#app");
if (!root) throw new Error("Missing #app root element");

/** Sets the document title and the light app background for the Çeliknaz screens. */
function setScreen(title: string, consumer: boolean): void {
  document.title = title;
  document.body.classList.toggle("is-consumer", consumer);
}

const router: Router = new Router({
  routes: {
    [paths.login]: () => {
      setScreen(tr.app.name, false);
      renderLoginPage(root, () => {
        router.navigate(paths.home);
      });
    },
    [paths.home]: () => {
      setScreen(tr.app.name, false);
      return renderStatusPage(renderAppShell(root));
    },
    [paths.ev]: () => {
      setScreen(tr.home.appName, true);
      return renderEvim(renderEvShell(root, homeSession(), "evim"), homeSession());
    },
    [paths.evTasks]: () => {
      setScreen(`${tr.home.tabs.tasks} · ${tr.home.appName}`, true);
      return renderTasks(renderEvShell(root, homeSession(), "tasks"), homeSession());
    },
    [paths.evAssistant]: () => {
      setScreen(`${tr.home.tabs.assistant} · ${tr.home.appName}`, true);
      return renderAssistant(renderEvShell(root, homeSession(), "assistant"), homeSession());
    },
    [paths.evRules]: () => {
      setScreen(`${tr.home.rules.title} · ${tr.home.appName}`, true);
      return renderRules(renderEvShell(root, homeSession(), null), homeSession());
    },
    [paths.evSettings]: () => {
      setScreen(`${tr.home.settings.title} · ${tr.home.appName}`, true);
      return renderSettings(renderEvShell(root, homeSession(), null), homeSession());
    },
    [paths.evNotifications]: () => {
      setScreen(`${tr.home.notif.title} · ${tr.home.appName}`, true);
      return renderNotifications(renderEvShell(root, homeSession(), null), homeSession());
    },
    [paths.evLearning]: () => {
      setScreen(`${tr.home.learn.title} · ${tr.home.appName}`, true);
      return renderLearning(renderEvShell(root, homeSession(), null), homeSession());
    },
    [paths.panel]: () => {
      setScreen(tr.home.panel.pageTitle, true);
      return renderPanel(root, homeSession());
    },
    [paths.evDevicePattern]: (params) => {
      setScreen(tr.home.appName, true);
      return renderDetail(renderEvShell(root, homeSession(), null), homeSession(), params.id ?? "");
    },
  },
  notFound: () => {
    setScreen(tr.app.name, false);
    renderNotFoundPage(renderAppShell(root));
  },
});

router.start();
