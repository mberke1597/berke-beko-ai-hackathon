# Çeliknaz home app — UI specification

Status: **draft**. Date: 2026-11-03. Owner: lead (implementation: frontend). Product context: [Celiknaz.md](Celiknaz.md) §16.

The consumer app should look and feel like **HomeWhiz** (Arçelik's own smart home app), so a Beko user feels at home immediately. It runs at `#/ev` in the existing Vite + TypeScript frontend, mobile-first, Turkish UI, no UI framework, all styling through `tokens.css`.

## 1. What we know about HomeWhiz (verified) and what is still open

Verified from public sources (App Store listing, Beko press release May 2025):
- A user has **homes**, homes have **rooms**; new homes get a default room **"My Devices" / "Cihazlarım"**; each appliance has its own name.
- Appliances are controlled and monitored remotely; programs can be adjusted, saved as favourites, and recommended by a **wizard**.
- **Automation rules / scenarios**, **customisable notifications**, **energy and water tracking** (daily / weekly / monthly) with saving tips.
- Recent versions add a **News** section on the home screen and an **AI Assistant** with device insights.

Not verified: exact colours, typography, tab bar and card layout. **To match the design pixel-close, we need 4–6 screenshots of the real HomeWhiz app** (home screen with rooms, an AC detail, a washer detail, notifications, menu). Until then this spec uses the structure above with Beko styling (Beko blue `#0089CF`, white cards, light grey background, rounded corners, appliance images or icons).

## 2. Navigation: 3 tabs

Bottom tab bar, always visible, like HomeWhiz. The **Çeliknaz voice button** floats above the tab bar on every tab.

| Tab | Turkish label | Content |
|---|---|---|
| 1 | **Evim** | rooms and their Beko appliances (HomeWhiz home screen) |
| 2 | **Görevler** | household tasks for every member |
| 3 | **Asistan** | Çeliknaz conversation, insights, energy summary, learned habits |

Header on every tab: home name with switcher (one home in the demo), member avatars showing who is at home, notification bell with badge, profile icon → **Ayarlar** (priority mode, notification level, quiet hours, clothing, location sharing, "Öğrendiklerini sıfırla").

> Decided (2026-11-03): the three tabs are Evim, Görevler and Asistan.

## 3. Tab 1 — Evim (rooms → appliances)

```text
┌──────────────────────────────────────┐
│ Evim ▾            👩 👨 🧒     🔔2  👤 │  header: home, who is home, bell, profile
├──────────────────────────────────────┤
│ ☀ 31°C dışarıda · Tasarruf modu      │  status strip: weather + priority mode
├──────────────────────────────────────┤
│ [Tümü] [Salon] [Yatak O.] [Mutfak] … │  room chips, horizontal scroll
├──────────────────────────────────────┤
│ SALON                                │
│ ┌───────────────┐ ┌───────────────┐  │
│ │ ❄ Klima       │ │               │  │  appliance cards, 2 per row
│ │ Soğutma · 25° │ │               │  │
│ │ Oda 24.8° %55 │ │               │  │
│ │          (⏻)  │ │               │  │  quick power toggle
│ └───────────────┘ └───────────────┘  │
│ MUTFAK                               │
│ ┌───────────────┐ ┌───────────────┐  │
│ │ 🍽 Bulaşık M.  │ │ 🔥 Fırın       │  │
│ │ Eko 50° · 42dk│ │ Kapalı        │  │
│ │ ▓▓▓▓▓░░ 60%   │ │          (⏻)  │  │  program progress bar
│ └───────────────┘ └───────────────┘  │
│                                      │
│                        ( 🎤 )        │  floating Çeliknaz button
├──────────────────────────────────────┤
│   🏠 Evim     ✅ Görevler   ✨ Asistan │  tab bar
└──────────────────────────────────────┘
```

- Data: `GET /api/homes/me` (rooms + appliances + state); live updates from `DeviceHub` `DeviceStateChanged`.
- "Tümü" shows all rooms as sections; a room chip filters to that room.
- Card states: off (grey), running (blue accent + progress), finished (green "Bitti"), error (red). An appliance changed by Çeliknaz shows a small "✨ Çeliknaz ayarladı" tag for a while.
- Tapping a card → **appliance detail** (full screen, back arrow):
  - **Klima**: big setpoint dial with − / +, mode buttons (Soğutma, Isıtma, Nem alma, Fan, Otomatik, Eko), fan level, room temperature and humidity, today's kWh and TL, 24 h temperature/humidity chart, **sanal kumanda** button (sends commands with `source: remote`).
  - **Çamaşır / bulaşık makinesi**: program list, temperature, start / pause, remaining time, "En ucuz saatte başlat" (uses `find_cheapest_time`).
  - **Fırın**: mode, target temperature, timer, preheat status.
  - **Buzdolabı** (optional): temperatures, door alert, read-only.
- Room management (rename, move appliance) is out of scope; the mock home is seeded.

## 4. Tab 2 — Görevler (household tasks)

```text
┌──────────────────────────────────────┐
│ Görevler                     + Ekle  │
├──────────────────────────────────────┤
│ [Bana] [Herkes] [Tamamlanan]         │  filter chips
├──────────────────────────────────────┤
│ ┌──────────────────────────────────┐ │
│ │ 👕 Çamaşırları as        Mehmet  │ │  task card: icon, title, assignee
│ │ Çamaşır makinesi bitti · 5 dk önce│ │  origin + time
│ │ [Tamamlandı] [Devret] [Atla]     │ │  actions
│ └──────────────────────────────────┘ │
│ ┌──────────────────────────────────┐ │
│ │ 🍽 Bulaşığı boşalt        Zeynep  │ │
│ └──────────────────────────────────┘ │
├──────────────────────────────────────┤
│ Bu hafta kim ne yaptı                │
│ Ayşe ▓▓▓▓▓▓ 6   Mehmet ▓▓▓▓ 4  ...   │  fairness bar per member
├──────────────────────────────────────┤
│ ⚙ Görev kuralları                    │  rules: trigger → template → strategy
└──────────────────────────────────────┘
```

- Data: `GET /api/homes/me/tasks`, actions `POST /api/tasks/{id}/done|skip|reassign`; live `TaskAssigned`.
- "+ Ekle": title, optional assignee ("Çeliknaz dağıtsın" is the default).
- **Görev kuralları** page: list of rules ("Çamaşır makinesi bitince → Çamaşırları as → Adil dağıt"), on/off switch, strategy picker (Adil dağıt / Sırayla / Hep aynı kişi / Sor).

## 5. Tab 3 — Asistan (Çeliknaz)

```text
┌──────────────────────────────────────┐
│ ✨ Çeliknaz                           │
├──────────────────────────────────────┤
│ Bugün için öneriler                  │  insight cards (from learned patterns)
│ ┌──────────────────────────────────┐ │
│ │ Hafta içi 18:30'da salonu         │ │
│ │ açıyorsunuz. Otomatik yapayım mı? │ │
│ │            [Evet] [Hayır]         │ │
│ └──────────────────────────────────┘ │
│ Bu hafta: 4.2 kWh · 18 TL tasarruf   │  energy summary
├──────────────────────────────────────┤
│ Sohbet                               │
│  Sen: Ev soğudu, biraz ısıt          │
│  Çeliknaz: Salonu 25°'ye aldım, ...  │  messages with small action chips
│     [❄ Salon klima → 25°]            │  what the assistant changed
├──────────────────────────────────────┤
│ [ Yazın...                  ] ( 🎤 ) │  text input + voice
└──────────────────────────────────────┘
```

- Chat uses the platform chat API and `RunHub` streaming; voice uses Web Speech (`tr-TR`) and reads answers aloud.
- Each assistant action shows as a chip; tapping it opens the appliance detail.
- "Çeliknaz neler öğrendi?" link → learned preferences and routines in plain Turkish, accept / reject, reset.

## 6. Shared elements

- **Floating voice button** (all tabs): press → listening overlay with live transcript → sends to Çeliknaz → short answer bubble + spoken reply, without leaving the current tab. The full history is on the Asistan tab.
- **Comfort question** sheet ("Nasıl, rahat mı? 🥶 Üşüyorum · 🙂 İyi · 🥵 Sıcak"), triggered by `ComfortCheckRequested`, follows the member's notification level.
- **Notifications**: bell → list; toasts from `NotificationCreated`.
- **Settings** (from the profile icon): priority mode as 3 cards (Tasarruf / Dengeli / Konfor) with estimated monthly TL, notification level, quiet hours, clothing chip (İnce / Normal / Kalın), location sharing, demo member switcher ("Ayşe olarak görüntüle").

## 7. Layout and styling rules

- Mobile-first at 375 px; on desktop the app is centred in a phone-width column (max ≈ 430 px) so the demo looks like the phone app. The Arçelik dashboard and platform pages stay full-width and are **not** part of these 3 tabs.
- Colours, spacing, radius, shadows and type only through `tokens.css`. Add tokens for appliance states (off, running, finished, error) and a light page background. Beko blue `#0089CF` for primary actions and the active tab.
- Appliance icons: inline SVGs in the repo (no external icon CDN).
- All strings in `frontend/src/i18n/tr.ts`.
- Accessibility: tab bar and cards are buttons with labels; the voice button has a text fallback; colour is never the only state signal (text + icon too).

## 8. Implementation order (frontend)

1. Tab bar + header + `#/ev` routing (Evim / Görevler / Asistan)
2. Evim: room chips, appliance cards, live updates
3. Klima detail + sanal kumanda
4. Asistan chat + floating voice button
5. Görevler list + actions
6. Washer / dishwasher / oven details, settings, comfort sheet, notifications, rules page
